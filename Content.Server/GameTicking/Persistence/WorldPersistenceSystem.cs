using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;
using Content.Server.Procedural;
using Content.Server.Salvage.Expeditions;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.Gravity;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Mind;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Content.Server.GameTicking.Persistence;

/// <summary>
/// Saves the entire game world (every map, grid, mob, player, mind, station, etc.) to a single file and loads it back
/// when the server starts, so the world carries on where it left off.
/// </summary>
/// <remarks>
/// The world is saved when the server shuts down, when a round restarts, and periodically as an autosave.
/// Game rules and transient audio entities are not saved; game rules are re-created from the game preset on load.
/// </remarks>
public sealed partial class WorldPersistenceSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IResourceManager _res = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private Content.Shared.Gravity.SharedGravitySystem _gravity = default!;
    [Dependency] private Content.Shared.DeviceNetwork.Systems.DeviceNetworkSystem _deviceNetwork = default!;
    [Dependency] private Robust.Server.Player.IPlayerManager _players = default!;
    [Dependency] private IDependencyCollection _dependency = default!;
    [Dependency] private Content.Server.Shuttles.Systems.StationAnchorSystem _stationAnchor = default!;
    [Dependency] private PullingSystem _pulling = default!;
    [Dependency] private Content.Server.Salvage.SalvageSystem _salvage = default!;
    [Dependency] private SharedAudioSystem _audio = default!;

    private bool _enabled;
    private ResPath _savePath;
    private TimeSpan _autosaveInterval;
    private TimeSpan _nextAutosave;

    /// <summary>
    /// True while <see cref="SaveWorld"/> is serializing, so we only filter entities out of our own saves.
    /// </summary>
    private bool _saving;

    /// <summary>
    /// FTL sound references cleared for the duration of a save (see <see cref="OnBeforeSerialization"/>).
    /// </summary>
    private readonly List<(FTLComponent Ftl, EntityUid? Startup, EntityUid? Travel)> _hiddenFtlSounds = new();
    private readonly List<(Content.Server.Salvage.Magnet.SalvageMagnetDataComponent Data, List<EntityUid> Grids)> _hiddenMagnetGrids = new();

    /// <summary>
    /// A save that is still being written to disk in the background.
    /// </summary>
    private Task<bool>? _pendingWrite;

    /// <summary>
    /// Whether world persistence is turned on (<see cref="CCVars.UsePersistence"/>).
    /// </summary>
    public bool Enabled => _enabled;

    /// <summary>
    /// The save file, relative to the server's data folder.
    /// </summary>
    public ResPath SavePath => _savePath;

    /// <summary>
    /// Where autosaves go (e.g. <c>World_Autosave.yml</c>), so they never replace the main save.
    /// </summary>
    public ResPath AutosavePath => WithSuffix("_Autosave");

    /// <summary>
    /// A copy of the main save made once when the server starts (e.g. <c>World_Backup.yml</c>).
    /// </summary>
    public ResPath BackupPath => WithSuffix("_Backup");

    /// <summary>
    /// Whether the start-of-server backup has been made yet.
    /// </summary>
    private bool _startupBackupDone;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BeforeSerializationEvent>(OnBeforeSerialization);

        Subs.CVar(_cfg, CCVars.UsePersistence, v => _enabled = v, true);
        Subs.CVar(_cfg, CCVars.PersistenceWorldFile, v => _savePath = new ResPath(v).ToRootedPath(), true);
        Subs.CVar(_cfg, CCVars.PersistenceAutosaveInterval, v =>
        {
            _autosaveInterval = TimeSpan.FromSeconds(Math.Max(0f, v));
            _nextAutosave = _timing.CurTime + _autosaveInterval;
        }, true);

        // Runs during server startup, before any player can connect (they get the clock when they connect).
        ContinueSavedGameClock();
    }

    /// <summary>
    /// Key in the save file's <c>meta</c> section holding the game time (<see cref="IGameTiming.CurTime"/>, in ticks
    /// of 100ns) when the world was saved.
    /// </summary>
    public const string GameTimeMetaKey = "persistenceGameTime";

    /// <summary>
    /// Makes the game clock carry on from the time the world was saved, instead of restarting at zero.
    /// </summary>
    /// <remarks>
    /// Lots of components store absolute game times (cooldowns, next updates, effect end times) without converting
    /// them for saving. After a reboot those would be hours in the future (e.g. a dispenser ignoring clicks until its
    /// cooldown "ends"). Continuing the clock keeps every saved time valid, including in content added later.
    /// Must run before the world is loaded and before any player connects.
    /// </remarks>
    /// <returns>True if the clock was moved forward.</returns>
    public bool ContinueSavedGameClock()
    {
        if (!_enabled || !SaveExists() || TryReadSavedGameTime(_savePath) is not { } savedTime)
            return false;

        if (savedTime <= _timing.CurTime)
            return false;

        Log.Info($"Continuing the game clock from the world save: {_timing.CurTime} -> {savedTime}.");
        _timing.TimeBase = (savedTime, _timing.CurTick);
        _nextAutosave = _timing.CurTime + _autosaveInterval;
        return true;
    }

    /// <summary>
    /// Reads the saved game time from the top of a save file, without loading the whole file.
    /// </summary>
    private TimeSpan? TryReadSavedGameTime(ResPath path)
    {
        try
        {
            using var reader = _res.UserData.OpenText(path);
            for (var i = 0; i < 30 && reader.ReadLine() is { } line; i++)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(GameTimeMetaKey + ":"))
                    continue;

                if (long.TryParse(trimmed[(GameTimeMetaKey.Length + 1)..].Trim(), out var ticks) && ticks > 0)
                    return TimeSpan.FromTicks(ticks);
            }
        }
        catch (Exception e)
        {
            Log.Warning($"Couldn't read the saved game time from {path}: {e.Message}");
        }

        return null;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Game time, not real time: the server pauses while empty (game.auto_pause_empty), and with real time the
        // first player to join after a long pause would immediately trigger an autosave (and its freeze).
        if (!_enabled || _autosaveInterval <= TimeSpan.Zero || _timing.CurTime < _nextAutosave)
            return;

        _nextAutosave = _timing.CurTime + _autosaveInterval;

        // Only autosave while someone is playing; an empty server has nothing new worth the save's freeze.
        if (_players.PlayerCount == 0)
            return;

        if (CanSave())
            SaveWorld("autosave", background: true, autosave: true);
    }

    /// <summary>
    /// Whether there is currently a world worth saving. We never save while sitting in the lobby with no maps loaded,
    /// because that would overwrite a good save with an empty world.
    /// </summary>
    public bool CanSave()
    {
        if (!_enabled)
            return false;

        if (_ticker.RunLevel != GameRunLevel.InRound && _ticker.RunLevel != GameRunLevel.PostRound)
            return false;

        return _map.MapExists(_ticker.DefaultMap);
    }

    /// <summary>
    /// Whether a world save exists on disk.
    /// </summary>
    public bool SaveExists()
    {
        return _res.UserData.Exists(_savePath);
    }

    /// <summary>
    /// Saves every entity in the game to <see cref="SavePath"/> (or <see cref="AutosavePath"/> for autosaves).
    /// The file is written to a temporary file first and then swapped in, so a crash mid-save can't corrupt the
    /// previous save. For the main save, the previous one is kept as a <c>.bak</c> file.
    /// </summary>
    /// <param name="reason">Shown in the logs.</param>
    /// <param name="force">Save even if <see cref="CanSave"/> says there's nothing worth saving.</param>
    /// <param name="background">
    /// Write the file on a background thread. Capturing the world still happens on the game thread (the server
    /// pauses for that part); only writing out the YAML happens in the background.
    /// </param>
    /// <param name="autosave">Write to <see cref="AutosavePath"/> instead of the main save, without a <c>.bak</c>.</param>
    public bool SaveWorld(string reason, bool force = false, bool background = false, bool autosave = false)
    {
        var target = autosave ? AutosavePath : _savePath;

        if (!force && !CanSave())
        {
            Log.Info($"Skipping world save ({reason}): no round in progress.");
            return false;
        }

        // Never have two saves writing the same files at once.
        WaitForPendingWrite();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Log.Info($"Saving world to {target} ({reason})...");

        if (!TrySerializeWorld(out var data))
        {
            Log.Error($"Failed to save world ({reason}). The previous save was left untouched.");
            return false;
        }

        Log.Info($"Captured world state in {stopwatch.Elapsed.TotalSeconds:N2}s, writing to disk...");

        var keepBackup = !autosave;
        if (!background)
            return WriteSave(data, reason, stopwatch, target, keepBackup);

        _pendingWrite = Task.Run(() => WriteSave(data, reason, stopwatch, target, keepBackup));
        return true;
    }

    /// <summary>
    /// Run right before the shutdown / round restart save: brings players home from salvage expeditions (which aren't
    /// saved) and drops held items on the floor (they don't survive a save in hands).
    /// </summary>
    public void PrepareForFinalSave()
    {
        // Expeditions and magnet salvage aren't saved, so bring everyone on them back home first.
        var expeditionFix = EntityManager.System<PersistentExpeditionFixSystem>();
        expeditionFix.BringPlayersHomeFromExpeditions();
        expeditionFix.BringPlayersHomeFromMagnetSalvage();
        DropPlayersHeldItems();
    }

    /// <summary>
    /// Drops whatever players are holding onto the floor at their feet. Items held in hands don't survive a world
    /// save, so this is done right before the shutdown save to keep them in the world.
    /// </summary>
    public void DropPlayersHeldItems()
    {
        var dropped = 0;
        var query = EntityQueryEnumerator<MindComponent>();
        while (query.MoveNext(out _, out var mind))
        {
            if (mind.OriginalOwnerUserId == null || mind.OwnedEntity is not { } body)
                continue;

            if (!TryComp<HandsComponent>(body, out var hands))
                continue;

            foreach (var held in _hands.EnumerateHeld((body, hands)))
            {
                if (!HasComp<VirtualItemComponent>(held))
                    dropped++;
            }

            _hands.DropAll((body, hands), checkActionBlocker: false, doDropInteraction: false);
        }

        if (dropped > 0)
            Log.Info($"Dropped {dropped} held item(s) onto the floor before saving.");
    }

    /// <summary>
    /// Blocks until a background save (see <see cref="SaveWorld"/>) has finished writing.
    /// </summary>
    public void WaitForPendingWrite()
    {
        var pending = _pendingWrite;
        if (pending == null)
            return;

        try
        {
            pending.Wait();
        }
        catch (Exception e)
        {
            Log.Error($"Background world save failed: {e}");
        }

        _pendingWrite = null;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        WaitForPendingWrite();
    }

    /// <summary>
    /// Captures every entity into YAML data. Must run on the game thread.
    /// </summary>
    private bool TrySerializeWorld([NotNullWhen(true)] out MappingDataNode? data)
    {
        data = null;
        MarkDefaultMap();

        var options = SerializationOptions.Default with
        {
            // Entities we deliberately skip (game rules, sounds) may still be referenced by saved entities.
            // Those references are simply cleared, the same as if the entity had been deleted.
            MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
            // One broken entity shouldn't stop the rest of the world from being saved.
            EntityExceptionBehaviour = EntityExceptionBehaviour.IgnoreEntityAndChildren,
        };

        // Mobs, player bodies, organs, borgs, mechs, ghosts etc. are marked "save: false" so mappers don't save them
        // into map files. A world save has to include them, so make every prototype savable just for this save.
        var unsavable = new List<EntityPrototype>();
        foreach (var proto in _proto.EnumeratePrototypes<EntityPrototype>())
        {
            if (proto.MapSavable)
                continue;

            proto.MapSavable = true;
            unsavable.Add(proto);
        }

        _saving = true;
        try
        {
            data = SerializeAllEntities(options);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Exception while saving world: {e}");
            return false;
        }
        finally
        {
            _saving = false;

            foreach (var (ftl, startup, travel) in _hiddenFtlSounds)
            {
                ftl.StartupStream = startup;
                ftl.TravelStream = travel;
            }

            _hiddenFtlSounds.Clear();

            foreach (var (magnetData, grids) in _hiddenMagnetGrids)
            {
                magnetData.ActiveEntities = grids;
            }

            _hiddenMagnetGrids.Clear();

            foreach (var proto in unsavable)
            {
                proto.MapSavable = false;
            }
        }
    }

    /// <summary>
    /// Same as <see cref="MapLoaderSystem.TrySerializeAllEntities"/>, but survives entities that fail to serialize.
    /// </summary>
    /// <remarks>
    /// The engine is meant to skip an entity that throws while being saved, but it doesn't fully clean up after
    /// entities without a prototype (maps, most grids). That crashes the whole save
    /// (KeyNotFoundException in EntitySerializer.WriteEntitySection), losing everything since the last save.
    /// So if anything fails, we save again from scratch, leaving out the failed entities and everything on them.
    /// </remarks>
    private MappingDataNode SerializeAllEntities(SerializationOptions options)
    {
        options.Category = FileCategory.Save;

        var serializer = RunSerializer(options, null);
        if (serializer.ErroringEntities.Count == 0)
            return Finish(serializer);

        var failed = serializer.ErroringEntities.ToHashSet();
        Log.Error($"{failed.Count} entities failed to save (see the errors above). " +
                  "Saving again without them and anything on them.");

        serializer = RunSerializer(options, failed);
        if (serializer.ErroringEntities.Count > 0)
            throw new Exception($"{serializer.ErroringEntities.Count} entities still failed to save on the second try.");

        return Finish(serializer);

        MappingDataNode Finish(EntitySerializer s)
        {
            var data = s.Write();
            StripDeadReferences(data);
            // Remember the game time, so the clock can carry on from it after a reboot (see ContinueSavedGameClock).
            data.Get<MappingDataNode>("meta").Add(GameTimeMetaKey, _timing.CurTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            RaiseLocalEvent(new AfterSerializationEvent(s.EntityData.Values.Select(e => e.Uid).ToHashSet(), data, FileCategory.Save));
            return data;
        }
    }

    /// <summary>
    /// Links to entities that aren't saved (mostly sounds, which are deliberately left out) get written as "invalid",
    /// and every one of them logs an error when the save is loaded. Drop those fields so they load as their default
    /// (usually "nothing") instead. E.g. a switched-on Newton's cradle remembers its looping click sound.
    /// </summary>
    private void StripDeadReferences(MappingDataNode data)
    {
        if (!data.TryGet<SequenceDataNode>("entities", out var groups))
            return;

        var stripped = 0;
        foreach (var group in groups.OfType<MappingDataNode>())
        {
            if (!group.TryGet<SequenceDataNode>("entities", out var entities))
                continue;

            foreach (var entity in entities.OfType<MappingDataNode>())
            {
                if (!entity.TryGet<SequenceDataNode>("components", out var components))
                    continue;

                foreach (var comp in components.OfType<MappingDataNode>())
                {
                    // A map's transform has no parent, and that is written as "invalid" on purpose.
                    comp.TryGet<ValueDataNode>("type", out var type);
                    if (type?.Value == "Transform")
                        continue;

                    var dead = comp
                        .Where(kv => kv.Value is ValueDataNode { Value: "invalid" })
                        .Select(kv => kv.Key)
                        .ToList();

                    foreach (var key in dead)
                    {
                        comp.Remove(key);
                        stripped++;
                    }

                    // Lists of entities (e.g. what something is linked to): drop the ones that aren't saved.
                    foreach (var (_, value) in comp)
                    {
                        if (value is not SequenceDataNode list)
                            continue;

                        for (var i = list.Sequence.Count - 1; i >= 0; i--)
                        {
                            if (list.Sequence[i] is ValueDataNode { Value: "invalid" })
                            {
                                list.RemoveAt(i);
                                stripped++;
                            }
                        }
                    }

                    switch (type?.Value)
                    {
                        // A physics joint (docking, welds, tethers...) to something that isn't saved can't be restored;
                        // leave the whole joint out.
                        case "Joint" when comp.TryGet<MappingDataNode>("joints", out var joints):
                        {
                            var deadJoints = joints
                                .Where(kv => kv.Value is MappingDataNode joint &&
                                             (!joint.TryGet<ValueDataNode>("bodyA", out var a) || a.Value == "invalid" ||
                                              !joint.TryGet<ValueDataNode>("bodyB", out var b) || b.Value == "invalid"))
                                .Select(kv => kv.Key)
                                .ToList();

                            foreach (var key in deadJoints)
                            {
                                joints.Remove(key);
                                stripped++;
                            }

                            break;
                        }
                        // A dock whose partner isn't saved isn't docked any more; forget its joint too.
                        case "Docking" when dead.Contains("dockedWith"):
                            comp.Remove("dockJointId");
                            break;
                    }
                }
            }
        }

        if (stripped > 0)
            Log.Debug($"Left {stripped} link(s) to unsaved entities out of the save.");
    }

    /// <summary>
    /// Serializes every savable entity, except <paramref name="exclude"/> and their children.
    /// </summary>
    private EntitySerializer RunSerializer(SerializationOptions options, HashSet<EntityUid>? exclude)
    {
        var entities = EntityManager.GetEntities().ToHashSet();
        var mapIds = new HashSet<MapId>();
        var mapQuery = AllEntityQuery<MapComponent>();
        while (mapQuery.MoveNext(out var map))
        {
            mapIds.Add(map.MapId);
        }

        var serializer = new EntitySerializer(_dependency, options);

        // Drop entities that can't be saved at all (and their children), like the engine does.
        var toRemove = new Queue<EntityUid>();
        foreach (var uid in entities)
        {
            if (exclude?.Contains(uid) == true || !serializer.IsSerializable(uid))
                toRemove.Enqueue(uid);
        }

        while (toRemove.TryDequeue(out var uid))
        {
            if (!entities.Remove(uid))
                continue;

            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                toRemove.Enqueue(child);
            }
        }

        RaiseLocalEvent(new BeforeSerializationEvent(entities, mapIds, FileCategory.Save));
        serializer.SerializeEntities(entities);
        return serializer;
    }

    /// <summary>
    /// Writes captured world data to disk. Safe to run on a background thread.
    /// </summary>
    private bool WriteSave(
        MappingDataNode data,
        string reason,
        System.Diagnostics.Stopwatch stopwatch,
        ResPath target,
        bool keepBackup)
    {
        var tmpPath = new ResPath(target + ".tmp");
        var bakPath = new ResPath(target + ".bak");

        try
        {
            _res.UserData.CreateDir(target.Directory);
            using (var writer = _res.UserData.OpenWriteText(tmpPath))
            {
                var document = new YamlDocument(data.ToYaml());
                var stream = new YamlStream { document };
                stream.Save(new YamlMappingFix(new Emitter(writer)), false);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to write world save ({reason}). The previous save was left untouched: {e}");
            TryDelete(tmpPath);
            return false;
        }

        try
        {
            if (_res.UserData.Exists(target))
            {
                if (keepBackup)
                {
                    TryDelete(bakPath);
                    _res.UserData.Rename(target, bakPath);
                }
                else
                {
                    _res.UserData.Delete(target);
                }
            }

            _res.UserData.Rename(tmpPath, target);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to move world save into place: {e}");
            return false;
        }

        Log.Info($"World saved to {target} in {stopwatch.Elapsed.TotalSeconds:N2}s ({reason}).");
        return true;
    }

    /// <summary>
    /// Loads the world save, if one exists.
    /// </summary>
    /// <param name="defaultMap">The map that was the round's main map when the world was saved.</param>
    public bool TryLoadWorld([NotNullWhen(true)] out LoadResult? result, out MapId defaultMap)
    {
        result = null;
        defaultMap = MapId.Nullspace;

        if (!_enabled)
            return false;

        // Make sure an autosave that's still being written has finished before reading the file.
        WaitForPendingWrite();

        if (!SaveExists())
            return false;

        MakeStartupBackup();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Log.Info($"Loading world from {_savePath}...");

        var options = new MapLoadOptions
        {
            ExpectedCategory = FileCategory.Save,
        };

        bool loaded;
        try
        {
            loaded = _loader.TryLoadGeneric(_savePath, out result, options);
        }
        catch (Exception e)
        {
            Log.Error($"Exception while loading world save {_savePath}: {e}");
            loaded = false;
        }

        if (!loaded || result == null)
        {
            SetAsideBrokenSave();
            result = null;
            return false;
        }

        defaultMap = FindDefaultMap(result);
        if (defaultMap == MapId.Nullspace)
        {
            _loader.Delete(result);
            SetAsideBrokenSave();
            result = null;
            return false;
        }

        CloseSavedUis(result);
        ReconnectDeviceNetworks(result);
        RefreshWeightlessness(result);
        ReapplyStationAnchors(result);
        RestartLoopingItemSounds(result);

        Log.Info($"Loaded world from {_savePath} in {stopwatch.Elapsed.TotalSeconds:N2}s (game time {_timing.CurTime}): " +
                 $"{result.Maps.Count} maps, {result.Grids.Count} grids, {result.Entities.Count} entities.");
        return true;
    }

    /// <summary>
    /// The save remembers which UIs were open (and for whom). Nobody actually has them open after a restart, and
    /// leftover "open" state makes the first click on e.g. a dispenser close the UI instead of opening it.
    /// </summary>
    private void CloseSavedUis(LoadResult result)
    {
        foreach (var uid in result.Entities)
        {
            if (TryComp<UserInterfaceComponent>(uid, out var ui) && ui.Actors.Count > 0)
                _ui.CloseUis((uid, ui));
        }
    }

    /// <summary>
    /// Whether something is weightless isn't saved, defaults to "weightless", and is normally only worked out on MapInit
    /// (which loaded entities never get) or when it changes grid. Work it out again once the whole world is loaded.
    /// </summary>
    private void RefreshWeightlessness(LoadResult result)
    {
        foreach (var uid in result.Entities)
        {
            if (TryComp<GravityAffectedComponent>(uid, out var gravityAffected))
                _gravity.RefreshWeightless((uid, gravityAffected));
        }
    }

    /// <summary>
    /// Sounds aren't saved, so a switched-on item with a looping sound (e.g. a swinging Newton's cradle) would go
    /// silent after a load. Start its loop again.
    /// </summary>
    private void RestartLoopingItemSounds(LoadResult result)
    {
        foreach (var uid in result.Entities)
        {
            if (!TryComp<ItemToggleActiveSoundComponent>(uid, out var sound) ||
                sound.ActiveSound == null ||
                sound.PlayingStream != null ||
                !TryComp<ItemToggleComponent>(uid, out var toggle) ||
                !toggle.Activated)
            {
                continue;
            }

            var stream = _audio.PlayPvs(sound.ActiveSound, uid, sound.ActiveSound.Params.WithLoop(true));
            sound.PlayingStream = stream?.Entity;
        }
    }

    /// <summary>
    /// Switched-on station anchors only pin their grid on MapInit, and the pinned state itself isn't saved, so after a
    /// load anchored stations would drift again.
    /// </summary>
    private void ReapplyStationAnchors(LoadResult result)
    {
        foreach (var uid in result.Entities)
        {
            _stationAnchor.ReapplyLoadedAnchor(uid);
        }
    }

    /// <summary>
    /// Devices only join their device network on MapInit, which loaded entities never get, and the networks themselves
    /// aren't saved. Without this, nothing sent over a device network works after a load: e.g. a signal button no
    /// longer opens its blast doors even though the link is still there. Devices keep their saved addresses.
    /// </summary>
    private void ReconnectDeviceNetworks(LoadResult result)
    {
        var connected = 0;
        foreach (var uid in result.Entities)
        {
            if (!TryComp<DeviceNetworkComponent>(uid, out var device) || !device.AutoConnect)
                continue;

            // Entities that haven't been map-initialized yet will connect on their own when they are.
            if (MetaData(uid).EntityLifeStage < EntityLifeStage.MapInitialized)
                continue;

            if (_deviceNetwork.ConnectDevice((uid, device)))
                connected++;
        }

        Log.Info($"Reconnected {connected} device(s) to their device networks.");
    }

    /// <summary>
    /// Renames a save that couldn't be loaded, so the next save doesn't overwrite it and it can be inspected or fixed.
    /// </summary>
    private void SetAsideBrokenSave()
    {
        var failedPath = new ResPath($"{_savePath}.failed-{DateTime.Now:yyyyMMdd-HHmmss}");
        try
        {
            _res.UserData.Rename(_savePath, failedPath);
            Log.Error($"Could not load world save. It was moved to {failedPath} so it won't be overwritten. " +
                      $"Starting from the starting map instead. The previous save may still be at {_savePath}.bak");
        }
        catch (Exception e)
        {
            Log.Error($"Could not load world save, and failed to move it out of the way: {e}");
        }
    }

    /// <summary>
    /// Tags the round's main map, so we know which map to treat as the main map after loading.
    /// </summary>
    private void MarkDefaultMap()
    {
        var query = AllEntityQuery<WorldPersistenceDefaultMapComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            RemComp<WorldPersistenceDefaultMapComponent>(uid);
        }

        if (_map.TryGetMap(_ticker.DefaultMap, out var mapUid))
            EnsureComp<WorldPersistenceDefaultMapComponent>(mapUid.Value);
    }

    private MapId FindDefaultMap(LoadResult result)
    {
        Entity<MapComponent>? fallback = null;
        foreach (var map in result.Maps)
        {
            if (HasComp<WorldPersistenceDefaultMapComponent>(map))
                return map.Comp.MapId;

            // Saves without the marker: just use the first map.
            fallback ??= map;
        }

        return fallback?.Comp.MapId ?? MapId.Nullspace;
    }

    private void OnBeforeSerialization(BeforeSerializationEvent ev)
    {
        if (!_saving || ev.Category != FileCategory.Save)
            return;

        // Things that are re-created on load instead of being saved:
        // - Game rules are started fresh from the game preset every time the world is loaded.
        // - Sounds and audio presets are short lived / re-created by the audio system.
        // - Dungeon atlas maps are a cache of map files; the dungeon system deletes and re-creates them every round.
        // - Salvage expeditions keep most of their state in runtime-only fields, so they don't survive a load
        //   (see PersistentExpeditionFixSystem).
        // - Expedition zombies only belong on their expedition, so one that followed the crew home isn't kept.
        // - Salvage magnet salvage (its holding maps and whatever it has pulled in) is temporary like expeditions; the
        //   magnet starts fresh after a load (see PersistentExpeditionFixSystem).
        var toRemove = new Queue<EntityUid>();
        var magnetData = AllEntityQuery<Content.Server.Salvage.Magnet.SalvageMagnetDataComponent>();
        while (magnetData.MoveNext(out var magnetDataComp))
        {
            foreach (var grid in _salvage.GetActiveMagnetGrids(magnetDataComp))
            {
                if (ev.Entities.Contains(grid))
                    toRemove.Enqueue(grid);
            }

            // The list points at grids that aren't saved; keep it out of the save (restored right after).
            if (magnetDataComp.ActiveEntities != null)
            {
                _hiddenMagnetGrids.Add((magnetDataComp, magnetDataComp.ActiveEntities));
                magnetDataComp.ActiveEntities = null;
            }
        }

        foreach (var uid in ev.Entities)
        {
            if (HasComp<GameRuleComponent>(uid) ||
                HasComp<AudioComponent>(uid) ||
                HasComp<AudioPresetComponent>(uid) ||
                HasComp<AudioAuxiliaryComponent>(uid) ||
                HasComp<AudioEffectComponent>(uid) ||
                HasComp<DungeonAtlasTemplateComponent>(uid) ||
                HasComp<SalvageExpeditionComponent>(uid) ||
                HasComp<Content.Server.Salvage.Magnet.SalvageMagnetHoldingMapComponent>(uid) ||
                HasComp<Content.Server.Zombies.ExpeditionZombieComponent>(uid) ||
                HasComp<Content.Server.Zombies.ZombifyOnSpawnComponent>(uid))
            {
                toRemove.Enqueue(uid);
            }
        }

        while (toRemove.TryDequeue(out var uid))
        {
            if (!ev.Entities.Remove(uid))
                continue;

            // Something being dragged keeps a link (and a physics joint) to whoever drags it. If only one side is saved,
            // the save can't be loaded, so let go first.
            if (TryComp<PullableComponent>(uid, out var pullable) && pullable.Puller != null)
                _pulling.TryStopPull(uid, pullable);
            if (TryComp<PullerComponent>(uid, out var puller) && puller.Pulling is { } pulled &&
                TryComp<PullableComponent>(pulled, out var pulledComp))
            {
                _pulling.TryStopPull(pulled, pulledComp);
            }

            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                toRemove.Enqueue(child);
            }
        }

        // Maps and grids keep some of their data in "chunk" entities that live in null-space rather than as children.
        // Drop the chunks whose map/grid isn't being saved, so they don't point at entities missing from the save.
        var chunks = new List<EntityUid>();
        foreach (var uid in ev.Entities)
        {
            if (TryComp<ChunkEntityComponent>(uid, out var chunk) && !ev.Entities.Contains(chunk.Root))
                chunks.Add(uid);
        }

        foreach (var uid in chunks)
        {
            ev.Entities.Remove(uid);
        }

        // Coordinates disks pointing at something that isn't being saved (e.g. a finished salvage expedition) would be
        // saved as an invalid reference and log an error on load. They'd be blank after loading anyway, so blank them now.
        var disks = AllEntityQuery<ShuttleDestinationCoordinatesComponent>();
        while (disks.MoveNext(out var disk, out var diskComp))
        {
            if (diskComp.Destination is not { } destination || ev.Entities.Contains(destination))
                continue;

            diskComp.Destination = null;
            Dirty(disk, diskComp);
        }

        // A shuttle mid-FTL points at its sounds, which aren't saved. Leave those references out of the save (they'd
        // log errors on load) but keep them in the running game, so the sounds can still be stopped on arrival.
        var ftls = AllEntityQuery<FTLComponent>();
        while (ftls.MoveNext(out var ftl))
        {
            if (ftl.StartupStream == null && ftl.TravelStream == null)
                continue;

            _hiddenFtlSounds.Add((ftl, ftl.StartupStream, ftl.TravelStream));
            ftl.StartupStream = null;
            ftl.TravelStream = null;
        }
    }

    /// <summary>
    /// The first time the world is loaded after the server starts, copy the save to <see cref="BackupPath"/>, so
    /// there's always a copy of the world as it was when the server came up.
    /// </summary>
    private void MakeStartupBackup()
    {
        if (_startupBackupDone)
            return;

        _startupBackupDone = true;
        try
        {
            using (var source = _res.UserData.OpenRead(_savePath))
            using (var copy = _res.UserData.Open(BackupPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(copy);
            }

            Log.Info($"Backed up {_savePath} to {BackupPath}.");
        }
        catch (Exception e)
        {
            Log.Error($"Failed to back up {_savePath} to {BackupPath}: {e}");
        }
    }

    /// <summary>
    /// The main save path with a suffix added to the file name, e.g. <c>World.yml</c> -> <c>World_Backup.yml</c>.
    /// </summary>
    private ResPath WithSuffix(string suffix)
    {
        var extension = _savePath.Extension;
        var name = _savePath.FilenameWithoutExtension + suffix + (extension.Length > 0 ? "." + extension : "");
        return _savePath.WithName(name);
    }

    private void TryDelete(ResPath path)
    {
        try
        {
            if (_res.UserData.Exists(path))
                _res.UserData.Delete(path);
        }
        catch (Exception e)
        {
            Log.Warning($"Failed to delete {path}: {e.Message}");
        }
    }
}
