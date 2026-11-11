using System.Numerics;
using Content.Server.Anomaly.Components;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Anomaly.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Projectiles;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server.Anomaly.Effects;

/// <summary>
/// This handles <see cref="ProjectileAnomalyComponent"/> and the events from <seealso cref="AnomalySystem"/>
/// </summary>
public sealed partial class ProjectileAnomalySystem : EntitySystem
{
    [Dependency] private TransformSystem _xform = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private GunSystem _gunSystem = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private EntityQuery<MobStateComponent> _mobStateQuery = default!;

    /// <summary> Pre-allocated collection for calculating entities in range. </summary>
    private readonly HashSet<EntityUid> _inRange = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<ProjectileAnomalyComponent, AnomalyPulseEvent>(OnPulse);
        SubscribeLocalEvent<ProjectileAnomalyComponent, AnomalySupercriticalEvent>(OnSupercritical);
    }

    private void OnPulse(EntityUid uid, ProjectileAnomalyComponent component, ref AnomalyPulseEvent args)
    {
        ShootProjectilesAtEntities(uid, component, args.Severity * args.PowerModifier, true);
    }

    private void OnSupercritical(EntityUid uid, ProjectileAnomalyComponent component, ref AnomalySupercriticalEvent args)
    {
        // The anomaly is deleted right after going supercritical, so its shots don't name it as their shooter:
        // projectiles pointing at a deleted shooter log an error every time they're sent to a player.
        ShootProjectilesAtEntities(uid, component, args.PowerModifier, false);
    }

    private void ShootProjectilesAtEntities(EntityUid uid, ProjectileAnomalyComponent component, float severity, bool anomalyIsShooter)
    {
        var projectileCount = (int)MathF.Round(MathHelper.Lerp(component.MinProjectiles, component.MaxProjectiles, severity));

        var xform = Transform(uid);

        _inRange.Clear();
        _lookup.GetEntitiesInRange(uid, component.ProjectileRange * severity, _inRange, LookupFlags.Dynamic);

        if (_inRange.Count == 0)
            return;

        var priority = new List<EntityUid>();
        foreach (var entity in _inRange)
        {
            if (_mobStateQuery.HasComponent(entity))
                priority.Add(entity);
        }

        Log.Debug($"shots: {projectileCount}");
        while (projectileCount > 0)
        {
            Log.Debug($"{projectileCount}");
            var target = priority.Count > 0
                ? _random.PickAndTake(priority)
                : _random.Pick(_inRange);

            var targetXForm = Transform(target);
            var targetCoords = targetXForm.Coordinates.Offset(_random.NextVector2(0.5f));

            ShootProjectile(
                uid,
                component,
                xform.Coordinates,
                targetCoords,
                severity,
                anomalyIsShooter
            );
            projectileCount--;
        }
    }

    private void ShootProjectile(
        EntityUid uid,
        ProjectileAnomalyComponent component,
        EntityCoordinates coords,
        EntityCoordinates targetCoords,
        float severity,
        bool anomalyIsShooter
    )
    {
        var mapPos = _xform.ToMapCoordinates(coords);

        var spawnCoords = _map.TryFindGridAt(mapPos, out var gridUid, out _)
                ? _xform.WithEntityId(coords, gridUid)
                : new(_map.GetMapOrInvalid(mapPos.MapId), mapPos.Position);

        var ent = Spawn(component.ProjectilePrototype, spawnCoords);
        var direction = _xform.ToMapCoordinates(targetCoords).Position - mapPos.Position;

        if (!TryComp<ProjectileComponent>(ent, out var comp))
            return;

        comp.Damage *= severity;

        var shooter = anomalyIsShooter ? uid : (EntityUid?) null;
        _gunSystem.ShootProjectile(ent, direction, Vector2.Zero, shooter, shooter, component.ProjectileSpeed);
    }
}
