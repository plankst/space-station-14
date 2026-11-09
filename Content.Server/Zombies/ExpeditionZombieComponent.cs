namespace Content.Server.Zombies;

/// <summary>
/// Marks an NPC zombie made by <see cref="ZombifyOnSpawnComponent"/>. These belong to their expedition and are left out
/// of world saves, so they never come home with the crew.
/// </summary>
[RegisterComponent]
public sealed partial class ExpeditionZombieComponent : Component;
