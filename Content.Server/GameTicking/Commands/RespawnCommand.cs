using System.Linq;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.Mind;
using Content.Shared.Administration;
using Content.Shared.Ghost.Components;
using Content.Shared.Players;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Network;

namespace Content.Server.GameTicking.Commands
{
    /// <summary>
    /// Sends a player back to the lobby.
    /// Anyone can respawn themselves while they're a ghost; admins (moderator) can respawn anyone at any time.
    /// </summary>
    [AnyCommand]
    sealed partial class RespawnCommand : LocalizedEntityCommands
    {
        [Dependency] private IPlayerManager _player = default!;
        [Dependency] private IPlayerLocator _locator = default!;
        [Dependency] private IAdminManager _admin = default!;
        [Dependency] private ServerGameTicker _gameTicker = default!;
        [Dependency] private MindSystem _mind = default!;

        public override string Command => "respawn";

        public override async void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var player = shell.Player;

            // Players without admin rights may only respawn themselves, and only as a ghost.
            if (player != null && !_admin.HasAdminFlag(player, AdminFlags.Moderator))
            {
                if (args.Length > 0)
                {
                    shell.WriteLine(Loc.GetString("cmd-respawn-self-only"));
                    return;
                }

                if (!EntityManager.HasComponent<GhostComponent>(player.AttachedEntity))
                {
                    shell.WriteLine(Loc.GetString("cmd-respawn-not-ghost"));
                    return;
                }

                _gameTicker.Respawn(player);
                return;
            }

            if (args.Length > 1)
            {
                shell.WriteError(Loc.GetString("cmd-respawn-invalid-args"));
                return;
            }

            NetUserId userId;
            if (args.Length == 0)
            {
                if (player == null)
                {
                    shell.WriteError(Loc.GetString("cmd-respawn-no-player"));
                    return;
                }

                userId = player.UserId;
            }
            else
            {
                var located = await _locator.LookupIdByNameOrIdAsync(args[0]);

                if (located == null)
                {
                    shell.WriteError(Loc.GetString("cmd-respawn-unknown-player"));
                    return;
                }

                userId = located.UserId;
            }

            if (!_player.TryGetSessionById(userId, out var targetPlayer))
            {
                if (!_player.TryGetPlayerData(userId, out var data))
                {
                    shell.WriteError(Loc.GetString("cmd-respawn-unknown-player"));
                    return;
                }

                _mind.WipeMind(data.ContentData()?.Mind);
                shell.WriteError(Loc.GetString("cmd-respawn-player-not-online"));
                return;
            }

            _gameTicker.Respawn(targetPlayer);
        }

        public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
        {
            if (args.Length != 1)
                return CompletionResult.Empty;

            // Only admins can respawn other players.
            if (shell.Player != null && !_admin.HasAdminFlag(shell.Player, AdminFlags.Moderator))
                return CompletionResult.Empty;

            var options = _player.Sessions.OrderBy(c => c.Name).Select(c => c.Name).ToArray();

            return CompletionResult.FromHintOptions(options, Loc.GetString("cmd-respawn-player-completion"));
        }
    }
}
