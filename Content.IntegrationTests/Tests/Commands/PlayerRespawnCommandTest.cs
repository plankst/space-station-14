using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.Administration.Managers;
using Content.Server.GameTicking;
using Content.Shared.CCVar;
using Content.Shared.Ghost.Components;
using Robust.Server.Console;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests.Commands;

/// <summary>
/// A player without admin rights can use "respawn" to go back to the lobby, but only while they're a ghost.
/// </summary>
[TestFixture]
public sealed class PlayerRespawnCommandTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        DummyTicker = false,
        Connected = true,
        Dirty = true,
    };

    [Test]
    public async Task OnlyGhostsCanRespawnThemselves()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var admin = server.ResolveDependency<IAdminManager>();
        var console = server.ResolveDependency<IServerConsoleHost>();
        var ticker = entMan.System<ServerGameTicker>();
        var player = server.ResolveDependency<IPlayerManager>().Sessions.Single();

        server.CfgMan.SetCVar(CCVars.GameLobbyEnabled, true);
        await server.WaitPost(() => admin.DeAdmin(player));
        await pair.RunTicksSync(5);

        try
        {
            // Alive: nothing happens.
            await server.WaitPost(() =>
            {
                var body = entMan.SpawnEntity("MobHuman", new MapCoordinates(0, 0, ticker.DefaultMap));
                var mindSys = entMan.System<Content.Shared.Mind.SharedMindSystem>();
                var mind = mindSys.CreateMind(player.UserId, "Respawn Test Person");
                mindSys.TransferTo(mind, body);
            });
            await pair.RunTicksSync(5);

            var body = player.AttachedEntity;
            Assert.That(body, Is.Not.Null);
            await server.WaitPost(() => console.ExecuteCommand(player, "respawn"));
            await pair.RunTicksSync(5);
            Assert.That(player.AttachedEntity, Is.EqualTo(body), "A living player should not be respawned");

            // A non-admin can't respawn someone else, even as a ghost.
            await server.WaitPost(() => ticker.SpawnObserver(player));
            await pair.RunTicksSync(5);
            Assert.That(entMan.HasComponent<GhostComponent>(player.AttachedEntity), "Test setup: player should be a ghost");
            var ghost = player.AttachedEntity;
            await server.WaitPost(() => console.ExecuteCommand(player, $"respawn {player.Name}"));
            await pair.RunTicksSync(5);
            Assert.That(player.AttachedEntity, Is.EqualTo(ghost), "Non-admins should only be able to respawn themselves");

            // Ghost: back to the lobby.
            await server.WaitPost(() => console.ExecuteCommand(player, "respawn"));
            await pair.RunTicksSync(5);
            Assert.That(player.AttachedEntity, Is.Null, "A ghost should be sent back to the lobby");
            Assert.That(ticker.PlayerGameStatuses[player.UserId], Is.Not.EqualTo(Content.Shared.GameTicking.PlayerGameStatus.JoinedGame));
        }
        finally
        {
            await server.WaitPost(() => admin.ReAdmin(player));
            server.CfgMan.SetCVar(CCVars.GameLobbyEnabled, false);
            await server.WaitPost(() => ticker.RestartRound());
            await pair.RunTicksSync(5);
        }
    }
}
