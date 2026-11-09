using Content.IntegrationTests.Fixtures;
using Content.Shared.Kitchen.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Kitchen;

/// <summary>
/// Deleting a whole map (like the server does on shutdown) with a loaded microwave on it shouldn't make the microwave
/// try to empty itself while everything is being deleted. Any error logged fails the test.
/// </summary>
public sealed class MicrowaveDeleteTest : GameTest
{
    [Test]
    public async Task DeletingMapWithLoadedMicrowave()
    {
        var pair = Pair;
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();

        await server.WaitPost(() =>
        {
            var microwave = entMan.SpawnAtPosition("KitchenMicrowave", map.GridCoords);
            entMan.System<SharedTransformSystem>().AnchorEntity(microwave);
            var storage = entMan.GetComponent<MicrowaveComponent>(microwave).Storage;
            var containers = entMan.System<SharedContainerSystem>();
            for (var i = 0; i < 2; i++)
            {
                var food = entMan.SpawnAtPosition("FoodMeatFish", map.GridCoords);
                Assert.That(containers.Insert(food, storage), "Test setup: food should go in the microwave");
            }
        });
        await pair.RunTicksSync(5);

        await server.WaitPost(() => entMan.DeleteEntity(map.MapUid));
        await pair.RunTicksSync(5);
    }
}
