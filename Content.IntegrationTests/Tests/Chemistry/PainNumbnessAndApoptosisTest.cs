using Content.IntegrationTests.Fixtures;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using Content.Shared.StatusEffectNew;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Chemistry;

/// <summary>
/// Pain numbness softens brute hits by 5%, safe apoptosis softens radiation by 2.5%,
/// and THC gives both while clearing woozy.
/// </summary>
[TestFixture]
public sealed class PainNumbnessAndApoptosisTest : GameTest
{
    private const string PainNumbness = "StatusEffectPainNumbness";
    private const string SafeApoptosis = "StatusEffectSafeApoptosis";
    private const string Woozy = "StatusEffectWoozy";

    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: NumbnessDummy
  components:
  - type: Damageable
  - type: Injurable
    damageContainer: Biological
  - type: MobState
  - type: MobThresholds
    thresholds:
      0: Alive
      1000: Dead
  - type: StatusEffectContainer
";

    private static FixedPoint2 Hit(IEntityManager entMan, IPrototypeManager protoMan, EntityUid target, string type)
    {
        var damage = new DamageSpecifier(protoMan.Index<DamageTypePrototype>(type), FixedPoint2.New(100));
        entMan.System<DamageableSystem>().TryChangeDamage(target, damage, out var dealt);
        return dealt.DamageDict.TryGetValue(type, out var amount) ? amount : FixedPoint2.Zero;
    }

    [Test]
    public async Task NumbnessAndApoptosisReduceDamage()
    {
        var server = Pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var status = entMan.System<StatusEffectsSystem>();

        await server.WaitAssertion(() =>
        {
            var plain = entMan.SpawnEntity("NumbnessDummy", MapCoordinates.Nullspace);
            var numb = entMan.SpawnEntity("NumbnessDummy", MapCoordinates.Nullspace);
            Assert.That(status.TryAddStatusEffectDuration(numb, PainNumbness, TimeSpan.FromMinutes(1)));
            Assert.That(status.TryAddStatusEffectDuration(numb, SafeApoptosis, TimeSpan.FromMinutes(1)));

            Assert.Multiple(() =>
            {
                foreach (var type in new[] { "Blunt", "Slash", "Piercing" })
                {
                    Assert.That(Hit(entMan, protoMan, plain, type), Is.EqualTo(FixedPoint2.New(100)), type);
                    Assert.That(Hit(entMan, protoMan, numb, type), Is.EqualTo(FixedPoint2.New(95)), type);
                }

                Assert.That(Hit(entMan, protoMan, plain, "Radiation"), Is.EqualTo(FixedPoint2.New(100)));
                Assert.That(Hit(entMan, protoMan, numb, "Radiation"), Is.EqualTo(FixedPoint2.New(97.5)));

                // Other types are untouched.
                Assert.That(Hit(entMan, protoMan, numb, "Heat"), Is.EqualTo(FixedPoint2.New(100)));
            });
        });
    }

    [Test]
    public async Task ThcNumbsProtectsAndClearsWoozy()
    {
        var server = Pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var status = entMan.System<StatusEffectsSystem>();
        var effects = entMan.System<SharedEntityEffectsSystem>();

        await server.WaitAssertion(() =>
        {
            var user = entMan.SpawnEntity("NumbnessDummy", MapCoordinates.Nullspace);
            Assert.That(status.TryAddStatusEffectDuration(user, Woozy, TimeSpan.FromMinutes(1)));

            var thc = protoMan.Index<ReagentPrototype>("THC");
            foreach (var entry in thc.Metabolisms!.Metabolisms.Values)
            {
                foreach (var effect in entry.Effects)
                    effects.ApplyEffect(user, effect);
            }

            Assert.Multiple(() =>
            {
                Assert.That(status.HasStatusEffect(user, PainNumbness), "THC should numb pain");
                Assert.That(status.HasStatusEffect(user, SafeApoptosis), "THC should give safe apoptosis");
                Assert.That(status.HasStatusEffect(user, Woozy), Is.False, "THC should clear woozy");
            });
        });
    }
}
