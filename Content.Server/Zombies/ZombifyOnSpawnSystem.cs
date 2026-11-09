using Content.Server.Ghost.Roles.Components;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Shared.Mobs.Components;
using Content.Shared.Zombies;

namespace Content.Server.Zombies;

/// <summary>
/// Zombifies mobs with <see cref="ZombifyOnSpawnComponent"/>. They are plain NPCs, not ghost roles.
/// </summary>
public sealed partial class ZombifyOnSpawnSystem : EntitySystem
{
    [Dependency] private ZombieSystem _zombie = default!;

    private readonly List<Entity<ZombifyOnSpawnComponent>> _toZombify = new();

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Includes paused entities: expedition maps stay paused until the crew arrives.
        var query = AllEntityQuery<ZombifyOnSpawnComponent, MobStateComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var comp, out _, out var meta))
        {
            // Wait until its clothing and appearance have been set up.
            if (meta.EntityLifeStage < EntityLifeStage.MapInitialized)
                continue;

            _toZombify.Add((uid, comp));
        }

        foreach (var ent in _toZombify)
        {
            Zombify(ent);
        }

        _toZombify.Clear();
    }

    private void Zombify(Entity<ZombifyOnSpawnComponent> ent)
    {
        var chance = ent.Comp.InfectionChance;
        RemComp<ZombifyOnSpawnComponent>(ent);

        _zombie.ZombifyEntity(ent);
        EnsureComp<ExpeditionZombieComponent>(ent);

        if (TryComp<ZombieComponent>(ent, out var zombie))
            zombie.BaseZombieInfectionChance = chance;

        // Aggressive: path straight through doors (prying them open) and smash windows, grilles and other flimsy
        // things in the way, instead of giving up when a door or window is between them and their target.
        if (TryComp<HTNComponent>(ent, out var htn))
        {
            htn.Blackboard.SetValue(NPCBlackboard.NavPry, true);
            htn.Blackboard.SetValue(NPCBlackboard.NavSmash, true);
            htn.Blackboard.SetValue(NPCBlackboard.NavClimb, true);
            htn.Blackboard.SetValue(NPCBlackboard.NavInteract, true);
        }

        // Zombification offers the body as a ghost role; these are meant to be ordinary monsters.
        RemComp<GhostRoleComponent>(ent);
        RemComp<GhostTakeoverAvailableComponent>(ent);
    }
}
