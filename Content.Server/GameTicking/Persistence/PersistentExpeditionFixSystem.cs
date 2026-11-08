using System.Linq;
using Content.Server.Salvage.Expeditions;
using Content.Server.Shuttles.Systems;
using Content.Shared.Mind.Components;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Spawners.Components;
using Content.Shared.Station.Components;
using Content.Shared.Station.Systems;
using Content.Shared.Timing;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server.GameTicking.Persistence;

/// <summary>
/// Keeps salvage expeditions and FTL travel out of world saves.
/// </summary>
/// <remarks>
/// Both systems keep most of their state in runtime-only fields (offered missions, the active mission, FTL progress),
/// so restoring them from a save leaves blank expedition consoles and shuttles stuck in hyperspace.
/// Instead, expeditions are never saved: before a final save players and shuttles are brought home from expeditions,
/// and after loading any expedition leftovers are removed and expedition consoles start fresh.
/// </remarks>
public sealed partial class PersistentExpeditionFixSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private ShuttleSystem _shuttle = default!;
    [Dependency] private ShuttleConsoleSystem _console = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedContainerSystem _container = default!;

    /// <summary>
    /// Brings shuttles and players on salvage expeditions back home, since expeditions aren't saved.
    /// Called right before the shutdown / round restart save.
    /// </summary>
    public void BringPlayersHomeFromExpeditions()
    {
        var expeditions = new List<(EntityUid Map, EntityUid Station)>();
        var query = AllEntityQuery<SalvageExpeditionComponent>();
        while (query.MoveNext(out var uid, out var expedition))
        {
            expeditions.Add((uid, expedition.Station));
        }

        foreach (var (expeditionMap, station) in expeditions)
        {
            var home = GetHomeGrid(station);
            if (home == null)
            {
                Log.Warning($"Nowhere to bring things home to from expedition {ToPrettyString(expeditionMap)}");
                continue;
            }

            // Shuttles that flew there (the expedition's own terrain is the map itself).
            var shuttles = new List<EntityUid>();
            var children = Transform(expeditionMap).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                if (child != expeditionMap && HasComp<MapGridComponent>(child) && HasComp<ShuttleComponent>(child))
                    shuttles.Add(child);
            }

            foreach (var shuttle in shuttles)
            {
                if (_shuttle.TryFTLProximity(shuttle, home.Value))
                    Log.Info($"Brought {ToPrettyString(shuttle)} home from expedition {ToPrettyString(expeditionMap)}");
                else
                    Log.Warning($"Couldn't bring {ToPrettyString(shuttle)} home from expedition {ToPrettyString(expeditionMap)}");
            }

            // Players left standing on the expedition itself.
            var mobs = new List<EntityUid>();
            var mobQuery = AllEntityQuery<MindContainerComponent, TransformComponent>();
            while (mobQuery.MoveNext(out var mob, out var mind, out var xform))
            {
                if (mind.HasMind && xform.MapUid == expeditionMap)
                    mobs.Add(mob);
            }

            foreach (var mob in mobs)
            {
                _container.TryRemoveFromContainer(mob, force: true);
                _transform.SetCoordinates(mob, GetArrivalCoordinates(home.Value));
                _transform.AttachToGridOrMap(mob);
                Log.Info($"Brought {ToPrettyString(mob)} home from expedition {ToPrettyString(expeditionMap)}");
            }
        }
    }

    /// <summary>
    /// Cleans up after a world load: expedition leftovers are removed, expedition consoles start fresh, and shuttles
    /// that were mid-FTL finish their jump.
    /// </summary>
    public void FixAfterLoad()
    {
        // Saves made before expeditions were excluded may still contain them.
        BringPlayersHomeFromExpeditions();
        var maps = new List<EntityUid>();
        var mapQuery = AllEntityQuery<SalvageExpeditionComponent>();
        while (mapQuery.MoveNext(out var uid, out _))
        {
            maps.Add(uid);
        }

        foreach (var map in maps)
        {
            Log.Info($"Removing saved salvage expedition {ToPrettyString(map)}");
            Del(map);
        }

        // Coordinates disks only lead to expeditions, which no longer exist.
        var disks = new List<EntityUid>();
        var diskQuery = AllEntityQuery<ShuttleDestinationCoordinatesComponent>();
        while (diskQuery.MoveNext(out var uid, out _))
        {
            disks.Add(uid);
        }

        foreach (var disk in disks)
        {
            Del(disk);
        }

        if (disks.Count > 0)
            Log.Info($"Removed {disks.Count} coordinates disk(s).");

        // Offered missions aren't saved; make every console offer new ones right away.
        var dataQuery = AllEntityQuery<SalvageExpeditionDataComponent>();
        while (dataQuery.MoveNext(out _, out var data))
        {
            data.Missions.Clear();
            data.ActiveMission = 0;
            data.Cooldown = false;
            data.NextOffer = _timing.CurTime;
        }

        // After removing expeditions, so shuttles headed for one go home instead.
        FinishInterruptedFtl();
    }

    /// <summary>
    /// FTL progress isn't saved, so a shuttle saved mid-jump would sit in hyperspace forever.
    /// Shuttles in hyperspace arrive right away; shuttles that were just starting or cooling down are simply ready again.
    /// </summary>
    private void FinishInterruptedFtl()
    {
        var query = AllEntityQuery<FTLComponent, ShuttleComponent, TransformComponent>();
        var ready = new List<EntityUid>();
        while (query.MoveNext(out var uid, out var ftl, out _, out var xform))
        {
            if (!HasComp<FTLMapComponent>(xform.MapUid))
            {
                ready.Add(uid);
                continue;
            }

            // The destination may be gone (e.g. an expedition); go home instead.
            if (!Exists(ftl.TargetCoordinates.EntityId) && GetHomeGrid(_station.GetOwningStation(uid)) is { } home)
                ftl.TargetCoordinates = new EntityCoordinates(home, 0, 0);

            ftl.VisualizerEntity = null;
            ftl.State = FTLState.Arriving;
            ftl.StateTime = StartEndTime.FromCurTime(_timing, TimeSpan.Zero);
            Log.Info($"{ToPrettyString(uid)} was saved mid-FTL, finishing the jump.");
        }

        foreach (var uid in ready)
        {
            RemComp<FTLComponent>(uid);
            _console.RefreshShuttleConsoles(uid);
        }
    }

    /// <summary>
    /// The biggest grid of the given station, or of any station on the main map.
    /// </summary>
    private EntityUid? GetHomeGrid(EntityUid? station)
    {
        if (station is { } s && HasComp<StationDataComponent>(s) && _station.GetLargestGrid(s) is { } grid)
            return grid;

        foreach (var other in _station.GetStations())
        {
            if (_station.GetLargestGrid(other.Owner) is { } otherGrid && Transform(otherGrid).MapID == _ticker.DefaultMap)
                return otherGrid;
        }

        return null;
    }

    /// <summary>
    /// A late-join spawn point on the home grid, or failing that, the middle of it.
    /// </summary>
    private EntityCoordinates GetArrivalCoordinates(EntityUid home)
    {
        var points = new List<EntityCoordinates>();
        var query = AllEntityQuery<SpawnPointComponent, TransformComponent>();
        while (query.MoveNext(out var point, out var xform))
        {
            if (point.SpawnType == SpawnPointType.LateJoin && xform.GridUid == home)
                points.Add(xform.Coordinates);
        }

        if (points.Count > 0)
            return points[0];

        var center = Comp<MapGridComponent>(home).LocalAABB.Center;
        return new EntityCoordinates(home, center);
    }
}
