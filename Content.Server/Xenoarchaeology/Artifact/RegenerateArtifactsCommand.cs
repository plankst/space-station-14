using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Robust.Shared.Console;

namespace Content.Server.Xenoarchaeology.Artifact;

/// <summary>
/// Regenerates the nodes of every artifact (or one artifact), so artifacts from an old world save follow the current
/// artifact rules (node count, allowed triggers and effects).
/// </summary>
[AdminCommand(AdminFlags.Debug)]
public sealed partial class RegenerateArtifactsCommand : LocalizedEntityCommands
{
    [Dependency] private XenoArtifactSystem _artifact = default!;

    public override string Command => "regenartifacts";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1)
        {
            shell.WriteError(Loc.GetString("cmd-regenartifacts-invalid-args"));
            return;
        }

        var targets = new List<Entity<XenoArtifactComponent>>();
        if (args.Length == 1)
        {
            if (!NetEntity.TryParse(args[0], out var netEnt)
                || !EntityManager.TryGetEntity(netEnt, out var uid)
                || !EntityManager.TryGetComponent<XenoArtifactComponent>(uid, out var comp))
            {
                shell.WriteError(Loc.GetString("cmd-regenartifacts-not-artifact", ("entity", args[0])));
                return;
            }

            targets.Add((uid.Value, comp));
        }
        else
        {
            var query = EntityManager.AllEntityQueryEnumerator<XenoArtifactComponent>();
            while (query.MoveNext(out var uid, out var comp))
            {
                targets.Add((uid, comp));
            }
        }

        foreach (var artifact in targets)
        {
            _artifact.RegenerateArtifact(artifact);
        }

        shell.WriteLine(Loc.GetString("cmd-regenartifacts-done", ("count", targets.Count)));
    }
}
