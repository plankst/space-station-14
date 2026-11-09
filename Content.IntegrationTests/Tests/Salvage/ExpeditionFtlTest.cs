using System.Linq;
using System.Reflection;
using Content.IntegrationTests.Fixtures;
using Content.Server.Salvage;
using Content.Server.Salvage.Expeditions;
using Content.Server.Shuttles.Systems;
using Content.Server.Zombies;
using Content.Shared.Mind;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Procedural;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Station.Systems;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Salvage;

/// <summary>
/// Flies a crewed shuttle to a real, generated expedition of each faction and back again, with a player connected.
/// Any error logged on the server or client fails the test.
/// </summary>
[TestFixture]
public sealed class ExpeditionFtlTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Dirty = true, DummyTicker = false, Connected = true };

    [Test]
    [TestCase("Carps")]
    [TestCase("Xenos")]
    [TestCase("Zombies")]
    public async Task ShuttleFliesToExpeditionAndBack(string faction)
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;
        var salvage = entMan.System<SalvageSystem>();
        var shuttles = entMan.System<ShuttleSystem>();
        var mapSys = entMan.System<SharedMapSystem>();
        var ticker = entMan.System<Content.Server.GameTicking.ServerGameTicker>();
        var tickRate = (int) server.Timing.TickRate;

        await server.WaitPost(() =>
        {
            var station = entMan.System<StationSystem>().GetStations().First().Owner;
            var difficulty = server.ProtoMan.Index<SalvageDifficultyPrototype>("Moderate");
            var seed = Enumerable.Range(1, 1000).First(s => salvage.GetMission(difficulty, s).Faction == faction);
            var mission = new SalvageMissionParams { Index = 0, Seed = seed, Difficulty = "Moderate" };
            typeof(SalvageSystem).GetMethod("SpawnMission", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(salvage, new object?[] { mission, station, null });
        });

        // Wait for the planet to be generated (mobs are the last thing it spawns).
        EntityUid expedition = default;
        for (var i = 0; i < 600 && expedition == default; i++)
        {
            await pair.RunTicksSync(10);
            await server.WaitPost(() =>
            {
                var q = entMan.AllEntityQueryEnumerator<SalvageExpeditionComponent>();
                if (q.MoveNext(out var uid, out _) && entMan.HasComponent<BiomeComponent>(uid))
                    expedition = uid;
            });
        }

        Assert.That(expedition, Is.Not.EqualTo(default(EntityUid)), "The expedition wasn't generated");
        await pair.RunTicksSync(tickRate * 10);

        // A small shuttle with the player aboard, near the station.
        EntityUid shuttle = default;
        await server.WaitPost(() =>
        {
            var grid = mapSys.CreateGridEntity(ticker.DefaultMap);
            entMan.System<SharedTransformSystem>().SetWorldPosition(grid.Owner, new System.Numerics.Vector2(500, 500));
            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 5; y++)
                {
                    mapSys.SetTile(grid.Owner, grid.Comp, new Vector2i(x, y), new Tile(1));
                }
            }

            entMan.EnsureComponent<ShuttleComponent>(grid.Owner);
            shuttle = grid.Owner;

            var body = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid.Owner, 2.5f, 2.5f));
            var player = server.ResolveDependency<IPlayerManager>().Sessions.Single();
            var minds = entMan.System<SharedMindSystem>();
            var mind = minds.CreateMind(player.UserId, "Explorer");
            minds.TransferTo(mind, body);

            Assert.That(shuttles.CanFTL(shuttle, out var reason), $"The shuttle can't leave the station: {reason}");
            shuttles.FTLToCoordinates(shuttle, entMan.GetComponent<ShuttleComponent>(shuttle),
                new EntityCoordinates(expedition, 0, 0), Angle.Zero, startupTime: 1f, hyperspaceTime: 2f);
        });

        async Task WaitForLanding(EntityUid map, string where)
        {
            for (var i = 0; i < tickRate * 15; i += 10)
            {
                await pair.RunTicksSync(10);
                var landed = false;
                // After landing the shuttle keeps its FTL component for the cooldown.
                await server.WaitPost(() => landed = entMan.GetComponent<TransformComponent>(shuttle).MapUid == map &&
                                                     (!entMan.TryGetComponent<FTLComponent>(shuttle, out var ftl) ||
                                                      ftl.State == FTLState.Cooldown));
                if (landed)
                    return;
            }

            var state = "";
            await server.WaitPost(() =>
            {
                var xform = entMan.GetComponent<TransformComponent>(shuttle);
                var ftl = entMan.TryGetComponent<FTLComponent>(shuttle, out var f) ? $"{f.State} until {f.StateTime.End}" : "none";
                state = $"map={entMan.ToPrettyString(xform.MapUid)} target={entMan.ToPrettyString(map)} ftl={ftl} " +
                        $"now={server.Timing.CurTime} paused={entMan.GetComponent<MetaDataComponent>(map).EntityPaused}";
            });
            Assert.Fail($"The shuttle never landed {where}: {state}");
        }

        await WaitForLanding(expedition, "on the expedition");

        // The shuttle console's dock view asks whether to draw each grid nearby, including the planet itself (a grid
        // with no physics body on the client). That used to log an error every frame.
        await pair.RunTicksSync(tickRate);
        var client = pair.Client;
        await client.WaitPost(() =>
        {
            var planet = pair.ToClientUid(expedition);
            Assert.That(client.EntMan.EntityExists(planet), "The client doesn't know the planet");
            client.EntMan.System<SharedShuttleSystem>().CanDraw(planet);
        });

        // Spend a little while on the planet, and wait out the FTL cooldown.
        await pair.RunTicksSync(tickRate * 30);
        for (var i = 0; i < tickRate * 120; i += 10)
        {
            var cooling = false;
            await server.WaitPost(() => cooling = entMan.HasComponent<FTLComponent>(shuttle));
            if (!cooling)
                break;

            await pair.RunTicksSync(10);
        }

        await server.WaitPost(() =>
        {
            if (faction == "Zombies")
                Assert.That(entMan.Count<ExpeditionZombieComponent>(), Is.GreaterThan(0), "No zombies on a zombie expedition");

            Assert.That(shuttles.CanFTL(shuttle, out var reason), $"The shuttle can't leave the {faction} expedition: {reason}");
            shuttles.FTLToCoordinates(shuttle, entMan.GetComponent<ShuttleComponent>(shuttle),
                new EntityCoordinates(mapSys.GetMap(ticker.DefaultMap), 500, 500), Angle.Zero, startupTime: 1f, hyperspaceTime: 2f);
        });

        await WaitForLanding(mapSys.GetMap(ticker.DefaultMap), "back home");
        await pair.RunTicksSync(tickRate * 2);

        await server.WaitPost(() =>
        {
            Assert.That(entMan.Deleted(expedition) || entMan.IsQueuedForDeletion(expedition),
                "The expedition wasn't cleaned up after the last shuttle left");
        });
    }
}
