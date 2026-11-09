#nullable enable
using System.Linq;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Ghost.Roles;
using Content.Server.Ghost.Roles.Components;
using Content.Shared.Antag;
using Content.Shared.Antag.Components;
using Content.Shared.CCVar;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.GameRules;

/// <summary>
/// With <see cref="CCVars.GameNoAntagRoles"/> on, nobody can become an antagonist by any normal route: game rules
/// picking players (round start, late join, mid round), antag ghost role spawners, or event ghost roles that make you
/// an antagonist. With it off, everything works as usual.
/// </summary>
[TestFixture]
public sealed class NoAntagRolesTest : AntagTest
{
    public override PoolSettings PoolSettings => new()
    {
        Dirty = true,
        DummyTicker = false,
        Connected = true,
        Map = PoolManager.TestStation
    };

    [SidedDependency(Side.Server)] private GhostRoleSystem _ghostRole = default!;

    [Test]
    [TestCase(true)]
    [TestCase(false)]
    [RunOnSide(Side.Server)]
    public void GameRulesCantPickAntags(bool noAntags)
    {
        Server.CfgMan.SetCVar(CCVars.GameNoAntagRoles, noAntags);
        try
        {
            STicker.StartGameRule("Traitor", out var gameRule);
            var ruleUid = gameRule!.Value.Owner;
            var rule = new Entity<AntagSelectionComponent>(ruleUid, SEntMan.GetComponent<AntagSelectionComponent>(ruleUid));
            var traitor = SProtoMan.Index<AntagSpecifierPrototype>(rule.Comp.Antags.First().Proto);
            var player = ServerSession!;

            // Every way a game rule picks an antag goes through this check.
            Assert.That(AntagSys.CanBeAntag(player, rule, traitor, checkPref: false), Is.EqualTo(!noAntags),
                noAntags ? "A player could still be picked as a traitor with antagonists turned off" : "Normal antag picking is broken");

            STicker.ClearGameRules();
        }
        finally
        {
            Server.CfgMan.SetCVar(CCVars.GameNoAntagRoles, false);
        }
    }

    [Test]
    [TestCase(true)]
    [TestCase(false)]
    [RunOnSide(Side.Server)]
    public void NoAntagGhostRoles(bool noAntags)
    {
        Server.CfgMan.SetCVar(CCVars.GameNoAntagRoles, noAntags);
        try
        {
            // A rule whose antag is a ghost role (space dragon): its spawner isn't even created.
            STicker.StartGameRule("DragonSpawn", out _);
            var spawners = SEntMan.Count<GhostRoleAntagSpawnerComponent>();
            Assert.That(spawners > 0, Is.EqualTo(!noAntags),
                noAntags ? "An antag ghost role spawner was created with antagonists turned off" : "Antag ghost role spawners are broken");
            STicker.ClearGameRules();

            // Event ghost roles that make you an antagonist (rat king) are hidden and can't be taken; harmless ones
            // (Remilia, a familiar) are still there.
            var coords = new MapCoordinates(0, 0, STicker.DefaultMap);
            var ratKing = SEntMan.SpawnEntity("SpawnPointGhostRatKing", coords);
            var familiar = SEntMan.SpawnEntity("SpawnPointGhostRemilia", coords);
            var ratKingId = SEntMan.GetComponent<GhostRoleComponent>(ratKing).Identifier;
            var familiarId = SEntMan.GetComponent<GhostRoleComponent>(familiar).Identifier;

            var listed = _ghostRole.GetGhostRolesInfo(ServerSession).Select(r => r.Identifier).ToList();
            Assert.That(listed, Does.Contain(familiarId), "A harmless ghost role is missing from the list");
            Assert.That(listed.Contains(ratKingId), Is.EqualTo(!noAntags),
                noAntags ? "An antagonist ghost role is listed with antagonists turned off" : "Antagonist ghost roles are missing");

            if (noAntags)
                Assert.That(_ghostRole.Takeover(ServerSession!, ratKingId), Is.False, "An antagonist ghost role could be taken");

            SEntMan.DeleteEntity(ratKing);
            SEntMan.DeleteEntity(familiar);
        }
        finally
        {
            Server.CfgMan.SetCVar(CCVars.GameNoAntagRoles, false);
        }
    }
}
