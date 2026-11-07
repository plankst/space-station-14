using Robust.Shared.Serialization;

namespace Content.Shared.Salvage.Magnet;

[Serializable, NetSerializable]
public sealed class SalvageMagnetBoundUserInterfaceState : BoundUserInterfaceState
{
    public TimeSpan? EndTime;
    public TimeSpan NextOffer;

    public TimeSpan Cooldown;
    public TimeSpan Duration; //try to change this

    public int ActiveSeed;

    public List<int> Offers;

    public SalvageMagnetBoundUserInterfaceState(List<int> offers)
    {
        Offers = offers;
    }
}
