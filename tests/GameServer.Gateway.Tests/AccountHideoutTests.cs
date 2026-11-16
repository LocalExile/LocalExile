using GameServer.Core.Accounts;
using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed class AccountHideoutTests : IDisposable
{
    readonly SqliteAccountStore store;
    readonly List<string> accounts = [];

    public AccountHideoutTests()
    {
        TestEnvironment.OpenOwnSaves(nameof(AccountHideoutTests));
        store = new(Saves.Db);
    }

    Account Account()
    {
        var account = new Account(Guid.NewGuid(), $"Home{Guid.NewGuid():N}", "", "", DateTimeOffset.UnixEpoch);
        store.AddAccount(account);
        accounts.Add(account.Name);
        return account;
    }

    ClientState Player(Account account, string name, int order, string? area = null, string league = "Standard")
    {
        var created = DateTimeOffset.UnixEpoch.AddMinutes(order);
        store.AddCharacter(new(Guid.NewGuid(), account.Id, name, CharacterClass.Witch, 1, league,
            AccountService.StartingArea, created, created));
        CharacterSaves.Write(name, new([], Experience: 123, SelectedHideout: area));
        var home = HideoutCatalog.Selected(area)!;
        return new(new(home.AreaHash16, home.AreaId, 100, 100, 20, 20, null, []), 1, 1)
            { Account = account.Name, Character = name, League = league };
    }

    static ClientState Visit(ClientState player, ClientState owner) => new(owner.Layout, 1, 1)
        { Account = player.Account, Character = player.Character, League = player.League, InstanceOwner = owner.Character };

    [Fact]
    public void First_selected_home_and_layout_are_shared_across_characters_and_leagues()
    {
        var account = Account();
        var home = HideoutCatalog.Hideouts.First(h => h.AreaId != HideoutCatalog.DefaultArea);
        var oldest = Player(account, "HomeOlder", 0);
        var founder = Player(account, "HomeFounder", 1, home.AreaId);
        var alternate = Player(account, "HomeAlternate", 2, HideoutCatalog.DefaultArea, "Hardcore");
        var position = new HideoutPlacement(42, 53, 90);
        var statue = new CharacterSaves.SavedMonsterStatue(20, 30, 45, 7);
        CharacterSaves.SetHideoutPlacement(founder.Character!, home.AreaId, "stash", position);
        CharacterSaves.AddMonsterStatue(founder.Character!, home.AreaId, statue);

        Game.InitializeAccountHideouts();
        Assert.Equal(home.AreaId, CharacterHome.SelectedHideout(Saves.Db, alternate.Character!));
        Assert.Equal(home.AreaId, CharacterHome.SelectedHideout(Saves.Db, oldest.Character!));
        Assert.Equal(home.AreaId, Game.HomeHideout(alternate)!.AreaId);
        Assert.Equal(home.AreaId, Game.HomeHideout(oldest)!.AreaId);
        alternate.Use(founder.Layout);
        Assert.Equal(position, Game.SavedHideoutPlacements(alternate)!["stash"]);
        Assert.Equal(statue, Assert.Single(Game.AccountMonsterStatues(alternate)));
        Assert.All(store.CharactersOf(account.Id), c =>
        {
            Assert.Equal(home.AreaId, CharacterSaves.Load(c.Name)!.SelectedHideout);
            Assert.Equal(123u, CharacterSaves.Load(c.Name)!.Experience);
        });

        var moved = new HideoutPlacement(67, 89, 180);
        Game.SetAccountHideoutPlacement(alternate, home.AreaId, "stash", moved);
        Game.AddAccountMonsterStatue(alternate, statue with { X = 60 });
        AccountStash.Forget(account.Name);
        Assert.Equal(moved, Game.SavedHideoutPlacements(founder)!["stash"]);
        Assert.Equal(2, Game.AccountMonsterStatues(founder).Count);
        store.DeleteCharacter(store.FindCharacter(founder.Character!)!.Id);
        Assert.Equal(home.AreaId, Game.HomeHideout(oldest)!.AreaId);
        Assert.Equal(moved, Game.SavedHideoutPlacements(alternate)!["stash"]);
    }

    [Fact]
    public void Explicit_migration_runs_once_and_later_choices_and_edits_stay_account_owned()
    {
        var account = Account();
        var home = HideoutCatalog.Hideouts.First(h => h.AreaId != HideoutCatalog.DefaultArea);
        var older = Player(account, "HomeOriginal", 0, HideoutCatalog.DefaultArea);
        var originalLayout = older.Layout;
        var preferred = Player(account, "HomePreferred", 1, home.AreaId);
        var outsider = Player(Account(), "HomeVisitor", 0, home.AreaId);
        var position = new HideoutPlacement(12, 34, 56);
        CharacterSaves.SetHideoutPlacement(preferred.Character!, home.AreaId, "waypoint", position);
        CharacterSaves.SetHideoutPlacement(older.Character!, HideoutCatalog.DefaultArea, "stash", position);
        // A failed legacy mirror write must not commit the new account profile on its own.
        AccountStash.For(account.Name, Program.Items);
        string? beforeMigration = Saves.Get(SaveDatabase.Table.Stashes, account.Name);
        try
        {
            Saves.FailWrite = name => name == older.Character;
            Assert.Throws<IOException>(() => Game.InitializeAccountHideout(preferred.Character!));
        }
        finally { Saves.FailWrite = null; }
        Assert.Equal(beforeMigration, Saves.Get(SaveDatabase.Table.Stashes, account.Name));
        Assert.Equal(HideoutCatalog.DefaultArea, CharacterHome.SelectedHideout(Saves.Db, older.Character!));
        Game.InitializeAccountHideout(preferred.Character!);
        Assert.Equal(home.AreaId, Game.HomeHideout(older)!.AreaId);
        Assert.True(Game.OwnHideout(Visit(older, preferred)));
        var visitor = Visit(outsider, preferred);
        Assert.False(Game.OwnHideout(visitor));
        Assert.Equal(position, Game.SavedHideoutPlacements(visitor)!["waypoint"]);

        // Selection and every Auth compatibility field are one save, even if a later character write fails.
        string? beforeSelection = Saves.Get(SaveDatabase.Table.Stashes, account.Name);
        try
        {
            Saves.FailWrite = name => name == preferred.Character;
            Assert.Throws<IOException>(() => Game.SelectAccountHideout(older, HideoutCatalog.DefaultArea));
        }
        finally { Saves.FailWrite = null; }
        Assert.Equal(beforeSelection, Saves.Get(SaveDatabase.Table.Stashes, account.Name));
        Assert.Equal(home.AreaId, CharacterHome.SelectedHideout(Saves.Db, older.Character!));
        Assert.Equal(home.AreaId, Game.HomeHideout(preferred)!.AreaId);
        Game.SelectAccountHideout(older, HideoutCatalog.DefaultArea);
        Game.InitializeAccountHideout(preferred.Character!);
        Assert.Equal(HideoutCatalog.DefaultArea, Game.HomeHideout(preferred)!.AreaId);
        older.Use(originalLayout);
        Assert.Null(Game.SavedHideoutPlacements(older)); // Another character's old layout cannot reappear.
        Assert.Equal(home.AreaId, Game.HomeHideout(outsider)!.AreaId);
        Assert.Null(Game.SavedHideoutPlacements(outsider));
    }

    public void Dispose()
    {
        foreach (string account in accounts) AccountStash.Forget(account);
        TestEnvironment.ReleaseOwnSaves();
    }
}
