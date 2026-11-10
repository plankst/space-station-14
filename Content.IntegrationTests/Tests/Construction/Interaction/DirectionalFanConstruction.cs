using Content.Client.Construction;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.Construction.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests.Construction.Interaction;

/// <summary>
/// Directional fans can be built from 2 steel through the construction menu and screwdrivered apart for the 2 steel.
/// </summary>
public sealed class DirectionalFanConstruction : InteractionTest
{
    private const string Fan = "AtmosDeviceFanDirectional";

    [Test]
    public async Task BuildAndDeconstructFan()
    {
        await StartConstruction(Fan);
        await InteractUsing(Steel, 2);
        ClientAssertPrototype(Fan, Target);

        await InteractUsing(Screw);
        AssertDeleted();
        await AssertEntityLookup((Steel, 2));
    }

    /// <summary>
    /// The fan keeps the direction it was placed facing, so it blocks air on the side the builder chose.
    /// </summary>
    [Test]
    public async Task FanKeepsPlacedDirection()
    {
        await Client.WaitPost(() =>
        {
            var proto = ProtoMan.Index<ConstructionPrototype>(Fan);
            Assert.That(CConSys.TrySpawnGhost(proto, CEntMan.GetCoordinates(TargetCoords), Direction.East, out var ghost));
            Target = CEntMan.GetNetEntity(ghost!.Value);
            ConstructionGhostId = ghost.Value.GetHashCode();
        });
        await RunTicks(1);

        await InteractUsing(Steel, 2);
        ClientAssertPrototype(Fan, Target);

        var rotation = SEntMan.GetComponent<TransformComponent>(SEntMan.GetEntity(Target!.Value)).LocalRotation;
        Assert.That(rotation.GetCardinalDir(), Is.EqualTo(Direction.East));
    }
}
