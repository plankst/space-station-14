using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared.SprayPainter.Components;

/// <summary>
/// Used to mark an entity that has been repainted.
/// </summary>
[RegisterComponent, NetworkedComponent]
[AutoGenerateComponentState, AutoGenerateComponentPause]
public sealed partial class PaintedComponent : Component
{
    /// <summary>
    /// The time after which the entity is dried and does not appear as "freshly painted".
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan DryTime;

    /// <summary>
    /// The style the entity was painted with. The visuals themselves live in appearance data, which isn't saved, so
    /// this is what lets a painted locker/crate/door keep its paint after a world save is loaded.
    /// </summary>
    [DataField]
    public string? Prototype;
}
