namespace Content.Server.Zombies;

/// <summary>
/// Turns a freshly spawned mob into an NPC zombie (e.g. the infected crew on salvage expeditions).
/// Done a tick after spawning, so its random appearance and clothing are already in place.
/// Removed once the mob has been zombified.
/// </summary>
[RegisterComponent]
public sealed partial class ZombifyOnSpawnComponent : Component
{
    /// <summary>
    /// Chance for this zombie's bite to infect someone not wearing any protective gear.
    /// Armor and bio suits lower it as usual.
    /// </summary>
    [DataField]
    public float InfectionChance = 0.5f;
}
