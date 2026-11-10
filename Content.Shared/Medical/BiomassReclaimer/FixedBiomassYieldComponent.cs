using Robust.Shared.GameStates;

namespace Content.Shared.Medical.BiomassReclaimer;

/// <summary>
/// Lets an item (e.g. an organ) go in the biomass reclaimer, and gives a fixed amount of biomass for it instead of the
/// usual amount based on its mass.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class FixedBiomassYieldComponent : Component
{
    /// <summary>
    /// How much biomass the reclaimer makes from this.
    /// </summary>
    [DataField]
    public float Amount = 10f;
}
