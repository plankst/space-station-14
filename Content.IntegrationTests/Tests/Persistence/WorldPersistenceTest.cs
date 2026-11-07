using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Persistence;
using Content.Shared.CCVar;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
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

        // Loaded entities must still be able to find their solutions (lungs, bloodstream, stomach, beakers, ...).
        // Loading doesn't raise container insert events, which is what normally fills in the solution lookup.
        var broken = new List<string>();
        await server.WaitPost(() =>
        {
            var solutions = entMan.System<SharedSolutionContainerSystem>();
            var query = entMan.AllEntityQueryEnumerator<SolutionComponent, ContainedSolutionComponent>();
            while (query.MoveNext(out var uid, out var solution, out var contained))
            {
                if (!solutions.TryGetSolution(contained.Container, solution.Id, out var found) || found.Value.Owner != uid)
                    broken.Add($"{entMan.ToPrettyString(contained.Container)}: {solution.Id}");
            }
        });

        Assert.That(broken, Is.Empty, "Solutions can't be found after loading:\n" + string.Join("\n", broken.Take(20)));

        await Cleanup(pair);
    }

    /// <summary>
    /// A player put back into their body after a world load must be able to walk around.
    /// Movement input goes through the real client, the same way a player's key presses do.
    /// </summary>
    [Test]
    public async Task RestoredPlayerCanMove()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var playerMan = server.ResolveDependency<IPlayerManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var mindSys = entMan.System<SharedMindSystem>();
        var mapSys = entMan.System<SharedMapSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);

        var player = playerMan.Sessions.Single();
        await server.WaitPost(() =>
        {
            // Stand on a big patch of floor on the main map's grid.
            var gridQuery = entMan.AllEntityQueryEnumerator<Robust.Shared.Map.Components.MapGridComponent, TransformComponent>();
            EntityUid grid = default;
            while (gridQuery.MoveNext(out var uid, out var gridComp, out var xform))
            {
                if (xform.MapID != ticker.DefaultMap)
                    continue;

                grid = uid;
                for (var x = -10; x <= 10; x++)
                {
                    for (var y = -3; y <= 3; y++)
                    {
                        mapSys.SetTile(uid, gridComp, new Vector2i(x, y), new Tile(1));
                    }
                }

                break;
            }

            Assert.That(grid.IsValid(), "Test map has no grid");
            var body = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid, 0.5f, 0.5f));
            entMan.System<MetaDataSystem>().SetEntityName(body, PlayerBodyName);
            var mind = mindSys.CreateMind(player.UserId, PlayerBodyName);
            mindSys.TransferTo(mind, body);
        });
        await pair.RunTicksSync(10);

        Assert.That(await MovesWhenWalking(pair, player), "Player can't move even before saving (test setup problem)");

        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(30);

        Assert.That(ticker.WorldRestoredFromSave);
        Assert.That(entMan.GetComponent<MetaDataComponent>(player.AttachedEntity!.Value).EntityName, Is.EqualTo(PlayerBodyName));
        Assert.That(await MovesWhenWalking(pair, player), "Player can't move after their body was loaded from the world save");

        // Same again, but like a real server restart: the player is gone while the world is reloaded and reconnects later.
        var name = player.Name;
        var netManager = pair.Client.ResolveDependency<Robust.Shared.Network.IClientNetManager>();
        await pair.Client.WaitPost(() => netManager.ClientDisconnect("Disconnect command used."));
        await pair.RunTicksSync(5);

        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(30);
        Assert.That(ticker.WorldRestoredFromSave);

        await Task.WhenAll(pair.Client.WaitIdleAsync(), pair.Client.WaitIdleAsync());
        pair.Client.SetConnectTarget(server);
        await pair.Client.WaitPost(() => netManager.ClientConnect(null!, 0, name));
        await pair.RunTicksSync(30);

        player = playerMan.Sessions.Single();
        Assert.That(player.AttachedEntity, Is.Not.Null, "Reconnecting player was not put back into a body");
        Assert.That(entMan.GetComponent<MetaDataComponent>(player.AttachedEntity!.Value).EntityName, Is.EqualTo(PlayerBodyName));
        Assert.That(await MovesWhenWalking(pair, player), "Player can't move after reconnecting to a reloaded world");

        await Cleanup(pair);
    }

    /// <summary>
    /// With game.temp_playerfix on, a loaded player body is swapped for a clone that's offered as a ghost role,
    /// and a player who takes that role can walk around in it.
    /// </summary>
    [Test]
    public async Task TempPlayerFixMakesGhostRole()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var playerMan = server.ResolveDependency<IPlayerManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var mindSys = entMan.System<SharedMindSystem>();
        var mapSys = entMan.System<SharedMapSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);
        server.CfgMan.SetCVar(CCVars.TempPlayerFix, true);

        var player = playerMan.Sessions.Single();
        EntityUid original = default;
        await server.WaitPost(() =>
        {
            var gridQuery = entMan.AllEntityQueryEnumerator<Robust.Shared.Map.Components.MapGridComponent, TransformComponent>();
            while (gridQuery.MoveNext(out var uid, out var gridComp, out var xform))
            {
                if (xform.MapID != ticker.DefaultMap)
                    continue;

                for (var x = -10; x <= 10; x++)
                {
                    for (var y = -3; y <= 3; y++)
                    {
                        mapSys.SetTile(uid, gridComp, new Vector2i(x, y), new Tile(1));
                    }
                }

                original = entMan.SpawnEntity("MobHuman", new EntityCoordinates(uid, 0.5f, 0.5f));
                break;
            }

            entMan.System<MetaDataSystem>().SetEntityName(original, PlayerBodyName);
            var mind = mindSys.CreateMind(player.UserId, PlayerBodyName);
            mindSys.TransferTo(mind, original);
        });
        await pair.RunTicksSync(10);

        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(30);
        Assert.That(ticker.WorldRestoredFromSave);

        // The old body is gone, the player isn't in a body, and there is exactly one ghost role clone with the same name.
        var clones = new List<EntityUid>();
        await server.WaitPost(() =>
        {
            var q = entMan.AllEntityQueryEnumerator<Content.Server.Ghost.Roles.Components.GhostRoleComponent, MetaDataComponent>();
            while (q.MoveNext(out var uid, out _, out var meta))
            {
                if (meta.EntityName == PlayerBodyName &&
                    entMan.HasComponent<Content.Server.Ghost.Roles.Components.GhostTakeoverAvailableComponent>(uid))
                {
                    clones.Add(uid);
                }
            }
        });

        Assert.That(clones, Has.Count.EqualTo(1), "Expected exactly one ghost role clone of the player's body");
        Assert.That(entMan.GetComponent<MetaDataComponent>(player.AttachedEntity!.Value).EntityName, Is.Not.EqualTo(PlayerBodyName),
            "Player should not be in the replaced body");

        // Take the ghost role, then walk.
        await server.WaitPost(() =>
        {
            var role = entMan.GetComponent<Content.Server.Ghost.Roles.Components.GhostRoleComponent>(clones[0]);
            Assert.That(entMan.System<Content.Server.Ghost.Roles.GhostRoleSystem>().Takeover(player, role.Identifier));
        });
        await pair.RunTicksSync(10);

        Assert.That(player.AttachedEntity, Is.EqualTo(clones[0]));
        Assert.That(await MovesWhenWalking(pair, player), "Player can't move in the ghost role clone");

        server.CfgMan.SetCVar(CCVars.TempPlayerFix, false);
        await Cleanup(pair);
    }

    /// <summary>
    /// After loading, containers still show their liquid's color and dispensers still accept beakers.
    /// Then the same with game.temp_dispensefix on, which swaps each dispenser for a fresh copy of itself.
    /// </summary>
    [Test]
    public async Task LiquidsAndDispensersSurviveLoad()
    {
        const string beakerName = "Persistent Test Beaker";
        const string emptyBeakerName = "Persistent Test Empty Beaker";

        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var mapSys = entMan.System<SharedMapSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var solutions = entMan.System<SharedSolutionContainerSystem>();
        var appearance = entMan.System<SharedAppearanceSystem>();
        var slots = entMan.System<Content.Shared.Containers.ItemSlots.ItemSlotsSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);

        var rotation = Angle.FromDegrees(90);
        await server.WaitPost(() =>
        {
            var gridQuery = entMan.AllEntityQueryEnumerator<Robust.Shared.Map.Components.MapGridComponent, TransformComponent>();
            while (gridQuery.MoveNext(out var grid, out var gridComp, out var xform))
            {
                if (xform.MapID != ticker.DefaultMap)
                    continue;

                for (var x = -3; x <= 3; x++)
                {
                    for (var y = -3; y <= 3; y++)
                    {
                        mapSys.SetTile(grid, gridComp, new Vector2i(x, y), new Tile(1));
                    }
                }

                var dispenser = entMan.SpawnEntity("ChemDispenser", new EntityCoordinates(grid, 0.5f, 0.5f));
                xformSys.SetLocalRotation(dispenser, rotation);
                xformSys.AnchorEntity(dispenser);

                var beaker = entMan.SpawnEntity("Beaker", new EntityCoordinates(grid, 2.5f, 0.5f));
                entMan.System<MetaDataSystem>().SetEntityName(beaker, beakerName);
                Assert.That(solutions.TryGetSolution(beaker, "beaker", out var soln, out _));
                Assert.That(solutions.TryAddReagent(soln.Value, "Blood", 30));

                var empty = entMan.SpawnEntity("Beaker", new EntityCoordinates(grid, 2.5f, 1.5f));
                entMan.System<MetaDataSystem>().SetEntityName(empty, emptyBeakerName);
                break;
            }
        });
        await pair.RunTicksSync(10);

        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);
        Assert.That(ticker.WorldRestoredFromSave);

        EntityUid FindNamed(string name)
        {
            var q = entMan.AllEntityQueryEnumerator<MetaDataComponent>();
            while (q.MoveNext(out var uid, out var meta))
            {
                if (meta.EntityName == name)
                    return uid;
            }

            return EntityUid.Invalid;
        }

        EntityUid FindDispenser()
        {
            var q = entMan.AllEntityQueryEnumerator<Content.Server.Chemistry.Components.ReagentDispenserComponent, TransformComponent>();
            while (q.MoveNext(out var uid, out _, out var xform))
            {
                if (xform.MapID == ticker.DefaultMap)
                    return uid;
            }

            return EntityUid.Invalid;
        }

        await server.WaitPost(() =>
        {
            // 1. The liquid's color and fill level are back on the loaded beaker.
            var beaker = FindNamed(beakerName);
            Assert.That(solutions.TryGetSolution(beaker, "beaker", out var soln, out var solution));
            Assert.That(appearance.TryGetData<Color>(beaker, Content.Shared.Chemistry.SolutionContainerVisuals.Color, out var color),
                "Loaded beaker has no liquid color");
            Assert.That(color, Is.EqualTo(solution.GetColor(server.ProtoMan)));
            Assert.That(appearance.TryGetData<float>(beaker, Content.Shared.Chemistry.SolutionContainerVisuals.FillFraction, out var fill));
            Assert.That(fill, Is.GreaterThan(0f));

            // 2. The loaded dispenser still has its beaker slot and accepts a beaker.
            var dispenser = FindDispenser();
            Assert.That(dispenser.IsValid(), "Dispenser was lost");
            Assert.That(slots.TryInsert(dispenser, Content.Shared.Chemistry.SharedReagentDispenser.OutputSlotName, FindNamed(emptyBeakerName), null),
                "Loaded dispenser refuses beakers");
        });

        // 3. temp_dispensefix: the dispenser is swapped for a copy with the same place, rotation, anchoring and contents.
        var jugs = 0;
        EntityCoordinates coords = default;
        Angle loadedRotation = default;
        await server.WaitPost(() =>
        {
            var dispenser = FindDispenser();
            coords = entMan.GetComponent<TransformComponent>(dispenser).Coordinates;
            loadedRotation = entMan.GetComponent<TransformComponent>(dispenser).LocalRotation;
            jugs = entMan.GetComponent<Robust.Shared.Containers.ContainerManagerComponent>(dispenser).Containers.Values
                .Sum(c => c.ContainedEntities.Count);
        });

        server.CfgMan.SetCVar(CCVars.TempDispenseFix, true);
        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);
        server.CfgMan.SetCVar(CCVars.TempDispenseFix, false);

        await server.WaitPost(() =>
        {
            var count = 0;
            var q = entMan.AllEntityQueryEnumerator<Content.Server.Chemistry.Components.ReagentDispenserComponent, TransformComponent>();
            while (q.MoveNext(out _, out _, out var x))
            {
                if (x.MapID == ticker.DefaultMap)
                    count++;
            }

            Assert.That(count, Is.EqualTo(1), "temp_dispensefix should leave exactly one dispenser");
            var dispenser = FindDispenser();
            var xform = entMan.GetComponent<TransformComponent>(dispenser);
            Assert.That(xform.Anchored);
            Assert.That(xform.LocalRotation.EqualsApprox(loadedRotation), $"Rotation changed: {loadedRotation} -> {xform.LocalRotation}");
            Assert.That(xform.Coordinates.Position, Is.EqualTo(coords.Position));
            var contents = entMan.GetComponent<Robust.Shared.Containers.ContainerManagerComponent>(dispenser).Containers.Values
                .Sum(c => c.ContainedEntities.Count);
            Assert.That(contents, Is.EqualTo(jugs), "Dispenser contents weren't moved over 1:1");
            Assert.That(slots.TryGetSlot(dispenser, Content.Shared.Chemistry.SharedReagentDispenser.OutputSlotName, out var slot) && slot.Item != null,
                "The beaker in the dispenser wasn't moved over");
        });

        await Cleanup(pair);
    }

    /// <summary>
    /// A ChemMaster with a pill canister in its output slot used to crash the whole world load.
    /// </summary>
    [Test]
    public async Task ChemMasterWithPillCanisterLoads()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var mapSys = entMan.System<SharedMapSystem>();
        var slots = entMan.System<Content.Shared.Containers.ItemSlots.ItemSlotsSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);

        await server.WaitPost(() =>
        {
            var gridQuery = entMan.AllEntityQueryEnumerator<Robust.Shared.Map.Components.MapGridComponent, TransformComponent>();
            while (gridQuery.MoveNext(out var grid, out var gridComp, out var xform))
            {
                if (xform.MapID != ticker.DefaultMap)
                    continue;

                mapSys.SetTile(grid, gridComp, Vector2i.Zero, new Tile(1));
                // The ChemMaster is created first, like in the save that crashed.
                var chemMaster = entMan.SpawnEntity("ChemMaster", new EntityCoordinates(grid, 0.5f, 0.5f));
                var canister = entMan.SpawnEntity("PillCanister", new EntityCoordinates(grid, 0.5f, 0.5f));
                Assert.That(slots.TryInsert(chemMaster, Content.Shared.Chemistry.SharedChemMaster.OutputSlotName, canister, null));
                break;
            }
        });
        await pair.RunTicksSync(5);

        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);

        Assert.That(ticker.WorldRestoredFromSave, "World save with a pill canister in a ChemMaster failed to load");
        await server.WaitPost(() =>
        {
            var found = false;
            var q = entMan.AllEntityQueryEnumerator<Content.Server.Chemistry.Components.ChemMasterComponent>();
            while (q.MoveNext(out var uid, out _))
            {
                if (slots.TryGetSlot(uid, Content.Shared.Chemistry.SharedChemMaster.OutputSlotName, out var slot) && slot.Item != null)
                    found = true;
            }

            Assert.That(found, "The pill canister should still be in the ChemMaster after loading");
        });

        await Cleanup(pair);
    }

    /// <summary>
    /// Holds the "move right" key on the client for a second and reports whether the player's entity moved.
    /// </summary>
    private static async Task<bool> MovesWhenWalking(Pair.TestPair pair, Robust.Shared.Player.ICommonSession player)
    {
        var server = pair.Server;
        var client = pair.Client;
        var entMan = server.ResolveDependency<IEntityManager>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var body = player.AttachedEntity!.Value;
        var start = xformSys.GetWorldPosition(body);

        var input = client.ResolveDependency<IEntityManager>().System<Robust.Client.GameObjects.InputSystem>();
        var inputMan = client.ResolveDependency<Robust.Client.Input.IInputManager>();
        var timing = client.ResolveDependency<Robust.Shared.Timing.IGameTiming>();
        var session = client.ResolveDependency<Robust.Client.Player.IPlayerManager>().LocalSession!;

        async Task Press(Robust.Shared.Input.BoundKeyState state)
        {
            await client.WaitPost(() =>
            {
                var funcId = inputMan.NetworkBindMap.KeyFunctionID(Robust.Shared.Input.EngineKeyFunctions.MoveRight);
                var msg = new Robust.Shared.Input.ClientFullInputCmdMessage(timing.CurTick, timing.TickFraction, funcId)
                {
                    State = state,
                };
                input.HandleInputCommand(session, Robust.Shared.Input.EngineKeyFunctions.MoveRight, msg);
            });
        }

        await Press(Robust.Shared.Input.BoundKeyState.Down);
        await pair.RunTicksSync(30);
        await Press(Robust.Shared.Input.BoundKeyState.Up);
        await pair.RunTicksSync(5);

        var end = xformSys.GetWorldPosition(body);
        TestContext.Out.WriteLine($"Moved from {start} to {end}");
        return (end - start).Length() > 0.5f;
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
