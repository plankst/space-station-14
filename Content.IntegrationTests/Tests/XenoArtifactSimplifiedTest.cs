using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Shared.Xenoarchaeology.Artifact;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests;

/// <summary>
/// Artifacts are simplified for small or makeshift stations: at most 4 nodes (in any shape), and only triggers
/// that need basic tools or common items.
/// </summary>
[TestFixture]
public sealed class XenoArtifactSimplifiedTest : GameTest
{
    private const int MaxNodes = 4;
    private const int Samples = 100;

    private static readonly string[] AllowedTriggers =
    [
        "TriggerWrenching",
        "TriggerPrying",
        "TriggerScrewing",
        "TriggerPulsing",
        "TriggerMusic",
        "TriggerBlood",
        "TriggerBruteDamage",
        "TriggerAttackLaser",
        "TriggerInteractShock",
        "TriggerWater",
    ];

    // Only handheld artifacts can be thrown.
    private const string HandheldOnlyTrigger = "TriggerThrow";

    [Test]
    [TestCase("ComplexXenoArtifact", false)]
    [TestCase("ComplexXenoArtifactItem", true)]
    public async Task ArtifactsAreSmallWithSimpleTriggers(string artifactProto, bool handheld)
    {
        var server = Pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var artifacts = entMan.System<SharedXenoArtifactSystem>();
        var mapSys = entMan.System<SharedMapSystem>();

        var allowed = AllowedTriggers.ToList();
        if (handheld)
            allowed.Add(HandheldOnlyTrigger);

        // Nodes only remember their trigger's name, so compare by name.
        var allowedNames = allowed.Select(id => protoMan.Index<EntityPrototype>(id).Name).ToHashSet();
        var seenTriggers = new HashSet<string>();
        var seenSizes = new HashSet<int>();

        await server.WaitPost(() =>
        {
            mapSys.CreateMap(out var mapId);
            for (var i = 0; i < Samples; i++)
            {
                var uid = entMan.SpawnEntity(artifactProto, new MapCoordinates(i * 2, 0, mapId));
                var artifact = (uid, entMan.GetComponent<XenoArtifactComponent>(uid));
                var nodes = artifacts.GetAllNodes(artifact).ToList();

                Assert.That(nodes, Has.Count.InRange(1, MaxNodes), $"{artifactProto} had {nodes.Count} nodes");
                seenSizes.Add(nodes.Count);

                foreach (var node in nodes)
                {
                    var trigger = node.Comp.TriggerTip?.ToString() ?? string.Empty;
                    Assert.That(allowedNames, Does.Contain(trigger), $"{artifactProto} rolled a trigger that isn't allowed");
                    seenTriggers.Add(trigger);
                }

                entMan.DeleteEntity(uid);
            }

            mapSys.DeleteMap(mapId);
        });

        // Over 100 artifacts we should see a spread of sizes and triggers, not one fixed layout.
        Assert.That(seenSizes, Has.Count.GreaterThan(1), "Artifacts should vary in size");
        Assert.That(seenTriggers, Has.Count.GreaterThan(allowed.Count / 2), "Artifacts should use a variety of triggers");
    }
}
