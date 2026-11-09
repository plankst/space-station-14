namespace Content.Server.Salvage.Magnet;

/// <summary>
/// Marks the temporary map a salvage magnet builds its asteroid / debris / wreck on before pulling it to the station.
/// It's normally deleted right after the pull; world saves never keep one (see WorldPersistenceSystem).
/// </summary>
[RegisterComponent]
public sealed partial class SalvageMagnetHoldingMapComponent : Component;
