namespace GameServer.Gateway.Tests;

public class DepositAffinityTabTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    [Fact]
    public void Deposit_sends_an_item_to_the_tab_tagged_for_it_else_its_special_tab_and_leaves_gear()
    {
        TestEnvironment.OpenOwnSaves("deposittab");
        var catalog = new ItemCatalog("data");
        string account = "DepositTab" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, catalog);
        uint chaos = catalog.All.First(t => t.Name == "Chaos Orb").Hash;
        uint gear = catalog.All.First(t => t.Class == "Ring").Hash;
        int currencyTab = Enumerable.Range(0, stash.Count).Single(i => stash.TabType(i) == StashRules.Currency);
        int grid = Enumerable.Range(0, stash.Count).First(i => StashRules.IsGrid(stash.TabType(i)!.Value));

        Assert.Equal(currencyTab, stash.AffinityTab(chaos));
        Assert.Null(stash.AffinityTab(gear));
        Assert.NotNull(stash.SetAffinities(grid, [(StashRules.Currency, true)]));
        Assert.Equal(grid, stash.AffinityTab(chaos));
        Assert.Null(stash.AffinityTab(gear));
        AccountStash.Forget(account);
    }
}
