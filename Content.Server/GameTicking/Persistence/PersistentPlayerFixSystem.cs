using Content.Server.Cloning;
using Content.Server.Ghost.Roles.Components;
using Content.Shared.CCVar;
using Content.Shared.Damage.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Mind;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Configuration;

namespace Content.Server.GameTicking.Persistence;

/// <summary>
/// TEMPORARY workaround, enabled with <see cref="CCVars.TempPlayerFix"/>.
/// After a world save is loaded, replaces every living player body with a fresh clone that is offered as a ghost role.
/// </summary>
public sealed partial class PersistentPlayerFixSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private CloningSystem _cloning = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    /// <summary>
    /// Cloning settings used for the copy: same body, traits, equipment and implants, no antag roles.
    /// </summary>
    private const string CloningSettings = "BaseClone";

    public bool Enabled => _cfg.GetCVar(CCVars.TempPlayerFix);

    /// <summary>
    /// Replaces each living player body with a clone that is up for grabs as a ghost role.
    /// The player's mind is detached first, so they come back as an observer and can take the role.
    /// </summary>
    public void ReplacePlayerBodies()
    {
        var bodies = new List<(EntityUid Mind, EntityUid Body)>();
        var query = EntityQueryEnumerator<MindComponent>();
        while (query.MoveNext(out var mindId, out var mind))
        {
            // Only bodies that belong to a player.
            if (mind.OriginalOwnerUserId == null || mind.OwnedEntity is not { } body)
                continue;

            // Ghosts, borgs, animals etc. can't be cloned; dead bodies are left as they are.
            if (!HasComp<HumanoidProfileComponent>(body) || _mobState.IsDead(body))
                continue;

            bodies.Add((mindId, body));
        }

        foreach (var (mindId, original) in bodies)
        {
            ReplaceBody(mindId, original);
        }
    }

    private void ReplaceBody(EntityUid mindId, EntityUid original)
    {
        var xform = Transform(original);
        var coords = _transform.GetMapCoordinates(original, xform);

        if (!_cloning.TryCloning(original, coords, CloningSettings, out var clone))
        {
            Log.Error($"temp_playerfix: failed to clone {ToPrettyString(original)}, leaving it as it is.");
            return;
        }

        _transform.SetLocalRotation(clone.Value, xform.LocalRotation);
        _damageable.SetDamage(clone.Value, _damageable.GetAllDamage(original));

        // Offer the clone to observers.
        var name = Name(original);
        var ghostRole = EnsureComp<GhostRoleComponent>(clone.Value);
        ghostRole.RoleName = name;
        ghostRole.RoleDescription = Loc.GetString("temp-playerfix-ghost-role-description", ("name", name));
        EnsureComp<GhostTakeoverAvailableComponent>(clone.Value);

        // Take the mind out of the old body without making a ghost, then get rid of the old body.
        // The player keeps their mind and will join as an observer.
        _mind.TransferTo(mindId, null, createGhost: false);
        Del(original);

        Log.Info($"temp_playerfix: replaced {name}'s body with {ToPrettyString(clone.Value)} (now a ghost role).");
    }
}
