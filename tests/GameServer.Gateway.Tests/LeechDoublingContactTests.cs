using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Actual_Highwayman_mod_doubles_total_Life_recovery_once_and_clears_with_its_source(int rings)
    {
        using var f = new Contact(new Draws(), "Fireball");
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        var mod = Program.Items.Mods.ById("TotalRecoveryLifeLeechDoubledUnique__1")!;
        const string stat = "total_recovery_per_minute_from_life_leech_is_doubled";
        Assert.Equal(stat, Assert.Single(mod.Stats).Id);
        var items = Enumerable.Range(6, rings).Select(row =>
            (Row: (byte)row, Item: inventory.Add((byte)row, 0, 0, ItemCatalog.Create(Program.Items.Find("Gold Ring")!) with
                { Mods = [new(ModSection.Explicit, mod.Hash, [1])] }))).ToArray();
        f.Set("global_cannot_crit", 1);
        foreach (string pool in new[] { "life", "mana", "energy_shield" }) f.Set(pool + "_leech_from_any_damage_permyriad", 10000);
        f.Conn.Pools = Program.PoolsFor(f.Conn, 60) with
        { Life = 1000, Mana = 1000, EnergyShield = 1000, Strength = 1000, Dexterity = 1000, Intelligence = 1000 };
        f.Conn.PlayerMana = 500;
        Assert.Equal(rings, Program.CharacterPlayerStats(f.Conn)[Program.Stats.Key(stat)!.Value]);
        await f.Launch();
        var state = Program.LeechOf(f.Conn);
        var first = state.TakeRecovery(f.Conn.Clock.Elapsed.TotalSeconds + 10);
        Assert.Equal(200, first.Life, 6); Assert.Equal(100, first.Mana, 6); Assert.Equal(100, first.EnergyShield, 6);
        state.Clear(f.Conn.Clock.Elapsed.TotalSeconds);
        foreach (var item in items) inventory.Change(item.Row, item.Item.Id, ring => ring with { Mods = [] });
        Assert.Equal(0, Program.CharacterPlayerStats(f.Conn).GetValueOrDefault(Program.Stats.Key(stat)!.Value));
        await f.Launch();
        var second = state.TakeRecovery(f.Conn.Clock.Elapsed.TotalSeconds + 10);
        Assert.Equal(100, second.Life, 6); Assert.Equal(100, second.Mana, 6); Assert.Equal(100, second.EnergyShield, 6);
    }
}
