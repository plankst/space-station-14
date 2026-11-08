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
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Mind;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Audio.Components;
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
    [Dependency] private Content.Shared.DeviceNetwork.Systems.DeviceNetworkSystem _deviceNetwork = default!;
    [Dependency] private Robust.Server.Player.IPlayerManager _players = default!;
    [Dependency] private IDependencyCollection _dependency = default!;

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
        // Expeditions aren't saved, so bring everyone on one back home first.
        EntityManager.System<PersistentExpeditionFixSystem>().BringPlayersHomeFromExpeditions();
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
            RaiseLocalEvent(new AfterSerializationEvent(s.EntityData.Values.Select(e => e.Uid).ToHashSet(), data, FileCategory.Save));
            return data;
        }
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

        Log.Info($"Loaded world from {_savePath} in {stopwatch.Elapsed.TotalSeconds:N2}s: " +
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
        var toRemove = new Queue<EntityUid>();
        foreach (var uid in ev.Entities)
        {
            if (HasComp<GameRuleComponent>(uid) ||
                HasComp<AudioComponent>(uid) ||
                HasComp<AudioPresetComponent>(uid) ||
                HasComp<AudioAuxiliaryComponent>(uid) ||
                HasComp<AudioEffectComponent>(uid) ||
                HasComp<DungeonAtlasTemplateComponent>(uid) ||
                HasComp<SalvageExpeditionComponent>(uid))
            {
                toRemove.Enqueue(uid);
            }
        }

        while (toRemove.TryDequeue(out var uid))
        {
            if (!ev.Entities.Remove(uid))
                continue;

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
