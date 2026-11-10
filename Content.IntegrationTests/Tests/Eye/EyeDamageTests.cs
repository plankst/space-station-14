using System.Collections.Generic;
using System.Linq;
using Robust.Shared.GameObjects;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.Eye.Blinding.Systems;
using Content.Shared.Inventory;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Eye;

/// <summary>
/// Tests for eye damage
/// </summary>
public sealed class EyeDamageTests : InteractionTest
{
    // We need eye slots (and eyeballs) to blind/damage
    protected override string PlayerPrototype => "MobHuman";
    private static readonly EntProtoId LockerPrototype = "LockerFreezer";
    private static readonly EntProtoId WeldingMaskPrototype = "ClothingHeadHatWelding";
    private const int ExpectedWelderEyeDamage = 1; // 1 is a magic number in EyeProtectionSystem.cs. Test will fail if this is changed at some point.

    [SidedDependency(Side.Server)] private readonly BlindableSystem _blindable = default!;

    /// <summary>
    /// Tests that applying maximum eye damage triggers blindness.
    /// </summary>
    [Test]
    public async Task EyeDamageBlindsTest()
    {
        var blindableComponent = Comp<BlindableComponent>(Player);

        Assert.That(blindableComponent.IsBlind, Is.False, "Initial blind check failed");

        // Max eye damage inflicts blindness
        await Server.WaitPost(() =>
        {
            _blindable.AdjustEyeDamage(SPlayer, blindableComponent.MaxDamage);
        });

        Assert.That(blindableComponent.IsBlind, Is.True, "Max eye damage did not inflict blindness");
    }

    /// <summary>
    /// Tests that welding without protection causes eye damage, and welding with protection does not.
    /// </summary>
    [Test]
    public async Task WelderCausesEyeDamageTest()
    {
        var blindableComponent = Comp<BlindableComponent>(Player);
        await SpawnTarget(LockerPrototype);

        // Welding without protection causes eye damage
        Assert.That(blindableComponent.EyeDamage, Is.Zero, "Initial eye damage >0");

        await InteractUsing(Weld);

        Assert.That(blindableComponent.EyeDamage, Is.EqualTo(ExpectedWelderEyeDamage), "Unexpected eye damage amount");

        // Welding with protection prevents eye damage
        var initialEyeDamage = blindableComponent.EyeDamage;

        await PlaceInHands(WeldingMaskPrototype);
        await UseInHand();
        await AwaitDoAfters();
        await InteractUsing(Weld);

        Assert.That(blindableComponent.EyeDamage, Is.EqualTo(initialEyeDamage), "Eye damage inflicted despite protection");
    }

    /// <summary>
    /// Every hardsuit and EVA helmet shields from welding like a welding mask.
    /// </summary>
    [Test]
    public async Task HardsuitAndEvaHelmetsProtectEyes()
    {
        var bases = new[] { "ClothingHeadHardsuitBase", "ClothingHeadEVAHelmetBase" };
        var helmets = new List<string>();
        await Server.WaitPost(() =>
        {
            var factory = SEntMan.ComponentFactory;
            foreach (var proto in ProtoMan.EnumeratePrototypes<EntityPrototype>())
            {
                if (proto.Abstract || !ProtoMan.EnumerateAllParents<EntityPrototype>(proto.ID).Any(p => bases.Contains(p.id)))
                    continue;

                helmets.Add(proto.ID);
                Assert.That(proto.Components.ContainsKey(factory.GetComponentName<EyeProtectionComponent>()), $"{proto.ID} doesn't protect from welding");
            }
        });
        Assert.That(helmets, Has.Count.GreaterThan(20).And.Contains("ClothingHeadHelmetEVA").And.Contains("ClothingHeadHelmetHardsuitEngineering"));
    }

    /// <summary>
    /// Welding while wearing an EVA helmet doesn't hurt your eyes.
    /// </summary>
    [Test]
    public async Task WeldingInEvaHelmetDoesNoEyeDamage()
    {
        var blindableComponent = Comp<BlindableComponent>(Player);
        await SpawnTarget(LockerPrototype);

        await Server.WaitPost(() =>
        {
            var helmet = SEntMan.SpawnEntity("ClothingHeadHelmetEVA", SEntMan.GetComponent<TransformComponent>(SPlayer).Coordinates);
            Assert.That(SEntMan.System<InventorySystem>().TryEquip(SPlayer, helmet, "head", silent: true, force: true));
        });

        await InteractUsing(Weld);
        Assert.That(blindableComponent.EyeDamage, Is.Zero, "Welding hurt the eyes through an EVA helmet");
    }
}
