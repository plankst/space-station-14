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

    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: TestOldBigArtifact
  parent: ComplexXenoArtifact
  components:
  - type: XenoArtifact
    nodeCount:
      min: 10
      max: 12
    segmentSize:
      min: 5
      max: 8
";

    /// <summary>
    /// "regenartifacts" brings artifacts made under the old rules (e.g. from an old world save) up to date.
    /// </summary>
    [Test]
    public async Task RegenerateShrinksOldArtifacts()
    {
        var server = Pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var artifacts = entMan.System<Content.Server.Xenoarchaeology.Artifact.XenoArtifactSystem>();
        var mapSys = entMan.System<SharedMapSystem>();
        var console = server.ResolveDependency<Robust.Server.Console.IServerConsoleHost>();

        EntityUid uid = default;
        await server.WaitPost(() =>
        {
            mapSys.CreateMap(out var mapId);
            uid = entMan.SpawnEntity("TestOldBigArtifact", new MapCoordinates(0, 0, mapId));
            var comp = entMan.GetComponent<XenoArtifactComponent>(uid);
            Assert.That(artifacts.GetAllNodes((uid, comp)).Count(), Is.GreaterThan(MaxNodes), "Test setup: should start big");

            // Like an old save loaded after the update: the artifact now has the new generation settings.
            // (Set through reflection: these fields are only writable by the artifact systems.)
            typeof(XenoArtifactComponent).GetField(nameof(XenoArtifactComponent.NodeCount))!
                .SetValue(comp, new Content.Shared.Destructible.Thresholds.MinMax(2, 4));
            typeof(XenoArtifactComponent).GetField(nameof(XenoArtifactComponent.SegmentSize))!
                .SetValue(comp, new Content.Shared.Destructible.Thresholds.MinMax(4, 4));
            console.ExecuteCommand("regenartifacts");
        });
        await Pair.RunTicksSync(5);

        await server.WaitPost(() =>
        {
            var comp = entMan.GetComponent<XenoArtifactComponent>(uid);
            var nodes = artifacts.GetAllNodes((uid, comp)).ToList();
            Assert.That(nodes, Has.Count.InRange(1, MaxNodes));
            Assert.That(comp.NodeVertices.Count(v => v != null), Is.EqualTo(nodes.Count));

            // The old nodes are really gone, not just detached.
            var nodeEntities = 0;
            var query = entMan.AllEntityQueryEnumerator<XenoArtifactNodeComponent>();
            while (query.MoveNext(out var node, out var nodeComp))
            {
                if (nodeComp.Attached == uid)
                    nodeEntities++;
            }

            Assert.That(nodeEntities, Is.EqualTo(nodes.Count));
            entMan.DeleteEntity(uid);
        });
    }

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
