using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Salvage.Expeditions;
using Content.Server.Shuttles.Systems;
using Content.Shared.CCVar;
using Content.Shared.Shuttles.Components;
using Content.Shared.SSDIndicator;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.NPC;

/// <summary>
/// Humanoid mobs run by AI that no player controls (any NPC faction, not just zombies) don't fall into SSD sleep, and
/// don't stop an expedition shuttle from leaving as if they were crew left behind.
/// </summary>
[TestFixture]
public sealed class AiNpcWithoutPlayerTest : GameTest
{
    // A humanoid NPC with AI that isn't a zombie.
    private const string NpcProto = "MobSalvager";

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.ICSSDSleepTime), 2f)]
    public async Task AiNpcDoesNotSsdSleep()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();

        EntityUid npc = default;
        await server.WaitPost(() => npc = entMan.SpawnAtPosition(NpcProto, map.GridCoords));
        await pair.RunTicksSync((int) server.Timing.TickRate * 8);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<SSDIndicatorComponent>(npc).IsSSD, Is.False, "The NPC counts as a player gone SSD");
            Assert.That(entMan.HasComponent<StunnedComponent>(npc), Is.False, "The NPC fell into SSD sleep");
            Assert.That(entMan.System<StandingStateSystem>().IsDown(npc), Is.False, "The NPC fell over");
            entMan.DeleteEntity(npc);
        });
    }

    [Test]
    public async Task AiNpcDoesNotBlockExpeditionShuttle()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;
        var mapSys = entMan.System<SharedMapSystem>();
        var shuttles = entMan.System<ShuttleSystem>();

        await server.WaitAssertion(() =>
        {
            var expedition = mapSys.CreateMap(out var mapId);
            entMan.AddComponent<SalvageExpeditionComponent>(expedition);

            var shuttle = mapSys.CreateGridEntity(mapId);
            mapSys.SetTile(shuttle.Owner, shuttle.Comp, Vector2i.Zero, new Tile(1));
            entMan.EnsureComponent<ShuttleComponent>(shuttle.Owner);

            var npc = entMan.SpawnEntity(NpcProto, new MapCoordinates(20, 20, mapId));
            Assert.That(shuttles.CanFTL(shuttle.Owner, out var reason), $"An NPC on the planet stops the shuttle leaving: {reason}");

            // Someone without AI (e.g. a crewmember's body) still counts as left behind.
            var crew = entMan.SpawnEntity("MobHuman", new MapCoordinates(25, 25, mapId));
            Assert.That(shuttles.CanFTL(shuttle.Owner, out _), Is.False, "A crewmember left on the planet doesn't stop the shuttle");

            entMan.DeleteEntity(npc);
            entMan.DeleteEntity(crew);
            entMan.DeleteEntity(expedition);
        });
    }
}
