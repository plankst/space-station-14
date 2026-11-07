using Content.Shared.CCVar;
using System.Linq;
using Content.Server.Chemistry.Components;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;

namespace Content.Server.GameTicking.Persistence;

/// <summary>
/// TEMPORARY workaround, enabled with <see cref="CCVars.TempDispenseFix"/>.
/// After a world save is loaded, replaces every reagent dispenser with a fresh copy of itself.
/// </summary>
public sealed partial class PersistentDispenserFixSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public bool Enabled => _cfg.GetCVar(CCVars.TempDispenseFix);

    /// <summary>
    /// Replaces each reagent dispenser with a fresh copy: same prototype, place, rotation, anchoring, name and contents.
    /// </summary>
    public void ReplaceDispensers()
    {
        var dispensers = new List<EntityUid>();
        var query = EntityQueryEnumerator<ReagentDispenserComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            dispensers.Add(uid);
        }

        foreach (var uid in dispensers)
        {
            ReplaceDispenser(uid);
        }
    }

    private void ReplaceDispenser(EntityUid original)
    {
        var meta = MetaData(original);
        if (meta.EntityPrototype is not { } proto)
        {
            Log.Warning($"temp_dispensefix: {ToPrettyString(original)} has no prototype, leaving it as it is.");
            return;
        }

        var xform = Transform(original);
        var clone = Spawn(proto.ID, xform.Coordinates);
        var cloneXform = Transform(clone);

        // Same name/description (e.g. if it was renamed), facing and anchoring.
        _metaData.SetEntityName(clone, meta.EntityName);
        _metaData.SetEntityDescription(clone, meta.EntityDescription);
        if (xform.Anchored && !cloneXform.Anchored)
            _transform.AnchorEntity(clone, cloneXform);
        else if (!xform.Anchored && cloneXform.Anchored)
            _transform.Unanchor(clone, cloneXform);

        // After anchoring, which can snap the rotation.
        _transform.SetLocalRotation(clone, xform.LocalRotation, cloneXform);

        // Move the contents over (jugs, inserted beaker, ...), replacing whatever the fresh copy spawned with.
        if (TryComp<ContainerManagerComponent>(original, out var originalContainers))
        {
            foreach (var (id, from) in originalContainers.Containers)
            {
                if (!_container.TryGetContainer(clone, id, out var to))
                    continue;

                foreach (var spawned in to.ContainedEntities.ToArray())
                {
                    Del(spawned);
                }

                foreach (var item in from.ContainedEntities.ToArray())
                {
                    _container.Remove(item, from, force: true);
                    if (!_container.Insert(item, to, force: true))
                    {
                        Log.Warning($"temp_dispensefix: couldn't move {ToPrettyString(item)} into {ToPrettyString(clone)}, dropping it.");
                        _transform.SetCoordinates(item, xform.Coordinates);
                    }
                }
            }
        }

        Del(original);
        Log.Info($"temp_dispensefix: replaced dispenser with {ToPrettyString(clone)}.");
    }
}
