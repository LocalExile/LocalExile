using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Actual_Claw_mastery_instant_percentage_follows_active_claws_and_Ghost_Reaver(bool ghost, int claws)
    {
        using var f = new Contact(new Draws(), "Fireball", claws > 0 ? "Nailed Fist" : "Rusted Sword");
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        if (claws == 2) inventory.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Nailed Fist")!));
        const ushort mastery = 19050, effect = 4516;
        var source = Program.Passives.FindEffect(effect)!;
        Assert.True(Program.Passives.IsEffectFor(mastery, source));
        Assert.Equal("Claw4", source.Id);
        Assert.Equal((Program.Stats.Key("life_leech_%_is_instant_per_equipped_claw")!.Value, 10), Assert.Single(source.Stats));
        f.Conn.Passives.Add(mastery);
        f.Conn.MasteryEffects = new() { [mastery] = effect };
        f.Set("global_cannot_crit", 1);
        f.Set("keystone_ghost_reaver", ghost ? 1 : 0);
        f.Set("life_leech_from_any_damage_permyriad", 10000);
        f.Set("energy_shield_leech_from_any_damage_permyriad", 10000);
        f.Refresh(); f.Conn.Pools = f.Conn.Pools with { Life = 1000, EnergyShield = 1000 };
        uint before = f.Conn.PlayerLife;
        await f.Launch();
        Assert.Equal(ghost ? 0u : (uint)(claws * 10), f.Conn.PlayerLife - before);
        Assert.Equal(ghost ? (uint)(claws * 10) : 0u, f.Conn.EnergyShield.Current);
        // Remove the actual mastery source; existing timed recovery is not advanced by a new hit query.
        f.Conn.MasteryEffects = [];
        before = f.Conn.PlayerLife;
        uint shield = f.Conn.EnergyShield.Current;
        await f.Launch();
        Assert.Equal(before, f.Conn.PlayerLife);
        Assert.Equal(shield, f.Conn.EnergyShield.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inactive_weapon_set_claws_count_only_after_switching_into_the_active_set(bool ghost)
    {
        using var f = new Contact(new Draws(), "Fireball", "Rusted Sword");
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        inventory.Add(PlayerInventories.Weapon2, 0, 0, ItemCatalog.Create(Program.Items.Find("Nailed Fist")!));
        inventory.Add(PlayerInventories.Offhand2, 0, 0, ItemCatalog.Create(Program.Items.Find("Nailed Fist")!));
        f.Set("global_cannot_crit", 1);
        f.Set("keystone_ghost_reaver", ghost ? 1 : 0);
        f.Set("life_leech_from_any_damage_permyriad", 10000);
        f.Conn.Passives.Add(19050); f.Conn.MasteryEffects = new() { [19050] = 4516 };
        f.Refresh(); f.Conn.Pools = f.Conn.Pools with { Life = 1000, EnergyShield = 1000 };
        uint before = f.Conn.PlayerLife;
        await f.Launch();
        Assert.Equal(before, f.Conn.PlayerLife); Assert.Equal(0u, f.Conn.EnergyShield.Current);
        inventory.SwapWeaponSets();
        await f.Launch();
        Assert.Equal(before + (ghost ? 0u : 20u), f.Conn.PlayerLife);
        Assert.Equal(ghost ? 20u : 0u, f.Conn.EnergyShield.Current);
        inventory.SwapWeaponSets();
        before = f.Conn.PlayerLife; uint es = f.Conn.EnergyShield.Current;
        await f.Launch();
        Assert.Equal(before, f.Conn.PlayerLife); Assert.Equal(es, f.Conn.EnergyShield.Current);
    }

    [Fact]
    public async Task Requirement_disabled_claw_does_not_enable_instant_mastery()
    {
        using var f = new Contact(new Draws(), "Fireball", "Imperial Claw");
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        var claw = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        f.Conn.Passives.Add(19050); f.Conn.MasteryEffects = new() { [19050] = 4516 };
        f.Set("global_cannot_crit", 1);
        f.Set("life_leech_from_any_damage_permyriad", 10000);
        f.Refresh(); f.Conn.Pools = f.Conn.Pools with { Life = 1000 };
        Assert.Contains(claw.Id, Program.InactiveItems[f.Conn.Character!]);
        uint before = f.Conn.PlayerLife;
        await f.Launch();
        Assert.Equal(before, f.Conn.PlayerLife);
        Assert.Contains(claw.Id, Program.InactiveItems[f.Conn.Character!]);
        Assert.True(Program.LeechOf(f.Conn).Life.Count > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Actual_Weapon_Master_grants_all_resource_instant_leech_once_while_wielding_a_claw(int claws)
    {
        using var f = new Contact(new Draws(), "Fireball", claws > 0 ? "Nailed Fist" : "Rusted Sword");
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        if (claws == 2) inventory.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Nailed Fist")!));
        f.Conn.Passives.Add(52575);
        f.Set("global_cannot_crit", 1);
        foreach (string pool in new[] { "life", "mana", "energy_shield" }) f.Set(pool + "_leech_from_any_damage_permyriad", 10000);
        f.Refresh(); f.Conn.Pools = f.Conn.Pools with { Life = 1000, EnergyShield = 1000 };
        f.Conn.PlayerMana = 50;
        Assert.Equal(20, Program.CharacterPlayerStats(f.Conn)[Program.Stats.Key("leech_%_is_instant_while_wielding_claw")!.Value]);
        uint before = f.Conn.PlayerLife, instant = claws > 0 ? 20u : 0u;
        await f.Launch();
        Assert.Equal(before + instant, f.Conn.PlayerLife);
        Assert.Equal(50u + instant, f.Conn.PlayerMana);
        Assert.Equal(instant, f.Conn.EnergyShield.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_Gore_Dancer_Life_instant_percentage_survives_conversion_without_affecting_native_ES(bool ghost)
    {
        using var f = new Contact(new Draws(), "Fireball");
        f.Conn.Passives.Add(29630);
        f.Set("global_cannot_crit", 1);
        f.Set("keystone_ghost_reaver", ghost ? 1 : 0);
        f.Set("life_leech_from_any_damage_permyriad", 10000);
        f.Set("energy_shield_leech_from_any_damage_permyriad", 10000);
        f.Refresh(); f.Conn.Pools = f.Conn.Pools with { Life = 1000, EnergyShield = 1000 };
        Assert.Equal(30, Program.CharacterPlayerStats(f.Conn)[Program.Stats.Key("life_leech_%_is_instant")!.Value]);
        uint before = f.Conn.PlayerLife;
        await f.Launch();
        Assert.Equal(before + (ghost ? 0u : 30u), f.Conn.PlayerLife);
        Assert.Equal(ghost ? 30u : 0u, f.Conn.EnergyShield.Current);
    }
}
