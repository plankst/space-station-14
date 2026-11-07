using Content.Server.GameTicking.Persistence;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;

namespace Content.Server.Administration.Commands;

[AdminCommand(AdminFlags.Server)]
public sealed partial class PersistenceSave : LocalizedEntityCommands
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private WorldPersistenceSystem _worldPersistence = default!;

    public override string Command => "persistencesave";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        // No arguments: save the whole world to the world save file.
        if (args.Length == 0)
        {
            if (!_worldPersistence.Enabled)
            {
                shell.WriteError(Loc.GetString("cmd-persistencesave-world-disabled", ("cvar", CCVars.UsePersistence.Name)));
                return;
            }

            if (!_worldPersistence.CanSave())
            {
                shell.WriteError(Loc.GetString("cmd-persistencesave-world-no-round"));
                return;
            }

            if (_worldPersistence.SaveWorld($"persistencesave command by {shell.Player?.Name ?? "server console"}"))
                shell.WriteLine(Loc.GetString("cmd-persistencesave-world-success", ("path", _worldPersistence.SavePath.ToString())));
            else
                shell.WriteError(Loc.GetString("cmd-persistencesave-world-failed"));

            return;
        }

        if (args.Length > 2)
        {
            shell.WriteError(Loc.GetString("shell-wrong-arguments-number"));
            return;
        }

        if (!int.TryParse(args[0], out var intMapId))
        {
            shell.WriteError(Loc.GetString("cmd-parse-failure-integer", ("arg", args[0])));
            return;
        }

        var mapId = new MapId(intMapId);
        if (!_map.MapExists(mapId))
        {
            shell.WriteError(Loc.GetString("cmd-savemap-not-exist"));
            return;
        }

        var saveFilePath = (args.Length > 1 ? args[1] : null) ?? _config.GetCVar(CCVars.GameMap);
        if (string.IsNullOrWhiteSpace(saveFilePath))
        {
            shell.WriteError(Loc.GetString("cmd-persistencesave-no-path", ("cvar", nameof(CCVars.GameMap))));
            return;
        }

        _mapLoader.TrySaveMap(mapId, new ResPath(saveFilePath));
        shell.WriteLine(Loc.GetString("cmd-savemap-success"));
    }
}
