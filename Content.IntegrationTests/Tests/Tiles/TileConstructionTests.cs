using System.Linq;
using Robust.Shared.GameObjects;
using Content.IntegrationTests.Tests.Interaction;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.IntegrationTests.Tests.Tiles;

public sealed class TileConstructionTests : InteractionTest
{
    /// <summary>
    /// Test placing and cutting a single lattice.
    /// </summary>
    [Test]
    public async Task PlaceThenCutLattice()
    {
        await AssertTile(Plating);
        await AssertTile(Plating, PlayerCoords);
        AssertGridCount(1);
        await SetTile(null);
        await InteractUsing(Rod);
        await AssertTile(Lattice);
        Assert.That(HandSys.GetActiveItem((SEntMan.GetEntity(Player), Hands)), Is.Null);
        await InteractUsing(Cut);
        await AssertTile(null);
        await AssertEntityLookup((Rod, 1));
        AssertGridCount(1);
    }

    /// <summary>
    /// Test placing and cutting a single lattice in space (not adjacent to any existing grid.
    /// </summary>
    [Test]
    public async Task CutThenPlaceLatticeNewGrid()
    {
        await AssertTile(Plating);
        await AssertTile(Plating, PlayerCoords);
        AssertGridCount(1);

        // Remove grid
        await SetTile(null);
        await SetTile(null, PlayerCoords);
        Assert.That(MapData.Grid.Comp.Deleted);
        AssertGridCount(0);

        // Place Lattice
        var oldPos = TargetCoords;
        TargetCoords = SEntMan.GetNetCoordinates(new EntityCoordinates(MapData.MapUid, 1, 0));
        await InteractUsing(Rod);
        TargetCoords = oldPos;
        await AssertTile(Lattice);
        AssertGridCount(1);

        // Cut lattice
        Assert.That(HandSys.GetActiveItem((SEntMan.GetEntity(Player), Hands)), Is.Null);
        await InteractUsing(Cut);
        await AssertTile(null);
        AssertGridCount(0);

        await AssertEntityLookup((Rod, 1));
    }

    /// <summary>
    /// Test space -> floor -> plating
    /// </summary>
    [Test]
    public async Task FloorConstructDeconstruct()
    {
        await AssertTile(Plating);
        await AssertTile(Plating, PlayerCoords);
        AssertGridCount(1);

        // Remove grid
        await SetTile(null);
        await SetTile(null, PlayerCoords);
        Assert.That(MapData.Grid.Comp.Deleted);
        AssertGridCount(0);

        // Space -> Lattice
        var oldPos = TargetCoords;
        TargetCoords = SEntMan.GetNetCoordinates(new EntityCoordinates(MapData.MapUid, 1, 0));
        await InteractUsing(Rod);
        TargetCoords = oldPos;
        await AssertTile(Lattice);
        AssertGridCount(1);

        // Lattice -> Plating
        await InteractUsing(FloorItem);
        Assert.That(HandSys.GetActiveItem((SEntMan.GetEntity(Player), Hands)), Is.Null);
        await AssertTile(Plating);
        AssertGridCount(1);

        // Plating -> Tile
        await InteractUsing(FloorItem);
        Assert.That(HandSys.GetActiveItem((SEntMan.GetEntity(Player), Hands)), Is.Null);
        await AssertTile(Floor);
        AssertGridCount(1);

        // Tile -> Plating
        await InteractUsing(Pry);
        await AssertTile(Plating);
        AssertGridCount(1);

        await AssertEntityLookup((FloorItem, 1));
    }

    /// <summary>
    /// Test snowPlating -> floor -> snowPlating using tilestacking
    /// </summary>
    [Test]
    public async Task BrassPlatingPlace()
    {
        await SetTile(PlatingSnow);

        // Snow Plating -> Tile
        await InteractUsing(FloorItem);
        Assert.That(HandSys.GetActiveItem((SEntMan.GetEntity(Player), Hands)), Is.Null);
        await AssertTile(Floor);
        AssertGridCount(1);

        // Tile -> Snow Plating
        await InteractUsing(Pry);
        await AssertTile(PlatingSnow);
        AssertGridCount(1);
        await AssertEntityLookup((FloorItem, 1));
    }

