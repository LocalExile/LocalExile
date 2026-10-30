using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData("Cyclone", "Rusted Sword", 200)]
    [InlineData("PoisonArrow", "Crude Bow", 200)]
    [InlineData("Fireball", "Rusted Sword", 100)]
    [InlineData("IceNova", "Rusted Sword", 150)]
    public async Task Parsed_supported_contacts_use_Controlled_Destructions_less_critical_chance(string gem, string weapon, int chance)
    {
        var random = new Draws(300); // A crit under the old unmodified chance; a noncrit after the real support's LESS.
        using var f = new Contact(random, gem, weapon, distance: weapon == "Crude Bow" ? 60 : 2);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        var host = Assert.Single(inventory.Items(PlayerInventories.BodyArmour));
        var support = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemControlledDestruction")!);
        Assert.NotNull(inventory.Change(PlayerInventories.BodyArmour, host.Id,
            item => item with { Sockets = [item.Sockets[0], new(SocketColour.White, support)], Links = [2] }));
        f.Refresh();
        var selected = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill);
        Assert.Single(selected.Supports);
        Assert.Contains((Program.Stats.Key(gem is "Fireball" or "IceNova" ? "spell_critical_strike_chance" : "main_hand_critical_strike_chance")!.Value,
            chance), selected.Stats!);
        var hit = f.Hit;
        await f.Use();
        await f.HitLands();
        Assert.Equal(1, random.Calls);
        Assert.InRange((int)(f.Target.MaxLife - f.Target.Life), (int)Math.Floor(hit.MinTotal), (int)Math.Ceiling(hit.MaxTotal));
    }

    [Theory]
    [InlineData("Rusted Sword")]
    [InlineData("Crude Bow")]
    public async Task Real_unique_less_chance_mod_changes_default_attack_contact_and_removal_restores_it(string weapon)
    {
        var random = new Draws(800); // Old 10% crit, unique LESS makes it 6%.
        using var f = new Contact(random, weaponName: weapon, distance: weapon == "Crude Bow" ? 60 : 2);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        var mod = Program.Items.Mods.ById("LessCriticalStrikeChanceAmulet17")!;
        var stat = Assert.Single(mod.Stats);
        Assert.Equal("unique_critical_strike_chance_+%_final", stat.Id);
        Assert.Equal(-40, stat.Min);
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        var carrier = inventory.Add(5, 0, 0, ItemCatalog.Create(Program.Items.Find("Coral Amulet")!) with
            { Mods = [new(ModSection.Explicit, mod.Hash, [-40])] });
        f.Refresh();
        Assert.Equal(600, f.Conn.Pools.AttackCriticalStrikeChance);
        f.Set("unique_critical_strike_chance_+%_final", 0);
        f.Refresh();
        Assert.Equal(1000, f.Conn.Pools.AttackCriticalStrikeChance);
        Assert.True(f.Conn.PlayerStats.Remove(Program.Stats.Key("unique_critical_strike_chance_+%_final")!.Value));
        f.Refresh();
        Assert.Equal(600, f.Conn.Pools.AttackCriticalStrikeChance);
        var selected = Assert.Single(f.CurrentSkills(), s => s.Id == f.Skill);
        Assert.Contains((Program.Stats.Key("main_hand_critical_strike_chance")!.Value, 600), selected.Stats!);
        await f.Use();
        await f.HitLands();
        Assert.Equal(1, random.Calls);
        Assert.Equal(1000u, f.Target.MaxLife - f.Target.Life);
        Assert.NotNull(inventory.Remove(5, carrier.Id));
        f.Refresh();
        Assert.Equal(1000, f.Conn.Pools.AttackCriticalStrikeChance);
    }
}
