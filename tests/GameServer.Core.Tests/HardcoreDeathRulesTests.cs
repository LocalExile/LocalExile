using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Login;
using GameServer.Storage;

namespace GameServer.Core.Tests;

public class HardcoreDeathRulesTests
{
    static readonly DateTimeOffset DiedAt = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
    static Character NewCharacter(string league) => new(Guid.NewGuid(), Guid.NewGuid(), "DeathProbe", CharacterClass.Witch,
        12, league, "1_1_2", DiedAt.AddDays(-1), DiedAt, "1_1_town", 3);

    // toVoid is what the Game module does when the league's rules have HardcoreDeathToVoid (Ruthless's setting, off by
    // default): the death is permanent.
    [Theory]
    [InlineData("Hardcore", "Standard", false, false)]
    [InlineData("Hardcore Ruthless", "Ruthless", false, false)]
    [InlineData("SSF Hardcore Ruthless", "SSF Ruthless", false, false)]
    [InlineData("Hardcore Ruthless", "Void", true, true)]
    [InlineData("HC SSF R Allflame", "Void", true, true)]
    public void Death_retains_base_and_ssf_unless_ruthless_is_permanently_dead(string league, string target, bool toVoid, bool terminal)
    {
        var source = LeagueCatalog.FromEnvironment().ResolveSavedLeague(league)! with { PermanentDeath = toVoid };
        var original = NewCharacter(league);
        var dead = HardcoreDeathRules.Apply(original, source, DiedAt);
        Assert.Equal(target, dead.League);
        Assert.Equal(new HardcoreDeathRecord(league, DiedAt, terminal), dead.HardcoreDeath);
        Assert.Equal(original with { League = target, HardcoreDeath = dead.HardcoreDeath }, dead);
        Assert.Equal(!terminal, HardcoreDeathRules.CanEnter(dead));
        Assert.Same(dead, HardcoreDeathRules.Apply(dead, source, DiedAt.AddMinutes(1)));
    }

}
