using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;
using Content.Server.Procedural;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
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

    private bool _enabled;
    private ResPath _savePath;
    private TimeSpan _autosaveInterval;
    private TimeSpan _nextAutosave;

    /// <summary>
    /// True while <see cref="SaveWorld"/> is serializing, so we only filter entities out of our own saves.
    /// </summary>
    private bool _saving;

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

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BeforeSerializationEvent>(OnBeforeSerialization);

        Subs.CVar(_cfg, CCVars.UsePersistence, v => _enabled = v, true);
        Subs.CVar(_cfg, CCVars.PersistenceWorldFile, v => _savePath = new ResPath(v).ToRootedPath(), true);
        Subs.CVar(_cfg, CCVars.PersistenceAutosaveInterval, v =>
        {
            _autosaveInterval = TimeSpan.FromSeconds(Math.Max(0f, v));
            _nextAutosave = _timing.RealTime + _autosaveInterval;
        }, true);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_enabled || _autosaveInterval <= TimeSpan.Zero || _timing.RealTime < _nextAutosave)
            return;

        _nextAutosave = _timing.RealTime + _autosaveInterval;

        if (CanSave())
            SaveWorld("autosave", background: true);
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
    /// Saves every entity in the game to <see cref="SavePath"/>.
    /// The file is written to a temporary file first and then swapped in, so a crash mid-save can't corrupt the
    /// previous save. The previous save is kept as a <c>.bak</c> file.
    /// </summary>
    /// <param name="reason">Shown in the logs.</param>
    /// <param name="force">Save even if <see cref="CanSave"/> says there's nothing worth saving.</param>
    /// <param name="background">
    /// Write the file on a background thread. The world is still captured immediately on the game thread; only the
    /// slow part (writing out the YAML) happens in the background, so the server doesn't freeze during autosaves.
    /// </param>
    public bool SaveWorld(string reason, bool force = false, bool background = false)
    {
        if (!force && !CanSave())
        {
            Log.Info($"Skipping world save ({reason}): no round in progress.");
            return false;
        }

        // Never have two saves writing the same files at once.
        WaitForPendingWrite();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Log.Info($"Saving world to {_savePath} ({reason})...");

        if (!TrySerializeWorld(out var data))
        {
            Log.Error($"Failed to save world ({reason}). The previous save was left untouched.");
            return false;
        }

        Log.Info($"Captured world state in {stopwatch.Elapsed.TotalSeconds:N2}s, writing to disk...");

        if (!background)
            return WriteSave(data, reason, stopwatch);

        _pendingWrite = Task.Run(() => WriteSave(data, reason, stopwatch));
        return true;
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
            return _loader.TrySerializeAllEntities(out data, options);
        }
        catch (Exception e)
        {
            Log.Error($"Exception while saving world: {e}");
            return false;
        }
        finally
        {
            _saving = false;

            foreach (var proto in unsavable)
            {
                proto.MapSavable = false;
            }
        }
    }

    /// <summary>
    /// Writes captured world data to disk. Safe to run on a background thread.
    /// </summary>
    private bool WriteSave(MappingDataNode data, string reason, System.Diagnostics.Stopwatch stopwatch)
    {
        var tmpPath = new ResPath(_savePath + ".tmp");
        var bakPath = new ResPath(_savePath + ".bak");

        try
        {
            _res.UserData.CreateDir(_savePath.Directory);
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
            if (_res.UserData.Exists(_savePath))
            {
                TryDelete(bakPath);
                _res.UserData.Rename(_savePath, bakPath);
            }

            _res.UserData.Rename(tmpPath, _savePath);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to move world save into place: {e}");
            return false;
        }

        Log.Info($"World saved to {_savePath} in {stopwatch.Elapsed.TotalSeconds:N2}s ({reason}).");
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

        Log.Info($"Loaded world from {_savePath} in {stopwatch.Elapsed.TotalSeconds:N2}s: " +
                 $"{result.Maps.Count} maps, {result.Grids.Count} grids, {result.Entities.Count} entities.");
        return true;
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
        var toRemove = new Queue<EntityUid>();
        foreach (var uid in ev.Entities)
        {
            if (HasComp<GameRuleComponent>(uid) ||
                HasComp<AudioComponent>(uid) ||
                HasComp<AudioPresetComponent>(uid) ||
                HasComp<AudioAuxiliaryComponent>(uid) ||
                HasComp<AudioEffectComponent>(uid) ||
                HasComp<DungeonAtlasTemplateComponent>(uid))
            {
                toRemove.Enqueue(uid);
            }
        }

        var removed = new HashSet<EntityUid>();
        while (toRemove.TryDequeue(out var uid))
        {
            if (!ev.Entities.Remove(uid))
                continue;

            removed.Add(uid);
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                toRemove.Enqueue(child);
            }
        }

        if (removed.Count == 0)
            return;

        // Maps and grids keep some of their data in "chunk" entities that live in null-space rather than as children.
        // Drop the chunks that belong to anything we just removed, so they don't point at entities missing from the save.
        var chunks = new List<EntityUid>();
        foreach (var uid in ev.Entities)
        {
            if (TryComp<ChunkEntityComponent>(uid, out var chunk) && removed.Contains(chunk.Root))
                chunks.Add(uid);
        }

        foreach (var uid in chunks)
        {
            ev.Entities.Remove(uid);
        }
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
