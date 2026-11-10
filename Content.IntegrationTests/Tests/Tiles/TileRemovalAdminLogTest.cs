using Content.IntegrationTests.Tests.Interaction;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests.Tiles;

/// <summary>
/// Removing the last tile of a small grid deletes the grid. The admin log for it used to describe the position on the
/// deleted grid, which threw an error (admin logs are off in most tests, so this test turns them on).
/// </summary>
public sealed class TileRemovalAdminLogTest : InteractionTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        Dirty = true,
        AdminLogsEnabled = true,
    };

    [Test]
    public async Task CuttingLastLatticeLogsWithoutError()
    {
        // Leave a single lattice on its own grid.
        await SetTile(null);
        await SetTile(null, PlayerCoords);
        AssertGridCount(0);

        var oldPos = TargetCoords;
        TargetCoords = SEntMan.GetNetCoordinates(new EntityCoordinates(MapData.MapUid, 1, 0));
        await InteractUsing(Rod);
        TargetCoords = oldPos;
        await AssertTile(Lattice);
        AssertGridCount(1);

        // Cutting it deletes the grid; the admin log must not throw.
        await InteractUsing(Cut);
        await AssertTile(null);
        AssertGridCount(0);
    }
}
