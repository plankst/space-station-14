using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Persistence;
using Content.Shared.CCVar;
using Content.Shared.Mind;
using Content.Shared.Mobs.Components;
using Robust.Server.Player;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests.Persistence;

/// <summary>
/// Checks that the whole world (including mobs and player bodies) is saved, and that it is loaded back on the next
/// round with players put back into their own bodies.
/// </summary>
[TestFixture]
public sealed class WorldPersistenceTest : GameTest
{
    private const string SavePath = "/persistence/WorldPersistenceTest.yml";
    private const string PlayerBodyName = "Persistent Test Person";

    public override PoolSettings PoolSettings => new()
    {
        DummyTicker = false,
        Connected = true,
        Dirty = true,
    };

    [Test]
    public async Task WorldSurvivesRoundRestart()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var playerMan = server.ResolveDependency<IPlayerManager>();
        var res = server.ResolveDependency<IResourceManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var mindSys = entMan.System<SharedMindSystem>();
        var persistence = entMan.System<WorldPersistenceSystem>();
        var mapSys = entMan.System<SharedMapSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);

        var player = playerMan.Sessions.Single();
        var userId = player.UserId;
        var bodyChildren = 0;

        // Put the player into a named human body next to a named corgi.
        await server.WaitPost(() =>
        {
            var coords = new MapCoordinates(0, 0, ticker.DefaultMap);
            var body = entMan.SpawnEntity("MobHuman", coords);
            entMan.System<MetaDataSystem>().SetEntityName(body, PlayerBodyName);
            entMan.SpawnEntity("MobCorgi", coords);

            var mind = mindSys.CreateMind(userId, PlayerBodyName);
            mindSys.TransferTo(mind, body);
            bodyChildren = entMan.GetComponent<TransformComponent>(body).ChildCount;
        });

        await pair.RunTicksSync(5);
        Assert.That(entMan.GetComponent<MetaDataComponent>(player.AttachedEntity!.Value).EntityName, Is.EqualTo(PlayerBodyName));
        var corgis = CountPrototypes(entMan, ["MobCorgi"]).GetValueOrDefault("MobCorgi");
        Assert.That(corgis, Is.GreaterThan(0));

        // Restarting the round saves the world, wipes it, and (with the lobby disabled) loads it straight back.
        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);

        Assert.Multiple(() =>
        {
            Assert.That(res.UserData.Exists(new ResPath(SavePath)), "World save file was not written");
            Assert.That(ticker.WorldRestoredFromSave, "The round did not load the world save");
            Assert.That(ticker.RunLevel, Is.EqualTo(Shared.GameTicking.GameRunLevel.InRound));
            Assert.That(mapSys.MapExists(ticker.DefaultMap));
        });

        // The player is back in their old body, which still has everything it was carrying / made of.
        var attached = player.AttachedEntity;
        Assert.That(attached, Is.Not.Null, "Player was not put back into a body");
        Assert.Multiple(() =>
        {
            Assert.That(entMan.GetComponent<MetaDataComponent>(attached!.Value).EntityName, Is.EqualTo(PlayerBodyName));
            Assert.That(entMan.HasComponent<MobStateComponent>(attached.Value));
            Assert.That(entMan.GetComponent<TransformComponent>(attached.Value).MapID, Is.EqualTo(ticker.DefaultMap));
            Assert.That(entMan.GetComponent<TransformComponent>(attached.Value).ChildCount, Is.EqualTo(bodyChildren));
            Assert.That(mindSys.TryGetMind(userId, out _, out var mind));
            Assert.That(mind!.CurrentEntity, Is.EqualTo(attached));
        });

        // Mobs survive too.
        Assert.That(CountPrototypes(entMan, ["MobCorgi"]).GetValueOrDefault("MobCorgi"), Is.EqualTo(corgis),
            "Corgis were not saved and loaded exactly once");

        // A second save/load cycle must not duplicate anything.
        var entityCount = entMan.EntityCount;
        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);
        Assert.That(entMan.EntityCount, Is.InRange(entityCount - 50, entityCount + 50));
        Assert.That(CountPrototypes(entMan, ["MobCorgi"]).GetValueOrDefault("MobCorgi"), Is.EqualTo(corgis));

        await Cleanup(pair);
    }

    /// <summary>
    /// Mobs, borgs, mechs, organs etc. are marked "save: false" so they never get saved into map files, which means
    /// nothing else checks that they can actually be saved and loaded. World saves include them, so check them here.
    /// Any error logged while saving or loading fails the test.
    /// </summary>
    [Test]
    public async Task UnsavablePrototypesSurviveWorldSave()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var mapSys = entMan.System<SharedMapSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);

        var protos = protoMan.EnumeratePrototypes<EntityPrototype>()
            .Where(p => !p.Abstract && !p.MapSavable)
            .Where(p => !pair.IsTestPrototype(p))
            .Where(p => !p.Components.ContainsKey("MapGrid") && !p.Components.ContainsKey("RoomFill"))
            // Loose organs can't be spawned on their own without tripping unrelated asserts; bodies (and so their
            // organs) are still covered by the mobs.
            .Where(p => !p.Components.ContainsKey("Organ"))
            // Sounds are deliberately not saved.
            .Where(p => p.ID != "Audio" && !p.Components.ContainsKey("Audio"))
            // Nar'Sie / Ratvar eat the grid they're standing on, which has nothing to do with saving.
            .Where(p => !p.Components.ContainsKey("EventHorizon"))
            .Where(p => p.Categories.All(c => c.ID is not ("Spawner" or "Debug" or "GameRules")))
            .Select(p => p.ID)
            .ToList();

        Assert.That(protos, Is.Not.Empty);

        await server.WaitPost(() =>
        {
            // Each entity gets its own little grid far from the others, so mobs don't fight each other.
            for (var i = 0; i < protos.Count; i++)
            {
                var grid = mapSys.CreateGridEntity(ticker.DefaultMap);
                entMan.System<SharedTransformSystem>().SetWorldPosition(grid.Owner, new System.Numerics.Vector2(i * 50, 500));
                for (var x = -1; x <= 1; x++)
                {
                    for (var y = -1; y <= 1; y++)
                    {
                        mapSys.SetTile(grid.Owner, grid.Comp, new Vector2i(x, y), new Tile(1));
                    }
                }

                entMan.SpawnEntity(protos[i], new EntityCoordinates(grid.Owner, 0.5f, 0.5f));
            }
        });

        await pair.RunTicksSync(60);

        var before = CountPrototypes(entMan, protos);

        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);

        Assert.That(ticker.WorldRestoredFromSave, "The round did not load the world save");

        var after = CountPrototypes(entMan, protos);
        var missing = before
            .Where(kv => after.GetValueOrDefault(kv.Key) < kv.Value)
            .Select(kv => $"{kv.Key}: {kv.Value} -> {after.GetValueOrDefault(kv.Key)}")
            .ToList();

        Assert.That(missing, Is.Empty, "Entities were lost by the world save:\n" + string.Join("\n", missing));

        await Cleanup(pair);
    }

    private static Dictionary<string, int> CountPrototypes(IEntityManager entMan, List<string> protos)
    {
        var wanted = protos.ToHashSet();
        var counts = new Dictionary<string, int>();
        var query = entMan.AllEntityQueryEnumerator<MetaDataComponent>();
        while (query.MoveNext(out _, out var meta))
        {
            if (meta.EntityPrototype is { } proto && wanted.Contains(proto.ID))
                counts[proto.ID] = counts.GetValueOrDefault(proto.ID) + 1;
        }

        return counts;
    }

    private static async Task Cleanup(Pair.TestPair pair)
    {
        var server = pair.Server;
        var res = server.ResolveDependency<IResourceManager>();
        var ticker = server.ResolveDependency<IEntityManager>().System<ServerGameTicker>();

        // Turn persistence off and start a normal round so other tests aren't affected.
        server.CfgMan.SetCVar(CCVars.UsePersistence, false);
        await server.WaitPost(() =>
        {
            foreach (var path in new[] { SavePath, SavePath + ".bak" })
            {
                var resPath = new ResPath(path);
                if (res.UserData.Exists(resPath))
                    res.UserData.Delete(resPath);
            }

            ticker.RestartRound();
        });
        await pair.RunTicksSync(5);
    }
}
