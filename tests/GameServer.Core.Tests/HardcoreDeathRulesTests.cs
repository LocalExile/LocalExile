using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Login;
using GameServer.Storage;

namespace GameServer.Core.Tests;

public class HardcoreDeathRulesTests
{
    static readonly DateTimeOffset DiedAt = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
    static Character NewCharacter(string league) => new(Guid.NewGuid(), Guid.NewGuid(), "DeathProbe", CharacterClass.Witch,
        12, league, "1_1_2", DiedAt.AddDays(-1), DiedAt, "1_1_town", 3);

    [Theory]
    [InlineData("Hardcore", "Standard", false)]
    [InlineData("SSF Hardcore", "SSF Standard", false)]
    [InlineData("Hardcore Allflame", "Allflame", false)]
    [InlineData("HC SSF Allflame", "SSF Allflame", false)]
    [InlineData("Hardcore Ruthless", "Void", true)]
    [InlineData("SSF Hardcore Ruthless", "Void", true)]
    [InlineData("HC Ruthless Allflame", "Void", true)]
    [InlineData("HC SSF R Allflame", "Void", true)]
    public void Death_retains_base_and_ssf_unless_ruthless_is_permanently_dead(string league, string target, bool terminal)
    {
        var source = LeagueCatalog.Default.ResolveSavedLeague(league)!;
        var original = NewCharacter(league);
        var dead = HardcoreDeathRules.Apply(original, source, DiedAt);
        Assert.Equal(target, dead.League);
        Assert.Equal(new HardcoreDeathRecord(league, DiedAt, terminal), dead.HardcoreDeath);
        Assert.Equal(original with { League = target, HardcoreDeath = dead.HardcoreDeath }, dead);
        Assert.Equal(!terminal, HardcoreDeathRules.CanEnter(dead));
        Assert.Same(dead, HardcoreDeathRules.Apply(dead, source, DiedAt.AddMinutes(1)));
    }

    [Fact]
    public void A_custom_active_challenge_uses_its_softcore_base()
    {
        var source = new LeagueCatalog(["FutureLeague"]).Find("HC SSF FutureLeague")!;
        Assert.Equal("SSF FutureLeague", HardcoreDeathRules.Apply(NewCharacter(source.Name), source, DiedAt).League);
    }

    [Fact]
    public void A_softcore_or_unrelated_league_cannot_be_used_to_migrate_a_character()
    {
        Assert.Throws<ArgumentException>(() => HardcoreDeathRules.Apply(NewCharacter("Standard"),
            LeagueCatalog.Default.Find("Standard")!, DiedAt));
        Assert.Throws<InvalidOperationException>(() => HardcoreDeathRules.Apply(NewCharacter("Hardcore"),
            LeagueCatalog.Default.Find("Hardcore Allflame")!, DiedAt));
    }

    [Fact]
    public void Tickets_from_the_source_league_or_a_reused_character_name_are_rejected()
    {
        var live = NewCharacter("Hardcore Allflame");
        Assert.True(HardcoreDeathRules.TicketMatches(live, live.Id, "hardcore allflame"));
        Assert.False(HardcoreDeathRules.TicketMatches(live, Guid.NewGuid(), live.League));
        var dead = HardcoreDeathRules.Apply(live, LeagueCatalog.Default.Find(live.League)!, DiedAt);
        Assert.False(HardcoreDeathRules.TicketMatches(dead, live.Id, live.League));
        Assert.True(HardcoreDeathRules.TicketMatches(dead, live.Id, dead.League));
        var ruthless = NewCharacter("Hardcore Ruthless");
        var permanent = HardcoreDeathRules.Apply(ruthless, LeagueCatalog.Default.Find(ruthless.League)!, DiedAt);
        Assert.False(HardcoreDeathRules.TicketMatches(permanent, permanent.Id, "Void"));
    }

    [Fact]
    public void Login_does_not_issue_an_instance_assignment_for_a_permanently_dead_character()
    {
        var store = new JsonAccountStore();
        var accounts = new AccountService(store);
        var account = accounts.Register("DeathAccount", "pw1234").Value!;
        var character = accounts.CreateCharacter(account.Id, "DeathProbe", CharacterClass.Witch, "HC SSF R Allflame").Value!;
        store.UpdateCharacter(HardcoreDeathRules.Apply(character, LeagueCatalog.Default.Find(character.League)!, DiedAt));
        bool assigned = false;
        var session = new LoginServerSession(accounts) { Assign = _ => { assigned = true; return (0x235c, 1); } };
        session.Handle(new AccountLogin(2, account.Name, new byte[32], AccountService.ClientPasswordDigest("pw1234"),
            new byte[32], false, false, 0x40, 0));
        var reply = session.Handle(new U8U32(0, 0));
        Assert.False(assigned);
        Assert.DoesNotContain(reply, p => p is InstanceInfo);
        var list = Assert.IsType<List15>(reply[0]);
        Assert.Equal("Void", Assert.Single(list.Entries).Text);
        Assert.Equal(1, Assert.Single(list.Entries).B49);
        Assert.Equal(new U16Message(0x0f, LoginServerSession.ResultLeagueDoesNotExist), reply[1]);
    }

    [Fact]
    public void Json_store_preserves_committed_death_during_stale_level_and_town_updates()
    {
        var store = new JsonAccountStore();
        var original = NewCharacter("Hardcore");
        store.AddCharacter(original);
        var dead = HardcoreDeathRules.Apply(original, LeagueCatalog.Default.Find(original.League)!, DiedAt);
        store.UpdateCharacter(dead);
        store.UpdateCharacter(original with { Level = 20, LastTown = "1_2_town" });
        Assert.Equal(dead with { Level = 20, LastTown = "1_2_town" }, store.GetCharacter(original.Id));
    }
}
