using System.Linq;
using Content.Server.Salvage.Expeditions;
using Content.Server.Salvage.Magnet;
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
using Content.Shared.Physics;
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
    private static readonly Robust.Shared.Prototypes.ProtoId<Content.Shared.Roles.JobPrototype> CaptainJob = "Captain";

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private ShuttleSystem _shuttle = default!;
    [Dependency] private ShuttleConsoleSystem _console = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private Content.Shared.Maps.TurfSystem _turf = default!;
    [Dependency] private Content.Server.Salvage.SalvageSystem _salvage = default!;

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
                BringHome(mob, home.Value, xform => xform.MapUid == expeditionMap);
                Log.Info($"Brought {ToPrettyString(mob)} home from expedition {ToPrettyString(expeditionMap)}");
            }
        }
    }

    /// <summary>
    /// Brings players standing on salvage the magnet pulled in (asteroids, debris, wrecks) back to their station,
    /// since magnet salvage isn't saved. Called right before the shutdown / round restart save, and after a load.
    /// </summary>
    public void BringPlayersHomeFromMagnetSalvage()
    {
        var query = AllEntityQuery<SalvageMagnetDataComponent>();
        while (query.MoveNext(out var station, out var data))
        {
            var grids = _salvage.GetActiveMagnetGrids(data);
            if (grids.Count == 0)
                continue;

            var mobs = new List<EntityUid>();
            var mobQuery = AllEntityQuery<MindContainerComponent, TransformComponent>();
            while (mobQuery.MoveNext(out var mob, out var mind, out var xform))
            {
                if (mind.HasMind && xform.GridUid is { } grid && grids.Contains(grid))
                    mobs.Add(mob);
            }

            if (mobs.Count == 0)
                continue;

            var home = GetHomeGrid(station);
            if (home == null)
            {
                Log.Warning($"Nowhere to bring players home to from magnet salvage of {ToPrettyString(station)}");
                continue;
            }

            foreach (var mob in mobs)
            {
                BringHome(mob, home.Value, xform => xform.GridUid is { } grid && grids.Contains(grid));
                Log.Info($"Brought {ToPrettyString(mob)} home from magnet salvage");
            }
        }
    }

    /// <summary>
    /// Moves a player home, along with whatever they're dragging if it's somewhere that isn't saved too (e.g. a locker
    /// they were dragging off the salvage). The dragged thing goes on a free floor tile next to them, since two bodies
    /// on the same spot shove each other (it pushed the player off the station in testing). If there's no free floor
    /// next to them, it's left behind.
    /// </summary>
    private void BringHome(EntityUid mob, EntityUid home, Func<TransformComponent, bool> notSaved)
    {
        EntityUid? dragged = null;
        if (TryComp<Content.Shared.Movement.Pulling.Components.PullerComponent>(mob, out var puller) &&
            puller.Pulling is { } pulling && notSaved(Transform(pulling)))
        {
            dragged = pulling;
        }

        var arrival = GetArrivalCoordinates(home);
        _container.TryRemoveFromContainer(mob, force: true);
        _transform.SetCoordinates(mob, arrival);
        _transform.AttachToGridOrMap(mob);

        if (dragged is not { } item)
            return;

        if (!TryFindFreeFloorNextTo(arrival, out var spot))
        {
            Log.Info($"Left {ToPrettyString(item)} behind: no free floor next to where {ToPrettyString(mob)} arrived");
            return;
        }

        _transform.SetCoordinates(item, spot);
        _transform.AttachToGridOrMap(item);
        Log.Info($"Brought {ToPrettyString(item)} home too, since {ToPrettyString(mob)} was dragging it");
    }

    private static readonly Vector2i[] NextTo =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(-1, 1), new(1, -1), new(-1, -1),
    };

    /// <summary>
    /// The middle of a floor tile next to <paramref name="coords"/> with nothing solid on it.
    /// </summary>
    private bool TryFindFreeFloorNextTo(EntityCoordinates coords, out EntityCoordinates spot)
    {
        spot = default;
        if (_transform.GetGrid(coords) is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComp))
            return false;

        var center = _map.TileIndicesFor(grid, gridComp, coords);
        foreach (var offset in NextTo)
        {
            var tile = _map.GetTileRef(grid, gridComp, center + offset);
            if (_turf.IsSpace(tile) || _turf.IsTileBlocked(tile, CollisionGroup.MobMask))
                continue;

            spot = _map.GridTileToLocal(grid, gridComp, tile.GridIndices);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Cleans up after a world load: expedition and salvage magnet leftovers are removed, expedition consoles and
    /// salvage magnets start fresh, and shuttles that were mid-FTL finish their jump.
    /// </summary>
    public void FixAfterLoad()
    {
        // Magnet salvage isn't saved; saves from before that may still have some pulled in. Clear it out and give
        // every magnet fresh offers, as if it had never been used.
        BringPlayersHomeFromMagnetSalvage();
        var holdingMaps = new List<EntityUid>();
        var holdingQuery = AllEntityQuery<SalvageMagnetHoldingMapComponent>();
        while (holdingQuery.MoveNext(out var uid, out _))
        {
            holdingMaps.Add(uid);
        }

        foreach (var map in holdingMaps)
        {
            Log.Info($"Removing saved salvage magnet holding map {ToPrettyString(map)}");
            Del(map);
        }

        var magnets = AllEntityQuery<SalvageMagnetDataComponent>();
        while (magnets.MoveNext(out var station, out var magnetData))
        {
            _salvage.ResetMagnet((station, magnetData));
        }

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
    /// Where players stranded on something that isn't saved (an expedition, magnet salvage) are brought back to:
    /// the captain's spawn point on the home grid, or failing that a late-join spawn point, or failing that the floor
    /// tile closest to the middle of the grid.
    /// </summary>
    private EntityCoordinates GetArrivalCoordinates(EntityUid home)
    {
        EntityCoordinates? captain = null;
        var points = new List<EntityCoordinates>();
        var query = AllEntityQuery<SpawnPointComponent, TransformComponent>();
        while (query.MoveNext(out var point, out var xform))
        {
            if (xform.GridUid != home)
                continue;

            if (point.SpawnType == SpawnPointType.Job && point.Job == CaptainJob)
                captain ??= xform.Coordinates;
            else if (point.SpawnType == SpawnPointType.LateJoin)
                points.Add(xform.Coordinates);
        }

        if (captain != null)
            return captain.Value;

        if (points.Count > 0)
            return points[0];

        // No arrivals spawn: the floor tile closest to the middle of the grid (the middle itself may be open space).
        var grid = Comp<MapGridComponent>(home);
        var center = grid.LocalAABB.Center;
        Vector2i? best = null;
        var bestDistance = float.MaxValue;
        var tiles = _map.GetAllTilesEnumerator(home, grid);
        while (tiles.MoveNext(out var tile))
        {
            var distance = (_map.GridTileToLocal(home, grid, tile.Value.GridIndices).Position - center).LengthSquared();
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = tile.Value.GridIndices;
        }

        return best is { } indices ? _map.GridTileToLocal(home, grid, indices) : new EntityCoordinates(home, center);
    }
}
