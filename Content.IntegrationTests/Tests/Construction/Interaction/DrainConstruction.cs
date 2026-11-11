using Content.IntegrationTests.Tests.Interaction;

namespace Content.IntegrationTests.Tests.Construction.Interaction;

/// <summary>
/// Floor drains can be built from 1 steel through the construction menu and screwdrivered apart for the steel.
/// </summary>
public sealed class DrainConstruction : InteractionTest
{
    private const string Drain = "FloorDrain";

    [Test]
    public async Task BuildAndDeconstructDrain()
    {
        await StartConstruction(Drain);
        await InteractUsing(Steel, 1);
        ClientAssertPrototype(Drain, Target);

        await InteractUsing(Screw);
        AssertDeleted();
        await AssertEntityLookup((Steel, 1));
    }

    /// <summary>
    /// Drains placed on the map (or loaded from a world save) can be taken apart the same way.
    /// </summary>
    [Test]
    public async Task DeconstructMappedDrain()
    {
        await StartDeconstruction(Drain);
        await InteractUsing(Screw);
        AssertDeleted();
        await AssertEntityLookup((Steel, 1));
    }
}
