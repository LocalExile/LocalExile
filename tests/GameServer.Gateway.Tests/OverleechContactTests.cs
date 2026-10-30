using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData(null, "Rusted Sword", true)]
    [InlineData(null, "Crude Bow", true)]
    [InlineData("Cyclone", "Rusted Sword", true)]
    [InlineData("BurningArrow", "Crude Bow", true)]
    [InlineData("Fireball", "Rusted Sword", false)]
    [InlineData("IceNova", "Rusted Sword", false)]
    public async Task Actual_attack_overleech_item_stat_retains_only_attack_instances(string? gem, string weapon, bool attack)
    {
        using var f = new Contact(new Draws(), gem, weapon);
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        var held = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        var mod = Program.Items.Mods.ById("ESLeechFromAttacksNotRemovedOnFullESUnique__1")!;
        Assert.Equal("energy_shield_leech_from_attacks_does_not_stop_on_full_energy_shield", Assert.Single(mod.Stats).Id);
        // Real source row on a controlled weapon fixture; this does not assert crafting eligibility.
        inventory.Change(PlayerInventories.Weapon1, held.Id,
            item => item with { Mods = [new(ModSection.Explicit, mod.Hash, [1])] });
        f.Set("global_cannot_crit", 1);
        f.Set("energy_shield_leech_from_any_damage_permyriad", 100);
        f.Refresh();
        f.Conn.Pools = f.Conn.Pools with { EnergyShield = 1000 };
        f.Conn.EnergyShield.SetMaximum(1000, refill: true);
        await f.Use(); await f.HitLands();
        var session = Program.LeechOf(f.Conn);
        // This checks retention at the accepted full-pool hit. A later condition query may legitimately expire
        // this short instance; exact expiry/condition behavior has separate explicit-time/session controls.
        Assert.Equal(attack ? 1 : 0, session.EnergyShield.Count);
        inventory.Change(PlayerInventories.Weapon1, held.Id, item => item with { Mods = [] });
        f.Refresh(); f.Conn.Pools = f.Conn.Pools with { EnergyShield = 1000 };
        // Refresh restored the fixture's real (zero-ES) gear pool; restore full ES for the removal condition.
        f.Conn.EnergyShield.SetMaximum(1000, refill: true);
        Assert.Equal(0, Program.CharacterPlayerStats(f.Conn).GetValueOrDefault(
            Program.Stats.Key("energy_shield_leech_from_attacks_does_not_stop_on_full_energy_shield")!.Value));
        Assert.Equal(0, session.EnergyShield.Count);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void Life_overleech_virtual_replacement_controls_projection_and_recovery(int? replacement, int expected)
    {
        using var f = new Contact(new Draws());
        f.Set("life_leech_does_not_stop_at_full_life", 1);
        if (replacement is int value) f.Set("virtual_life_leech_does_not_stop_at_full_life", value);
        f.Conn.PlayerLife = f.Conn.PlayerMaxLife;
        var stats = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(expected, stats.GetValueOrDefault(Program.Stats.Key("virtual_life_leech_does_not_stop_at_full_life")!.Value));
        var session = Program.LeechOf(f.Conn);
        session.Hit(f.Conn.Clock.Elapsed.TotalSeconds, (100, 0, 0), true, false, false);
        Assert.Equal(expected, session.Life.Count);
    }
}
