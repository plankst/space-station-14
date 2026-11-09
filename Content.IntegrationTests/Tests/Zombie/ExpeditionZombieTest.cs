using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared.CCVar;
using Content.Server.Ghost.Roles.Components;
using Content.Server.NPC.HTN;
using Content.Server.Zombies;
using Content.Shared.Body;
using Content.Shared.Inventory;
using Content.Shared.NPC;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Zombies;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Zombie;

/// <summary>
/// The infected crew that roam salvage expeditions.
/// </summary>
[TestFixture]
[TestOf(typeof(ZombifyOnSpawnSystem))]
public sealed class ExpeditionZombieTest : GameTest
{
    private static readonly ProtoId<NpcFactionPrototype> ZombieFaction = "Zombie";

    private static readonly string[] ZombieProtos = { "MobZombieExpedition", "MobZombieExpeditionSuited" };

    /// <summary>
    /// Expedition zombies spawn as random, dressed humans and turn into hostile NPC zombies (not ghost roles) with a
    /// 50% bite infection chance. Also on a paused map, which is how expedition maps are made.
    /// </summary>
    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ZombiesSpawnRandomAndHostile(bool pausedMap)
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();

        var spawned = new List<EntityUid>();
        await server.WaitPost(() =>
        {
            if (pausedMap)
                entMan.System<SharedMapSystem>().SetPaused(map.MapUid, true);

            for (var i = 0; i < 12; i++)
            {
                spawned.Add(entMan.SpawnAtPosition(ZombieProtos[i % 4 == 3 ? 1 : 0], map.GridCoords));
            }
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var inventory = entMan.System<InventorySystem>();
            var faction = entMan.System<NpcFactionSystem>();
            var visualBody = entMan.System<SharedVisualBodySystem>();
            var jumpsuits = new HashSet<string>();
            var names = new HashSet<string>();

            foreach (var uid in spawned)
            {
                var name = entMan.ToPrettyString(uid);
                Assert.That(entMan.TryGetComponent<ZombieComponent>(uid, out var zombie), $"{name} wasn't zombified");
                Assert.Multiple(() =>
                {
                    Assert.That(zombie!.BaseZombieInfectionChance, Is.EqualTo(0.5f), $"{name} has the wrong bite infection chance");
                    Assert.That(entMan.HasComponent<ZombifyOnSpawnComponent>(uid), Is.False);
                    Assert.That(entMan.HasComponent<GhostRoleComponent>(uid), Is.False, $"{name} is a ghost role");
                    Assert.That(entMan.HasComponent<GhostTakeoverAvailableComponent>(uid), Is.False, $"{name} is a ghost role");
                    Assert.That(entMan.HasComponent<HTNComponent>(uid), $"{name} has no AI");
                    Assert.That(entMan.HasComponent<ActiveNPCComponent>(uid), $"{name}'s AI is asleep");
                    Assert.That(faction.IsMember(uid, ZombieFaction), $"{name} isn't in the zombie faction");
                    Assert.That(inventory.TryGetSlotEntity(uid, "jumpsuit", out var jumpsuit), $"{name} has no clothes");
                    Assert.That(inventory.TryGetSlotEntity(uid, "shoes", out _), $"{name} has no shoes");
                    Assert.That(inventory.TryGetSlotEntity(uid, "gloves", out _), Is.False, $"{name} has gloves");
                    if (jumpsuit != null)
                        jumpsuits.Add(entMan.GetComponent<MetaDataComponent>(jumpsuit.Value).EntityPrototype!.ID);
                });

                names.Add(entMan.GetComponent<MetaDataComponent>(uid).EntityName);

                Assert.That(visualBody.TryGatherMarkingsData(uid, null, out var profiles, out _, out _), $"{name} has no appearance");
                foreach (var profile in profiles!.Values)
                {
                    Assert.That(profile.SkinColor, Is.EqualTo(zombie!.SkinColor), $"{name} doesn't look like a zombie");
                }
            }

            // Random appearance and clothing, so they look like a whole crew that turned.
            Assert.That(names, Has.Count.GreaterThan(6), "Zombies all have the same names");
            Assert.That(jumpsuits, Has.Count.GreaterThan(2), "Zombies all wear the same clothes");
        });

