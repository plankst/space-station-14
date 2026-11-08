using Robust.Shared.Audio;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.IntegrationTests.Tests.Persistence;

/// <summary>
/// Throws when an entity with it is saved, because resolved sounds can't be written to a save file.
/// Used to check that one broken entity doesn't stop the whole world from saving.
/// </summary>
[RegisterComponent]
public sealed partial class UnsavableTestComponent : Component
{
    [DataField]
    public ResolvedSoundSpecifier? Song;
}
