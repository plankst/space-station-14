using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.SmartFridge;

namespace Content.IntegrationTests.Tests.SmartFridge;

public sealed class SmartFridgeInteractionTest : InteractionTest
{
    private const string SmartFridgeProtoId = "SmartFridge";
    private const string SampleItemProtoId = "FoodAmbrosiaVulgaris";
    private const string SampleDumpableAndInsertableId = "PillCanisterSomething";
    private const int SampleDumpableCount = 5;
    private const string SampleDumpableId = "ChemBagSomething";

    [TestPrototypes]
    private const string TestPrototypes = $@"
- type: entity
  parent: PillCanister
  id: {SampleDumpableAndInsertableId}
  components:
  - type: EntityTableContainerFill
    containers:
      storagebase:
        id: PillCopper
        amount: 5

- type: entity
  parent: ChemBag
  id: {SampleDumpableId}
  components:
  - type: EntityTableContainerFill
    containers:
      storagebase:
        id: PillCopper
        amount: 5
";

    [Test]
    public async Task InsertAndDispenseItemTest()
    {
        await PlaceInHands(SampleItemProtoId);

        await SpawnTarget(SmartFridgeProtoId);
        var fridge = SEntMan.GetEntity(Target.Value);
        var component = SEntMan.GetComponent<SmartFridgeComponent>(fridge);

        await SpawnEntity("APCBasic", SEntMan.GetCoordinates(TargetCoords));
        await RunTicks(1);

        // smartfridge spawns with nothing
        Assert.That(component.Entries, Is.Empty);
        await InteractUsing(SampleItemProtoId);

        // smartfridge now has items
        Assert.That(component.Entries, Is.Not.Empty);
        Assert.That(component.ContainedEntries[component.Entries[0]], Is.Not.Empty);

        // open the UI
        await Activate();
        Assert.That(IsUiOpen(SmartFridgeUiKey.Key));

        // dispense an item
        await SendBui(SmartFridgeUiKey.Key, new SmartFridgeDispenseItemMessage(component.Entries[0]));

        // assert that the listing is still there
        Assert.That(component.Entries, Is.Not.Empty);
        // but empty
        Assert.That(component.ContainedEntries[component.Entries[0]], Is.Empty);

        // and that the thing we dispensed is actually around
        await AssertEntityLookup(
            ("APCBasic", 1),
            (SampleItemProtoId, 1)
        );
    }

    [Test]
    public async Task InsertDumpableInsertableItemTest()
    {
        await PlaceInHands(SampleItemProtoId);

        await SpawnTarget(SmartFridgeProtoId);
        var fridge = SEntMan.GetEntity(Target.Value);
        var component = SEntMan.GetComponent<SmartFridgeComponent>(fridge);

        await SpawnEntity("APCBasic", SEntMan.GetCoordinates(TargetCoords));
        await RunTicks(1);

        await InteractUsing(SampleDumpableAndInsertableId);

        // smartfridge now has one item only
        Assert.That(component.Entries, Is.Not.Empty);
        Assert.That(component.ContainedEntries[component.Entries[0]].Count, Is.EqualTo(1));
    }

    [Test]
    public async Task InsertDumpableItemTest()
    {
        await PlaceInHands(SampleItemProtoId);

        await SpawnTarget(SmartFridgeProtoId);
        var fridge = SEntMan.GetEntity(Target.Value);
        var component = SEntMan.GetComponent<SmartFridgeComponent>(fridge);

        await SpawnEntity("APCBasic", SEntMan.GetCoordinates(TargetCoords));
        await RunTicks(1);

        await InteractUsing(SampleDumpableId);

        // smartfridge now has N items
        Assert.That(component.Entries, Is.Not.Empty);
        Assert.That(component.ContainedEntries[component.Entries[0]].Count, Is.EqualTo(SampleDumpableCount));
    }

    /// <summary>
    /// Dispensing one item shows one fewer on the player's screen right away, and never more. The shared sets in the
    /// networked state used to get changed by every re-prediction, so the count ran all the way down to 0 before the
    /// server's real count came in.
    /// </summary>
    [Test]
    public async Task DispenseCountsDownByOneOnClient()
    {
        await PlaceInHands(SampleItemProtoId);

        await SpawnTarget(SmartFridgeProtoId);
        var fridge = SEntMan.GetEntity(Target.Value);
        var component = SEntMan.GetComponent<SmartFridgeComponent>(fridge);

        await SpawnEntity("APCBasic", SEntMan.GetCoordinates(TargetCoords));
        await RunTicks(1);

        await InteractUsing(SampleDumpableId);
        var entry = component.Entries[0];
        Assert.That(component.ContainedEntries[entry].Count, Is.EqualTo(SampleDumpableCount));

        await Activate();
        Assert.That(IsUiOpen(SmartFridgeUiKey.Key));
        Assert.That(TryGetBui(SmartFridgeUiKey.Key, out var bui));

        // Dispense one, the way the fridge window does it (predicted), and watch the count on the client every tick.
        await Client.WaitPost(() => bui!.SendPredictedMessage(new SmartFridgeDispenseItemMessage(entry)));
        var lowest = int.MaxValue;
        var cFridge = CEntMan.GetEntity(Target.Value);
        for (var i = 0; i < 30; i++)
        {
            await RunTicks(1);
            await Client.WaitPost(() =>
            {
                var cComp = CEntMan.GetComponent<SmartFridgeComponent>(cFridge);
                var count = cComp.ContainedEntries.TryGetValue(entry, out var items) ? items.Count : 0;
                lowest = Math.Min(lowest, count);
            });
        }

        Assert.That(lowest, Is.EqualTo(SampleDumpableCount - 1), "The fridge showed the wrong count while dispensing one item");
        Assert.That(component.ContainedEntries[entry].Count, Is.EqualTo(SampleDumpableCount - 1), "The server didn't dispense exactly one");
    }
}
