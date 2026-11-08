using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Persistence;
using Content.Server.Salvage.Expeditions;
using Content.Shared.Salvage.Expeditions;
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
    /// After loading, containers still show their liquid's color and dispensers still accept beakers.
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
    /// Things that only lived in non-saved runtime state must come back right after a load:
    /// - a UI that was open when the world was saved must not stay "open" (the first click on a dispenser closed it);
    /// - lathes must have their animation flags set (otherwise the insertion animation loops forever);
    /// - painted lockers keep their paint.
    /// </summary>
    [Test]
    public async Task UiLatheAndPaintSurviveLoad()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var playerMan = server.ResolveDependency<IPlayerManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var mindSys = entMan.System<SharedMindSystem>();
        var mapSys = entMan.System<SharedMapSystem>();
        var ui = entMan.System<SharedUserInterfaceSystem>();
        var appearance = entMan.System<SharedAppearanceSystem>();
        var interaction = entMan.System<Content.Shared.Interaction.SharedInteractionSystem>();
        var player = playerMan.Sessions.Single();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);

        const string paint = "LockerCaptain";
        await server.WaitPost(() =>
        {
            var q = entMan.AllEntityQueryEnumerator<Robust.Shared.Map.Components.MapGridComponent, TransformComponent>();
            while (q.MoveNext(out var grid, out var gridComp, out var xform))
            {
                if (xform.MapID != ticker.DefaultMap)
                    continue;

                for (var x = -3; x <= 3; x++)
                {
                    mapSys.SetTile(grid, gridComp, new Vector2i(x, 0), new Tile(1));
                }

                var dispenser = entMan.SpawnEntity("ChemDispenser", new EntityCoordinates(grid, 1.5f, 0.5f));
                entMan.GetComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(dispenser).NeedsPower = false;
                entMan.SpawnEntity("Autolathe", new EntityCoordinates(grid, -1.5f, 0.5f));

                // Paint a locker the same way the spray painter does.
                var locker = entMan.SpawnEntity("LockerSteel", new EntityCoordinates(grid, -2.5f, 0.5f));
                appearance.SetData(locker, Content.Shared.SprayPainter.Prototypes.PaintableVisuals.Prototype, paint);
                entMan.EnsureComponent<Content.Shared.SprayPainter.Components.PaintedComponent>(locker).Prototype = paint;

                var body = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid, 0.5f, 0.5f));
                var mind = mindSys.CreateMind(player.UserId, "Persistent UI Tester");
                mindSys.TransferTo(mind, body);
                break;
            }
        });
        await pair.RunTicksSync(10);

        T Find<T>() where T : IComponent
        {
            var q = entMan.AllEntityQueryEnumerator<T, TransformComponent>();
            while (q.MoveNext(out var uid, out var comp, out var xform))
            {
                if (xform.MapID == ticker.DefaultMap)
                    return comp;
            }

            return default!;
        }

        // Leave the dispenser UI open when the world is saved.
        var useDelay = entMan.System<Content.Shared.Timing.Systems.UseDelaySystem>();
        await server.WaitPost(() =>
        {
            var dispenser = Find<Content.Server.Chemistry.Components.ReagentDispenserComponent>().Owner;
            interaction.InteractHand(player.AttachedEntity!.Value, dispenser);
            Assert.That(ui.IsUiOpen(dispenser, Content.Shared.Chemistry.ReagentDispenserUiKey.Key, player.AttachedEntity!.Value));

            // Simulate a use-delay saved by an earlier server run: its end time is hours past "now" (the game clock
            // restarts from zero after a reboot). This made dispensers ignore left-click/E after loading.
            useDelay.SetLength(dispenser, TimeSpan.FromHours(10));
            useDelay.TryResetDelay(dispenser);
            useDelay.SetLength(dispenser, TimeSpan.Zero);
            Assert.That(useDelay.IsDelayed(dispenser));

            // Hold an item, which would otherwise be lost by the save.
            var crowbar = entMan.SpawnEntity("Crowbar", entMan.GetComponent<TransformComponent>(player.AttachedEntity!.Value).Coordinates);
            Assert.That(entMan.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>().TryPickupAnyHand(player.AttachedEntity!.Value, crowbar));
        });
        await pair.RunTicksSync(5);

        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);
        Assert.That(ticker.WorldRestoredFromSave);

        await server.WaitPost(() =>
        {
            // The first click opens the dispenser UI instead of "closing" the one left open in the save.
            var dispenser = Find<Content.Server.Chemistry.Components.ReagentDispenserComponent>().Owner;
            var user = player.AttachedEntity!.Value;
            Assert.That(ui.IsUiOpen(dispenser, Content.Shared.Chemistry.ReagentDispenserUiKey.Key, user), Is.False,
                "UI left open in the save should be closed after loading");
            Assert.That(useDelay.IsDelayed(dispenser), Is.False, "Stale use-delay from the save still blocks the dispenser");

            // The held crowbar was put on the floor instead of being lost.
            var crowbars = 0;
            var cq = entMan.AllEntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (cq.MoveNext(out var cUid, out var cMeta, out var cXform))
            {
                if (cMeta.EntityPrototype?.ID == "Crowbar" && cXform.MapID == ticker.DefaultMap)
                {
                    crowbars++;
                    Assert.That(entMan.System<Robust.Shared.Containers.SharedContainerSystem>().IsEntityInContainer(cUid), Is.False);
                }
            }
            Assert.That(crowbars, Is.EqualTo(1), "Held item was lost by the save");
            interaction.InteractHand(user, dispenser);
            Assert.That(ui.IsUiOpen(dispenser, Content.Shared.Chemistry.ReagentDispenserUiKey.Key, user),
                "First click on a loaded dispenser didn't open its UI");

            // Lathe animation flags are set, and nothing is being inserted.
            var lathe = Find<Content.Shared.Lathe.LatheComponent>().Owner;
            Assert.That(appearance.TryGetData<bool>(lathe, Content.Shared.Materials.MaterialStorageVisuals.Inserting, out var inserting) && !inserting,
                "Loaded lathe is missing its 'not inserting' flag");
            Assert.That(appearance.TryGetData<bool>(lathe, Content.Shared.Lathe.LatheVisuals.IsInserting, out var latheInserting) && !latheInserting);
            Assert.That(appearance.TryGetData<bool>(lathe, Content.Shared.Lathe.LatheVisuals.IsRunning, out _));

            // The locker kept its paint.
            var painted = Find<Content.Shared.SprayPainter.Components.PaintedComponent>().Owner;
            Assert.That(appearance.TryGetData<string>(painted, Content.Shared.SprayPainter.Prototypes.PaintableVisuals.Prototype, out var style),
                "Painted locker lost its paint");
            Assert.That(style, Is.EqualTo(paint));
        });

        await Cleanup(pair);
    }

    /// <summary>
    /// Holds the "move right" key on the client for a second and reports whether the player's entity moved.
    /// </summary>
    /// <summary>
    /// Like <see cref="RestoredPlayerCanMove"/>, but the save is loaded by a brand-new server, like a real reboot.
    /// A new server's clock starts from zero, which an in-process round restart never shows.
    /// </summary>
    [Test]
    public async Task RestoredPlayerCanMoveAfterReboot()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var playerMan = server.ResolveDependency<IPlayerManager>();
        var res = server.ResolveDependency<IResourceManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var mindSys = entMan.System<SharedMindSystem>();
        var mapSys = entMan.System<SharedMapSystem>();
        var persistence = entMan.System<WorldPersistenceSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, false);

        var player = playerMan.Sessions.Single();
        await server.WaitPost(() =>
        {
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
            // Gravity, so the player doesn't drift off while we wait.
            var gravity = entMan.EnsureComponent<Content.Shared.Gravity.GravityComponent>(grid);
            entMan.System<Content.Server.Gravity.GravitySystem>().EnableGravity(grid, gravity);
            var body = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid, 0.5f, 0.5f));
            entMan.System<MetaDataSystem>().SetEntityName(body, PlayerBodyName);
            var mind = mindSys.CreateMind(player.UserId, PlayerBodyName);
            mindSys.TransferTo(mind, body);
        });

        // Play for a while first, so anything saved as an absolute time is well ahead of a fresh server's clock.
        await pair.RunTicksSync(10);
        Assert.That(await MovesWhenWalking(pair, player), "Player can't move even before saving (test setup problem)");
        await pair.RunTicksSync(900);
        Assert.That(await MovesWhenWalking(pair, player), "Player can't move before saving (test setup problem)");

        string save = default!;
        await server.WaitPost(() =>
        {
            entMan.System<WorldPersistenceSystem>().DropPlayersHeldItems();
            Assert.That(persistence.SaveWorld("test reboot", force: true));
            using var reader = res.UserData.OpenText(new ResPath(SavePath));
            save = reader.ReadToEnd();
        });
        var oldUserId = player.UserId.UserId.ToString();
        await Cleanup(pair);

        // "Reboot": a brand-new server and client.
        var pair2 = await PoolManager.GetServerClient(new PoolSettings
        {
            DummyTicker = false,
            Connected = true,
            Fresh = true,
        });
        var server2 = pair2.Server;
        var entMan2 = server2.ResolveDependency<IEntityManager>();
        var ticker2 = entMan2.System<ServerGameTicker>();
        var player2 = server2.ResolveDependency<IPlayerManager>().Sessions.Single();
        TestContext.Out.WriteLine($"Old server tick {server.Timing.CurTick}, new server tick {server2.Timing.CurTick}");

        server2.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server2.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server2.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server2.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, false);

        await server2.WaitPost(() =>
        {
            // The test client on the new server has a different account, so give it the saved player's mind.
            var res2 = server2.ResolveDependency<IResourceManager>();
            res2.UserData.CreateDir(new ResPath(SavePath).Directory);
            using var writer = res2.UserData.OpenWriteText(new ResPath(SavePath));
            writer.Write(save.Replace(oldUserId, player2.UserId.UserId.ToString()));
        });

        await server2.WaitPost(() => ticker2.RestartRound());
        await pair2.RunTicksSync(30);

        Assert.That(ticker2.WorldRestoredFromSave, "The new server did not load the world save");
        Assert.That(player2.AttachedEntity, Is.Not.Null, "Player was not put back into their body");
        Assert.That(entMan2.GetComponent<MetaDataComponent>(player2.AttachedEntity!.Value).EntityName, Is.EqualTo(PlayerBodyName));

        // Anything that recalculates movement speed (taking damage, picking something up...) used to leave a loaded
        // mob at zero speed: the hunger/thirst speed modifier wasn't recalculated after loading.
        await server2.WaitPost(() =>
        {
            var body2 = player2.AttachedEntity!.Value;
            // Walk on the ground (weightless movement ignores speed modifiers).
            var grid2 = entMan2.GetComponent<TransformComponent>(body2).GridUid!.Value;
            var gravity2 = entMan2.EnsureComponent<Content.Shared.Gravity.GravityComponent>(grid2);
            entMan2.System<Content.Server.Gravity.GravitySystem>().EnableGravity(grid2, gravity2);
            entMan2.System<Content.Shared.Movement.Systems.MovementSpeedModifierSystem>().RefreshMovementSpeedModifiers(body2);
        });
        await pair2.RunTicksSync(2);
        Assert.That(await MovesWhenWalking(pair2, player2), "Player can't move after their body was loaded by a new server");

        await Cleanup(pair2);
        await pair2.CleanReturnAsync();
    }

    /// <summary>
    /// After loading, a PDA must still report the ID inside it, otherwise the player loses all door access.
    /// Also, a coordinates disk pointing at something that's gone must not leave a broken reference in the save.
    /// </summary>
    [Test]
    public async Task PdaAccessAndDisksSurviveLoad()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var mapSys = entMan.System<SharedMapSystem>();
        var access = entMan.System<Content.Shared.Access.Systems.AccessReaderSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);

        const string bodyName = "Persistent Test Captain";
        const string diskName = "Persistent Test Disk";
        await server.WaitPost(() =>
        {
            var coords = new MapCoordinates(0, 0, ticker.DefaultMap);
            var body = entMan.SpawnEntity("MobHuman", coords);
            entMan.System<MetaDataSystem>().SetEntityName(body, bodyName);
            var pda = entMan.SpawnEntity("CaptainPDA", coords);
            Assert.That(entMan.System<Content.Shared.Inventory.InventorySystem>().TryEquip(body, pda, "id", silent: true, force: true));
            Assert.That(access.FindAccessTags(body), Does.Contain(new ProtoId<Content.Shared.Access.AccessLevelPrototype>("Captain")),
                "Test setup: the captain should have captain access before saving");

            // A disk pointing at a map that has since been deleted.
            var gone = mapSys.CreateMap(out _);
            var disk = entMan.SpawnEntity("CoordinatesDisk", coords);
            entMan.System<MetaDataSystem>().SetEntityName(disk, diskName);
            entMan.GetComponent<Content.Shared.Shuttles.Components.ShuttleDestinationCoordinatesComponent>(disk).Destination = gone;
            entMan.DeleteEntity(gone);
        });
        await pair.RunTicksSync(5);

        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);

        Assert.That(ticker.WorldRestoredFromSave);
        EntityUid? body = null;
        var tags = new List<string>();
        var disks = -1;
        await server.WaitPost(() =>
        {
            // Match the body itself, not its identity entity (which carries the same name).
            var query = entMan.AllEntityQueryEnumerator<Content.Shared.Inventory.InventoryComponent, MetaDataComponent>();
            while (query.MoveNext(out var uid, out _, out var meta))
            {
                if (meta.EntityName == bodyName)
                    body = uid;
            }

            if (body != null)
                tags.AddRange(access.FindAccessTags(body.Value).Select(t => t.Id));

            disks = entMan.Count<Content.Shared.Shuttles.Components.ShuttleDestinationCoordinatesComponent>();
        });

        Assert.That(body, Is.Not.Null, "The captain wasn't saved");
        Assert.That(tags, Does.Contain("Captain"), "The ID in the PDA lost its access after loading");
        // Coordinates disks are removed after loading (they only lead to expeditions, which aren't saved), and the
        // broken reference must not have logged an error while loading (any error fails the test).
        Assert.That(disks, Is.Zero);

        await Cleanup(pair);
    }

    /// <summary>
    /// Salvage expeditions and FTL travel keep their state in fields that aren't saved, so:
    /// - expeditions are left out of saves, with players and shuttles on them brought home first;
    /// - coordinates disks are removed and expedition consoles offer fresh missions after loading;
    /// - a shuttle saved in the middle of an FTL jump finishes it instead of being stuck in hyperspace.
    /// </summary>
    [Test]
    public async Task ExpeditionsAndFtlSurviveLoad()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var playerMan = server.ResolveDependency<IPlayerManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var mapSys = entMan.System<SharedMapSystem>();
        var mindSys = entMan.System<SharedMindSystem>();
        var shuttleSys = entMan.System<Content.Server.Shuttles.Systems.ShuttleSystem>();
        var stationSys = entMan.System<Content.Shared.Station.Systems.StationSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);

        const string explorerName = "Persistent Test Explorer";
        const string salvageShuttleName = "Persistent Test Salvage Shuttle";
        const string cargoShuttleName = "Persistent Test Cargo Shuttle";
        var player = playerMan.Sessions.Single();
        EntityUid cargoShuttle = default;

        await server.WaitPost(() =>
        {
            var station = stationSys.GetStations().First().Owner;
            var data = entMan.EnsureComponent<SalvageExpeditionDataComponent>(station);
            data.ActiveMission = 5;
            data.Cooldown = true;

            // An expedition with a shuttle that flew there and a player standing on it.
            var expedition = mapSys.CreateMap(out var expeditionMapId);
            var expeditionComp = entMan.AddComponent<SalvageExpeditionComponent>(expedition);
            expeditionComp.Station = station;
            expeditionComp.EndTime = server.Timing.CurTime + TimeSpan.FromHours(1);
            var salvageShuttle = MakeShuttle(expeditionMapId, salvageShuttleName);
            var explorer = entMan.SpawnEntity("MobHuman", new MapCoordinates(30, 30, expeditionMapId));
            entMan.System<MetaDataSystem>().SetEntityName(explorer, explorerName);
            var mind = mindSys.CreateMind(player.UserId, explorerName);
            mindSys.TransferTo(mind, explorer);

            var disk = entMan.SpawnEntity("CoordinatesDisk", new MapCoordinates(0, 0, ticker.DefaultMap));
            entMan.GetComponent<Content.Shared.Shuttles.Components.ShuttleDestinationCoordinatesComponent>(disk).Destination = expedition;

            // A cargo shuttle that will be in the middle of an FTL jump when the world is saved.
            cargoShuttle = MakeShuttle(ticker.DefaultMap, cargoShuttleName);
            entMan.System<SharedTransformSystem>().SetWorldPosition(cargoShuttle, new System.Numerics.Vector2(200, 200));
            shuttleSys.FTLToCoordinates(cargoShuttle, entMan.GetComponent<Content.Shared.Shuttles.Components.ShuttleComponent>(cargoShuttle),
                new EntityCoordinates(mapSys.GetMap(ticker.DefaultMap), 300, 300), Angle.Zero, startupTime: 0.1f, hyperspaceTime: 600f);
        });
        await pair.RunTicksSync(30);

        await server.WaitPost(() =>
        {
            var xform = entMan.GetComponent<TransformComponent>(cargoShuttle);
            Assert.That(entMan.HasComponent<Content.Shared.Shuttles.Components.FTLMapComponent>(xform.MapUid),
                "Test setup: the cargo shuttle should be in hyperspace before saving");
        });

        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(30);
        Assert.That(ticker.WorldRestoredFromSave);

        await server.WaitPost(() =>
        {
            Assert.That(entMan.Count<SalvageExpeditionComponent>(), Is.Zero, "Expeditions should not be saved");
            Assert.That(entMan.Count<Content.Shared.Shuttles.Components.ShuttleDestinationCoordinatesComponent>(), Is.Zero,
                "Coordinates disks should be removed after loading");

            var station = stationSys.GetStations().First().Owner;
            var data = entMan.GetComponent<SalvageExpeditionDataComponent>(station);
            Assert.That(data.ActiveMission, Is.Zero);
            Assert.That(data.Cooldown, Is.False);
            Assert.That(data.Missions, Is.Not.Empty, "The expedition console should offer fresh missions");

            var found = new Dictionary<string, EntityUid>();
            var query = entMan.AllEntityQueryEnumerator<MetaDataComponent>();
            while (query.MoveNext(out var uid, out var meta))
            {
                if (meta.EntityName.StartsWith(explorerName))
                    found[explorerName] = uid;
                else if (meta.EntityName == salvageShuttleName || meta.EntityName == cargoShuttleName)
                    found[meta.EntityName] = uid;
            }

            Assert.That(found.Keys, Is.EquivalentTo(new[] { explorerName, salvageShuttleName, cargoShuttleName }));
            foreach (var (name, uid) in found)
            {
                Assert.That(entMan.GetComponent<TransformComponent>(uid).MapID, Is.EqualTo(ticker.DefaultMap),
                    $"{name} should be on the main map after loading");
            }
        });

        await Cleanup(pair);
        return;

        EntityUid MakeShuttle(MapId mapId, string name)
        {
            var grid = mapSys.CreateGridEntity(mapId);
            for (var x = 0; x < 3; x++)
            {
                for (var y = 0; y < 3; y++)
                {
                    mapSys.SetTile(grid, new Vector2i(x, y), new Tile(1));
                }
            }

            entMan.EnsureComponent<Content.Shared.Shuttles.Components.ShuttleComponent>(grid);
            entMan.System<MetaDataSystem>().SetEntityName(grid, name);
            return grid;
        }
    }

    /// <summary>
    /// The first load after the server starts copies the save to World_Backup.yml, and autosaves go to
    /// World_Autosave.yml without touching the main save.
    /// </summary>
    [Test]
    public async Task StartupBackupAndSeparateAutosave()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var res = server.ResolveDependency<IResourceManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var persistence = entMan.System<WorldPersistenceSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);

        Assert.Multiple(() =>
        {
            Assert.That(persistence.AutosavePath, Is.EqualTo(new ResPath("/persistence/WorldPersistenceTest_Autosave.yml")));
            Assert.That(persistence.BackupPath, Is.EqualTo(new ResPath("/persistence/WorldPersistenceTest_Backup.yml")));
        });

        string ReadAll(ResPath path)
        {
            using var reader = res.UserData.OpenText(path);
            return reader.ReadToEnd();
        }

        // Pretend the server just started (the test server has usually loaded a world already).
        typeof(WorldPersistenceSystem)
            .GetField("_startupBackupDone", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(persistence, false);

        // The round restart saves World.yml and then loads it, which makes the startup backup.
        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);
        Assert.That(ticker.WorldRestoredFromSave);

        var mainSave = string.Empty;
        await server.WaitPost(() =>
        {
            Assert.That(res.UserData.Exists(persistence.BackupPath), "No backup was made when the world was first loaded");
            mainSave = ReadAll(persistence.SavePath);
            Assert.That(ReadAll(persistence.BackupPath), Is.EqualTo(mainSave), "The backup should be a copy of the save");

            // Autosaves only write World_Autosave.yml, twice in a row to check it's replaced, not backed up.
            Assert.That(persistence.SaveWorld("autosave", autosave: true));
            Assert.That(persistence.SaveWorld("autosave", autosave: true));
            Assert.That(res.UserData.Exists(persistence.AutosavePath), "The autosave wasn't written");
            Assert.That(res.UserData.Exists(new ResPath(persistence.AutosavePath + ".bak")), Is.False,
                "Autosaves should be a single file");
            Assert.That(ReadAll(persistence.SavePath), Is.EqualTo(mainSave), "An autosave must not change World.yml");
        });

        await server.WaitPost(() =>
        {
            foreach (var path in new[] { persistence.AutosavePath, persistence.BackupPath })
            {
                if (res.UserData.Exists(path))
                    res.UserData.Delete(path);
            }
        });

        await Cleanup(pair);
    }

    /// <summary>
    /// Autosaves happen on their timer while a player is online, and not at all while nobody is.
    /// </summary>
    [Test]
    public async Task AutosaveOnlyWithPlayersOnline()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var res = server.ResolveDependency<IResourceManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var persistence = entMan.System<WorldPersistenceSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, false);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0.5f);

        bool AutosaveExists()
        {
            persistence.WaitForPendingWrite();
            return res.UserData.Exists(persistence.AutosavePath);
        }

        void DeleteAutosave()
        {
            persistence.WaitForPendingWrite();
            if (res.UserData.Exists(persistence.AutosavePath))
                res.UserData.Delete(persistence.AutosavePath);
        }

        await server.WaitPost(DeleteAutosave);

        // A player is online: the autosave happens.
        await pair.RunTicksSync(60);
        var saved = false;
        await server.WaitPost(() => saved = AutosaveExists());
        Assert.That(saved, "No autosave happened with a player online");

        // Nobody online: no autosave.
        var name = pair.Player!.Name;
        var netManager = pair.Client.ResolveDependency<Robust.Shared.Network.IClientNetManager>();
        await pair.Client.WaitPost(() => netManager.ClientDisconnect("Disconnect command used."));
        await pair.RunTicksSync(5);
        await server.WaitPost(DeleteAutosave);
        await pair.RunTicksSync(60);
        await server.WaitPost(() => saved = AutosaveExists());
        Assert.That(saved, Is.False, "Autosaved while nobody was online");

        // Reconnect so the pair is usable again.
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        await Task.WhenAll(pair.Client.WaitIdleAsync(), pair.Server.WaitIdleAsync());
        pair.Client.SetConnectTarget(server);
        await pair.Client.WaitPost(() => netManager.ClientConnect(null!, 0, name));
        await pair.RunTicksSync(30);
        await server.WaitPost(DeleteAutosave);

        await Cleanup(pair);
    }

    /// <summary>
    /// Linked devices keep working after a load: a button still opens its blast door and a lever still runs its
    /// conveyor, used by hand the way a player would.
    /// </summary>
    [Test]
    public async Task LinkedDevicesWorkAfterLoad()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var mapSys = entMan.System<SharedMapSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var links = entMan.System<Content.Shared.DeviceLinking.Systems.DeviceLinkSystem>();
        var interaction = entMan.System<Content.Shared.Interaction.SharedInteractionSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);

        const string userName = "Persistent Test Engineer";
        var names = new[] { "Persistent Test Button", "Persistent Test Door", "Persistent Test Lever", "Persistent Test Conveyor" };

        await server.WaitPost(() =>
        {
            var gridQuery = entMan.AllEntityQueryEnumerator<Robust.Shared.Map.Components.MapGridComponent, TransformComponent>();
            while (gridQuery.MoveNext(out var grid, out var gridComp, out var xform))
            {
                if (xform.MapID != ticker.DefaultMap)
                    continue;

                for (var x = 0; x <= 6; x++)
                {
                    mapSys.SetTile(grid, gridComp, new Vector2i(x, 0), new Tile(1));
                }

                EntityUid Spawn(string proto, string name, float x, bool anchor)
                {
                    var uid = entMan.SpawnEntity(proto, new EntityCoordinates(grid, x, 0.5f));
                    entMan.System<MetaDataSystem>().SetEntityName(uid, name);
                    if (anchor)
                        xformSys.AnchorEntity(uid);
                    return uid;
                }

                var user = Spawn("MobHuman", userName, 2.5f, false);
                var button = Spawn("SignalButton", names[0], 1.5f, true);
                var door = Spawn("BlastDoor", names[1], 0.5f, true);
                var lever = Spawn("TwoWayLever", names[2], 3.5f, true);
                var conveyor = Spawn("ConveyorBelt", names[3], 4.5f, true);
                links.LinkDefaults(null, button, door);
                links.LinkDefaults(null, lever, conveyor);
                break;
            }
        });
        await pair.RunTicksSync(10);

        Dictionary<string, EntityUid> Find()
        {
            var found = new Dictionary<string, EntityUid>();
            var q = entMan.AllEntityQueryEnumerator<MetaDataComponent>();
            while (q.MoveNext(out var uid, out var meta))
            {
                // Skip identity entities, which carry the same name as their owner.
                if ((meta.EntityName == userName && entMan.HasComponent<Content.Shared.Inventory.InventoryComponent>(uid))
                    || (names.Contains(meta.EntityName) && entMan.HasComponent<TransformComponent>(uid) && meta.EntityPrototype != null))
                {
                    found[meta.EntityName] = uid;
                }
            }

            return found;
        }

        // Press the button and pull the lever, by hand, and report what the door and conveyor did.
        async Task<(Content.Shared.Doors.Components.DoorState Door, Content.Shared.Conveyor.ConveyorState Conveyor)> UseDevices()
        {
            Dictionary<string, EntityUid> found = default!;
            await server.WaitPost(() =>
            {
                found = Find();
                Assert.That(found.Keys, Is.SupersetOf(names.Append(userName)), "Some test devices are missing");
                interaction.InteractHand(found[userName], found[names[0]]);
                interaction.InteractHand(found[userName], found[names[2]]);
            });
            await pair.RunTicksSync(5);

            (Content.Shared.Doors.Components.DoorState, Content.Shared.Conveyor.ConveyorState) result = default;
            await server.WaitPost(() => result = (
                entMan.GetComponent<Content.Shared.Doors.Components.DoorComponent>(found[names[1]]).State,
                entMan.GetComponent<Content.Shared.Conveyor.ConveyorComponent>(found[names[3]]).State));
            return result;
        }

        var before = await UseDevices();
        Assert.That(before.Door, Is.Not.EqualTo(Content.Shared.Doors.Components.DoorState.Closed), "Test setup: the button should open the door");
        Assert.That(before.Conveyor, Is.Not.EqualTo(Content.Shared.Conveyor.ConveyorState.Off), "Test setup: the lever should start the conveyor");

        // Put everything back to rest before saving, so after loading we can see the devices react again.
        await pair.RunTicksSync(90);
        await server.WaitPost(() =>
        {
            var found = Find();
            interaction.InteractHand(found[userName], found[names[0]]);
            interaction.InteractHand(found[userName], found[names[2]]);
        });
        await pair.RunTicksSync(90);

        var stateBeforeSave = default((Content.Shared.Doors.Components.DoorState, Content.Shared.Conveyor.ConveyorState));
        await server.WaitPost(() =>
        {
            var found = Find();
            stateBeforeSave = (entMan.GetComponent<Content.Shared.Doors.Components.DoorComponent>(found[names[1]]).State,
                entMan.GetComponent<Content.Shared.Conveyor.ConveyorComponent>(found[names[3]]).State);
        });

        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);
        Assert.That(ticker.WorldRestoredFromSave);

        await pair.RunTicksSync(60);
        var after = await UseDevices();
        Assert.Multiple(() =>
        {
            Assert.That(after.Door, Is.Not.EqualTo(stateBeforeSave.Item1), "The button no longer works its blast door after loading");
            Assert.That(after.Conveyor, Is.Not.EqualTo(stateBeforeSave.Item2), "The lever no longer works its conveyor after loading");
        });

        await Cleanup(pair);
    }

    private static async Task<bool> MovesWhenWalking(Pair.TestPair pair, Robust.Shared.Player.ICommonSession player)
    {
        var server = pair.Server;
        var client = pair.Client;
        var entMan = server.ResolveDependency<IEntityManager>();
        var body = player.AttachedEntity!.Value;
        // Measure relative to what the player stands on: the whole grid may be drifting through space.
        var start = entMan.GetComponent<TransformComponent>(body).LocalPosition;

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

        var end = entMan.GetComponent<TransformComponent>(body).LocalPosition;
        TestContext.Out.WriteLine($"Moved from {start} to {end}");
        return (end - start).Length() > 0.5f;
    }

    /// <summary>
    /// An active salvage expedition used to crash every world save (its chosen song couldn't be written to the file),
    /// which lost a whole session on a live server. Expeditions are now left out of saves entirely; the save must
    /// still succeed with one running.
    /// </summary>
    [Test]
    public async Task SalvageExpeditionSurvivesSave()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<Robust.Shared.Timing.IGameTiming>();
        var ticker = entMan.System<ServerGameTicker>();
        var mapSys = entMan.System<SharedMapSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);

        const string expeditionName = "Persistent Test Expedition";
        await server.WaitPost(() =>
        {
            var map = mapSys.CreateMap(out _);
            entMan.System<MetaDataSystem>().SetEntityName(map, expeditionName);
            var expedition = entMan.AddComponent<SalvageExpeditionComponent>(map);
            expedition.EndTime = timing.CurTime + TimeSpan.FromHours(1);
            Assert.That(expedition.SelectedSong, Is.Not.Null);
        });
        await pair.RunTicksSync(5);

        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);

        Assert.That(ticker.WorldRestoredFromSave, "The world save failed with an active salvage expedition");
        await server.WaitPost(() =>
            Assert.That(entMan.Count<SalvageExpeditionComponent>(), Is.Zero, "Expeditions should not be saved"));

        await Cleanup(pair);
    }

    /// <summary>
    /// If some entity can't be saved, the save must still succeed without it (and without anything on it),
    /// instead of failing and losing everything since the last save.
    /// </summary>
    [Test]
    public async Task BrokenEntityDoesNotBreakSave()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var ticker = entMan.System<ServerGameTicker>();
        var mapSys = entMan.System<SharedMapSystem>();

        server.CfgMan.SetCVar(CCVars.UsePersistence, true);
        server.CfgMan.SetCVar(CCVars.PersistenceWorldFile, SavePath);
        server.CfgMan.SetCVar(CCVars.PersistenceAutosaveInterval, 0f);
        server.CfgMan.SetCVar(CCVars.PersistenceSaveOnRoundRestart, true);

        const string brokenMapName = "Persistent Test Broken Map";
        const string goodCorgiName = "Persistent Test Good Corgi";
        await server.WaitPost(() =>
        {
            // A map without a prototype that throws while being saved, with a grid and a mob on it,
            // like the salvage expedition map in the save that failed on the live server.
            var brokenMap = mapSys.CreateMap(out var brokenMapId);
            entMan.System<MetaDataSystem>().SetEntityName(brokenMap, brokenMapName);
            // Saving skips values left at their default, so it has to be set to actually be written (and throw).
            entMan.AddComponent<UnsavableTestComponent>(brokenMap).Song = new Robust.Shared.Audio.ResolvedCollectionSpecifier("ExpeditionEnd", 0);
            var grid = mapSys.CreateGridEntity(brokenMapId);
            mapSys.SetTile(grid, Vector2i.Zero, new Tile(1));
            entMan.SpawnEntity("MobCorgi", new EntityCoordinates(grid, 0.5f, 0.5f));

            var goodCorgi = entMan.SpawnEntity("MobCorgi", new MapCoordinates(0, 0, ticker.DefaultMap));
            entMan.System<MetaDataSystem>().SetEntityName(goodCorgi, goodCorgiName);
        });
        await pair.RunTicksSync(5);

        // Saving logs errors on purpose (the broken entity), so don't fail on them.
        var failureLevel = server.CfgMan.GetCVar(Robust.UnitTesting.RTCVars.FailureLogLevel);
        server.CfgMan.SetCVar(Robust.UnitTesting.RTCVars.FailureLogLevel, Robust.Shared.Log.LogLevel.Fatal);
        await server.WaitPost(() => ticker.RestartRound());
        await pair.RunTicksSync(10);
        server.CfgMan.SetCVar(Robust.UnitTesting.RTCVars.FailureLogLevel, failureLevel);

        Assert.That(ticker.WorldRestoredFromSave, "One broken entity stopped the whole world from saving");
        await server.WaitPost(() =>
        {
            var names = new List<string>();
            var query = entMan.AllEntityQueryEnumerator<MetaDataComponent>();
            while (query.MoveNext(out _, out var meta))
            {
                names.Add(meta.EntityName);
            }

            Assert.Multiple(() =>
            {
                // Mobs get a number added to their name, e.g. "... Corgi (586)".
                Assert.That(names.Any(n => n.StartsWith(goodCorgiName)), "The rest of the world should still be saved");
                Assert.That(names, Does.Not.Contain(brokenMapName), "The broken map should have been left out");
                Assert.That(entMan.Count<UnsavableTestComponent>(), Is.Zero);
            });
        });

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
