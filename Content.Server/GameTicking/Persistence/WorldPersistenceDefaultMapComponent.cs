namespace Content.Server.GameTicking.Persistence;

/// <summary>
/// Marks the round's main map in a world save, so it can be used as the main map again after loading.
/// </summary>
[RegisterComponent]
public sealed partial class WorldPersistenceDefaultMapComponent : Component;
