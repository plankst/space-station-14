using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.IntegrationTests.Fixtures;
using Content.Shared.Kitchen;
using Content.Shared.Kitchen.EntitySystems;
using Content.Shared.Medical.BiomassReclaimer;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Medical;

/// <summary>
/// Organs are useful leftovers: every organic organ grinds into 5 protein and 1 fat, and the biomass reclaimer turns one
/// into 10 biomass.
/// </summary>
[TestFixture]
public sealed class OrganUsesTest : GameTest
{
    [Test]
    public async Task OrgansGrindIntoProteinAndFat()
    {
        var server = Pair.Server;
        var entMan = server.EntMan;
        var map = await Pair.CreateTestMap();
        var factory = entMan.ComponentFactory;

        var organs = server.ProtoMan.EnumeratePrototypes<EntityPrototype>()
            .Where(p => !p.Abstract && p.TryGetComponent<FixedBiomassYieldComponent>(out _, factory))
            .Select(p => p.ID)
            .ToList();

        // Hearts, lungs, brains etc. of every species.
        Assert.That(organs, Has.Count.GreaterThan(30));
        Assert.That(organs, Does.Contain("OrganHumanHeart"));

        await server.WaitAssertion(() =>
        {
            var grinder = entMan.System<SharedReagentGrinderSystem>();
            var wrong = new List<string>();
            foreach (var id in organs)
            {
                var organ = entMan.SpawnAtPosition(id, map.GridCoords);
                var solution = grinder.GetGrinderSolution(organ, GrinderProgram.Grind);
                var contents = solution == null
                    ? "nothing"
                    : string.Join(" ", solution.Contents.OrderBy(r => r.Reagent.Prototype).Select(r => $"{r.Reagent.Prototype}={r.Quantity}"));

                if (!grinder.CanGrind(organ) || contents != "Fat=1 Protein=5")
                    wrong.Add($"{id}: {contents}");

                entMan.DeleteEntity(organ);
            }

            Assert.That(wrong, Is.Empty, "Organs that don't grind into 5 protein and 1 fat:\n" + string.Join("\n", wrong));
        });
    }

    [Test]
    public async Task ReclaimerTurnsOrganIntoBiomass()
    {
        var server = Pair.Server;
        var entMan = server.EntMan;
        var map = await Pair.CreateTestMap();

        EntityUid reclaimer = default;
        await server.WaitPost(() =>
        {
            reclaimer = entMan.SpawnAtPosition("BiomassReclaimer", map.GridCoords);
            entMan.System<SharedTransformSystem>().AnchorEntity(reclaimer);
            entMan.GetComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(reclaimer).NeedsPower = false;
        });
        await Pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var system = entMan.System<BiomassReclaimerSystem>();
            var heart = entMan.SpawnAtPosition("OrganHumanHeart", map.GridCoords);
            var reclaimerEnt = new Entity<BiomassReclaimerComponent>(reclaimer, entMan.GetComponent<BiomassReclaimerComponent>(reclaimer));

            var validate = typeof(BiomassReclaimerSystem).GetMethod("ValidateInsertion", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.That(validate.Invoke(system, new object[] { reclaimerEnt, heart })!.ToString(), Is.EqualTo("Success"),
                "The reclaimer won't take an organ");

            // What finishing the insertion do-after does.
            var start = typeof(BiomassReclaimerSystem).GetMethod("StartProcessing", BindingFlags.NonPublic | BindingFlags.Instance)!;
            start.Invoke(system, new object[] { new Entity<PhysicsComponent>(heart, entMan.GetComponent<PhysicsComponent>(heart)), reclaimerEnt });
        });

        // Let it finish processing.
        await Pair.RunTicksSync((int) server.Timing.TickRate * 30);

        await server.WaitAssertion(() =>
        {
            var biomass = 0;
            var q = entMan.AllEntityQueryEnumerator<StackComponent, MetaDataComponent>();
            while (q.MoveNext(out var stack, out var meta))
            {
                if (meta.EntityPrototype?.ID == "MaterialBiomass" || meta.EntityPrototype?.ID == "MaterialBiomass1")
                    biomass += stack.Count;
            }

            Assert.That(biomass, Is.EqualTo(10), "The reclaimer didn't make 10 biomass from the organ");
        });
    }
}
