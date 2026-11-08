using Content.Server.GameTicking.Persistence;
using Content.Shared.CCVar;
using Content.Shared.Mind;
using Content.Shared.Players;
using Content.Shared.Preferences;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.GameTicking;

// World persistence: loading the world save instead of a fresh map, and putting players back into their saved bodies.
public sealed partial class ServerGameTicker
{
    [Dependency] private WorldPersistenceSystem _worldPersistence = default!;
    [Dependency] private PersistentPlayerFixSystem _playerFix = default!;
    [Dependency] private PersistentDispenserFixSystem _dispenserFix = default!;
    [Dependency] private PersistentExpeditionFixSystem _expeditionFix = default!;

    /// <summary>
    /// True if the current round's world was loaded from the world save instead of a fresh map.
    /// </summary>
    [ViewVariables]
    public bool WorldRestoredFromSave { get; private set; }

    /// <summary>
    /// Loads the world save, if world persistence is on and a save exists.
    /// </summary>
    private bool TryLoadPersistentWorld()
    {
        WorldRestoredFromSave = false;

        if (!_worldPersistence.Enabled)
            return false;

        if (!_worldPersistence.TryLoadWorld(out _, out var mapId))
            return false;

        DefaultMap = mapId;
        WorldRestoredFromSave = true;
        RelinkLoadedMinds();

        // Expeditions and FTL travel don't survive a save; clean up their leftovers.
        _expeditionFix.FixAfterLoad();

        // TEMPORARY workaround for players who can't move after loading (game.temp_playerfix).
        if (_playerFix.Enabled)
            _playerFix.ReplacePlayerBodies();

        // TEMPORARY workaround for broken reagent dispensers after loading (game.temp_dispensefix).
        if (_dispenserFix.Enabled)
            _dispenserFix.ReplaceDispensers();

        return true;
    }

    /// <summary>
    /// Loaded minds don't go through the usual "assign mind to player" path, so any player data the server already
    /// has for those players (e.g. they're sitting in the lobby) still points at no mind. Fix that up.
    /// </summary>
    private void RelinkLoadedMinds()
    {
        var query = EntityQueryEnumerator<MindComponent>();
        while (query.MoveNext(out var mindId, out var mind))
        {
            if (mind.UserId is not { } userId)
                continue;

            if (!_playerManager.TryGetPlayerData(userId, out var data) || data.ContentData() is not { } content)
                continue;

            content.Mind = mindId;
        }
    }

    /// <summary>
    /// After loading a world save, put every connected player who still has a body in the world back into it,
    /// instead of spawning them a new character. Those players are removed from the round-start spawn list.
    /// </summary>
    private void RestorePersistentPlayers(
        List<ICommonSession> readyPlayers,
        Dictionary<NetUserId, HumanoidCharacterProfile> profiles)
    {
        if (!WorldRestoredFromSave)
            return;

        foreach (var session in _playerManager.Sessions)
        {
            if (session.Status != SessionStatus.InGame)
                continue;

            if (!Mind.TryGetMind(session.UserId, out var mindId, out var mind))
                continue;

            if (mind.CurrentEntity is not { } body || Deleted(body))
                continue;

            if (!_playerManager.SetAttachedEntity(session, body))
            {
                Log.Error($"Failed to put {session} back into their saved body {ToPrettyString(body)}");
                continue;
            }

            _pvsOverride.AddSessionOverride(mindId.Value, session);
            readyPlayers.Remove(session);
            profiles.Remove(session.UserId);
            PlayerJoinGame(session, silent: true);
            Log.Info($"Restored {session} into their saved body {ToPrettyString(body)}");
        }
    }

    /// <summary>
    /// Saves the world right before a round restart wipes it, so the next round continues from it.
    /// </summary>
    private void SaveWorldBeforeRestart()
    {
        if (!Cfg.GetCVar(CCVars.PersistenceSaveOnRoundRestart))
            return;

        if (!_worldPersistence.CanSave())
            return;

        // Bring players home from expeditions and put held items on the floor first.
        _worldPersistence.PrepareForFinalSave();
        _worldPersistence.SaveWorld("round restart");
    }
}