    /// <summary>
    /// Plating can be crowbarred back up to lattice, refunding a steel floor tile.
    /// </summary>
    [Test]
    [TestCase("Plating")]
    [TestCase("PlatingDamaged")]
    [TestCase("PlatingRCD")]
    [TestCase("PlatingAsteroid")]
    [TestCase("PlatingSnow")]
    public async Task PryPlatingToLattice(string plating)
    {
        await SetTile(plating);
        await InteractUsing(Pry);
        await AssertTile(Lattice);
        AssertGridCount(1);
        await AssertEntityLookup((FloorItem, 1));
    }

    /// <summary>
    /// Prying up plating takes 3 times as long as prying up a floor tile.
    /// </summary>
    [Test]
    public async Task PlatingPryTakesThreeTimesAsLong()
    {
        async Task<TimeSpan> PryDelay(string tile)
        {
            await SetTile(tile);
            await InteractUsing(Pry, awaitDoAfters: false);
            await RunTicks(1);
            Assert.That(ActiveDoAfters.Count(), Is.EqualTo(1), $"No pry started on {tile}");
            var delay = ActiveDoAfters.Single().Args.Delay;
            await CancelDoAfters();
            return delay;
        }

        var floor = await PryDelay(Floor);
        var plating = await PryDelay(Plating);
        Assert.That(plating.TotalSeconds, Is.EqualTo(floor.TotalSeconds * 3).Within(0.001));
    }

    /// <summary>
    /// Plating with something solid on it (a wall) can't be pried up.
    /// </summary>
    [Test]
    public async Task CantPryPlatingUnderSomethingSolid()
    {
        await SetTile(Plating);
        await Server.WaitPost(() => SEntMan.SpawnEntity("WallSolid", SEntMan.GetCoordinates(TargetCoords)));
        await InteractUsing(Pry);
        await AssertTile(Plating);
    }

    /// <summary>
    /// Cables (and other under-floor things) stay put on the lattice when the plating is pried up.
    /// </summary>
    [Test]
    public async Task CablesStayWhenPlatingIsPried()
    {
        await SetTile(Plating);
        EntityUid cable = default;
        await Server.WaitPost(() => cable = SEntMan.SpawnEntity("CableApcExtension", SEntMan.GetCoordinates(TargetCoords)));
        await RunTicks(1);
        Assert.That(SEntMan.GetComponent<TransformComponent>(cable).Anchored, "Test setup: cable should be anchored");

        await InteractUsing(Pry);
        await AssertTile(Lattice);
        Assert.That(SEntMan.EntityExists(cable), "The cable was destroyed");
        Assert.That(SEntMan.GetComponent<TransformComponent>(cable).Anchored, "The cable came loose");
    }

    /// <summary>
    /// The indestructible reinforced hull stays unpryable.
    /// </summary>
    [Test]
    public async Task ReinforcedHullCantBePried()
    {
        await SetTile("FloorHullReinforced");
        await InteractUsing(Pry);
        await AssertTile("FloorHullReinforced");
    }

    /// <summary>
    /// Automatic prying (disposal pipe exits, space wind, revenants, artifacts) pulls up floor tiles but must stop at
    /// plating, even though players can crowbar plating.
    /// </summary>
    [Test]
    public async Task AutomaticPryingStopsAtPlating()
    {
        var tileSys = SEntMan.System<Content.Shared.Maps.TileSystem>();
        var mapSys = SEntMan.System<SharedMapSystem>();

        await SetTile(Floor);
        await Server.WaitPost(() =>
        {
            var grid = SEntMan.GetComponent<MapGridComponent>(MapData.Grid);
            var tile = mapSys.GetTileRef(MapData.Grid, grid, SEntMan.GetCoordinates(TargetCoords));
            Assert.That(tileSys.PryTile(tile), Is.True);
        });
        await RunTicks(1);
        await AssertTile(Plating);

        await Server.WaitPost(() =>
        {
            var grid = SEntMan.GetComponent<MapGridComponent>(MapData.Grid);
            var tile = mapSys.GetTileRef(MapData.Grid, grid, SEntMan.GetCoordinates(TargetCoords));
            Assert.That(tileSys.PryTile(tile), Is.False);
        });
        await RunTicks(1);
        await AssertTile(Plating);
    }
}