        await server.WaitPost(() =>
        {
            foreach (var uid in spawned)
                entMan.DeleteEntity(uid);
        });
    }

    /// <summary>
    /// Expedition zombies never had a player, so they mustn't fall into "SSD sleep" (nobody controlling them) after a
    /// while. That used to stun and knock every zombie down for good about 10 minutes into an expedition. The SSD timer
    /// is shortened to 2 seconds here.
    /// </summary>
    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.ICSSDSleepTime), 2f)]
    public async Task ZombiesDontFallAsleep()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();

        var spawned = new List<EntityUid>();
        await server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                spawned.Add(entMan.SpawnAtPosition(ZombieProtos[0], map.GridCoords));
            }
        });
        await pair.RunTicksSync((int) server.Timing.TickRate * 8);

        await server.WaitAssertion(() =>
        {
            var standing = entMan.System<Content.Shared.Standing.StandingStateSystem>();
            foreach (var uid in spawned)
            {
                var name = entMan.ToPrettyString(uid);
                Assert.That(entMan.HasComponent<ZombieComponent>(uid), $"{name} wasn't zombified");
                Assert.That(entMan.HasComponent<Content.Shared.Stunnable.StunnedComponent>(uid), Is.False, $"{name} is stunned");
                Assert.That(standing.IsDown(uid), Is.False, $"{name} fell over");
            }
        });

        await server.WaitPost(() =>
        {
            foreach (var uid in spawned)
                entMan.DeleteEntity(uid);
        });
    }

    /// <summary>
    /// Expedition zombies get through a closed airlock (prying it) or a window (smashing it) to reach someone on the
    /// other side of a sealed corridor.
    /// </summary>
    [Test]
    [TestCase("Airlock")]
    [TestCase("Window")]
    public async Task ZombiesBreakThroughToTarget(string blocker)
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var mapSys = entMan.System<SharedMapSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid zombie = default;
        EntityUid victim = default;
        EntityUid obstacle = default;
        await server.WaitPost(() =>
        {
            var grid = map.Grid;
            for (var x = -1; x <= 10; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    mapSys.SetTile(grid.Owner, grid.Comp, new Vector2i(x, y), new Tile(1));
                }
            }

            EntityUid Place(string proto, int x, int y)
            {
                var uid = entMan.SpawnEntity(proto, new EntityCoordinates(grid.Owner, x + 0.5f, y + 0.5f));
                xformSys.AnchorEntity(uid);
                return uid;
            }

            // A sealed corridor along y = 0, split in two by the blocker.
            for (var x = -1; x <= 10; x++)
            {
                Place("WallSolid", x, -1);
                Place("WallSolid", x, 1);
            }

            Place("WallSolid", -1, 0);
            Place("WallSolid", 10, 0);
            obstacle = Place(blocker, 5, 0);

            zombie = entMan.SpawnEntity(ZombieProtos[0], new EntityCoordinates(grid.Owner, 1.5f, 0.5f));
            victim = entMan.SpawnEntity("MobHuman", new EntityCoordinates(grid.Owner, 8.5f, 0.5f));
            entMan.EnsureComponent<ZombieImmuneComponent>(victim);
        });

        var tickRate = (int) server.Timing.TickRate;
        var through = false;
        for (var i = 0; i < 60 && !through; i++)
        {
            await pair.RunTicksSync(tickRate);
            await server.WaitPost(() =>
            {
                // (Not "the victim got hurt": the test map has no air, so they take damage anyway.)
                var zombieX = entMan.GetComponent<TransformComponent>(zombie).LocalPosition.X;
                var opened = entMan.Deleted(obstacle) ||
                             entMan.TryGetComponent<Content.Shared.Doors.Components.DoorComponent>(obstacle, out var d) &&
                             d.State != Content.Shared.Doors.Components.DoorState.Closed;
                through = opened || zombieX > 5.5f;
            });
        }

        await server.WaitAssertion(() =>
        {
            var state = blocker == "Airlock" && entMan.TryGetComponent<Content.Shared.Doors.Components.DoorComponent>(obstacle, out var door)
                ? $"door {door.State}"
                : $"blocker {(entMan.Deleted(obstacle) ? "destroyed" : "intact")}";
            Assert.That(through, $"The zombie never got through the {blocker} to its target ({state}, zombie at {entMan.GetComponent<TransformComponent>(zombie).LocalPosition})");
        });
    }

    /// <summary>
    /// Expedition zombies go straight for someone they can see, even with weapons lying around. They used to try to
    /// pick up a weapon first like armed NPCs do, but zombies drop everything they grab, so they never got around to
    /// attacking and just wandered between items.
    /// </summary>
    [Test]
    public async Task ZombiesAttackInsteadOfGrabbingWeapons()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var mapSys = entMan.System<SharedMapSystem>();

        EntityUid victim = default;
        await server.WaitPost(() =>
        {
            for (var x = -4; x <= 10; x++)
            {
                for (var y = -4; y <= 4; y++)
                {
                    mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), new Tile(1));
                }
            }

            entMan.SpawnEntity(ZombieProtos[0], new EntityCoordinates(map.Grid.Owner, 0.5f, 0.5f));
            // Loot lying around the zombie, like in an expedition structure.
            foreach (var (proto, x, y) in new[] { ("Crowbar", -1.5f, 0.5f), ("Wrench", 0.5f, -1.5f), ("ToolboxMechanicalFilled", 0.5f, 2.5f) })
            {
                entMan.SpawnEntity(proto, new EntityCoordinates(map.Grid.Owner, x, y));
            }

            victim = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 6.5f, 0.5f));
            entMan.EnsureComponent<ZombieImmuneComponent>(victim);
            // The test map has no air; keep that from hurting the victim so only bites count.
            entMan.RemoveComponent<Content.Server.Body.Components.RespiratorComponent>(victim);
            entMan.RemoveComponent<Content.Server.Atmos.Components.BarotraumaComponent>(victim);
        });

        var bitten = false;
        for (var i = 0; i < 15 && !bitten; i++)
        {
            await pair.RunTicksSync((int) server.Timing.TickRate);
            await server.WaitPost(() =>
            {
                var damage = entMan.System<Content.Shared.Damage.Systems.DamageableSystem>().GetAllDamage(victim).DamageDict;
                bitten = damage.GetValueOrDefault("Slash") > 0 || damage.GetValueOrDefault("Piercing") > 0;
            });
        }

        Assert.That(bitten, "The zombie never attacked the person standing in front of it");
    }

    /// <summary>
    /// Zombies kill all sorts of things on expeditions. Turning an animal that can't be cuffed into a zombie used to log
    /// an error, and turning someone who's handcuffed would throw while taking the cuffs off.
    /// </summary>
    [Test]
    public async Task ZombifyingAnimalsAndCuffedPeopleWorks()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();

        EntityUid prisoner = default;
        var cuffable = entMan.System<Content.Shared.Cuffs.SharedCuffableSystem>();
        await server.WaitAssertion(() =>
        {
            var zombies = entMan.System<ZombieSystem>();

            var frog = entMan.SpawnAtPosition("MobFrog", map.GridCoords);
            zombies.ZombifyEntity(frog);
            Assert.That(entMan.HasComponent<ZombieComponent>(frog), "The frog didn't turn");

            prisoner = entMan.SpawnAtPosition("MobHuman", map.GridCoords);
            var cuffs = entMan.SpawnAtPosition("Handcuffs", map.GridCoords);
            Assert.That(cuffable.TryAddNewCuffs(prisoner, prisoner, cuffs), "Test setup: couldn't cuff");
            // Real cuffing (after the do-after) also marks the cuffs as in use.
            typeof(Content.Shared.Cuffs.Components.HandcuffComponent).GetField("Used")!
                .SetValue(entMan.GetComponent<Content.Shared.Cuffs.Components.HandcuffComponent>(cuffs), true);
            Assert.That(cuffable.GetAllCuffs(prisoner), Is.Not.Empty, "Test setup: not cuffed");
            zombies.ZombifyEntity(prisoner);
            Assert.That(entMan.HasComponent<ZombieComponent>(prisoner), "The cuffed person didn't turn");
        });
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(cuffable.GetAllCuffs(prisoner), Is.Empty, "The zombie is still cuffed");
        });
    }

    /// <summary>
    /// The Zombies expedition faction only spawns expedition zombies, with no boss, at about 11-12 zombies per
    /// expedition.
    /// </summary>
    [Test]
    public async Task ZombieExpeditionFaction()
    {
        var protoMan = Pair.Server.ProtoMan;
        await Pair.Server.WaitAssertion(() =>
        {
            Assert.That(protoMan.TryIndex<SalvageFactionPrototype>("Zombies", out var faction));
            Assert.That(faction!.MobGroups.Select(e => e.Proto), Is.SubsetOf(ZombieProtos));
            Assert.That(faction.MobGroups.All(e => e.Cost >= 2), "Zombies are heavy mobs and should cost at least 2");
            Assert.That(faction.Configs, Does.Not.ContainKey("Megafauna"), "Zombies shouldn't have a boss");
        });
    }
}
