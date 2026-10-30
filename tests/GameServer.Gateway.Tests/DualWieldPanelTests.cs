using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class DualWieldPanelTests
{
    [Fact]
    public void Averages_each_hands_own_critical_weighted_hit_and_rate()
    {
        var main = DamagePacket.Of(DamageType.Physical, 100, 200);   // 150 average, 1 per second
        var off = DamagePacket.Of(DamageType.Physical, 10, 30);      // 20 average, 2 per second
        var (perUse, perSecond, aps) = DualWieldPanel.Average(main, 1, off, .5, 1);
        Assert.Equal(8500, perUse);
        Assert.Equal((15000 + 4000) / 2, perSecond);
        Assert.Equal(150, aps);
        var weighted = DualWieldPanel.Average(main, 1, off, .5, 2);
        Assert.Equal(17000, weighted.PerUse);
        Assert.Equal(19000, weighted.PerSecond);
        Assert.Equal((0, 0, 0), DualWieldPanel.Average(DamagePacket.Empty, 1, DamagePacket.Empty, 1, 1) with { HundredTimesAttacksPerSecond = 0 });
    }

    [Fact]
    public void Off_hand_seconds_use_its_own_duration_at_the_off_hand_speed()
    {
        var off = new SkillCatalog.Unarmed(1000, 1, 1, 8);
        Assert.Equal(1, DualWieldPanel.OffHandSeconds(off, 0), 9);
        Assert.Equal(.5, DualWieldPanel.OffHandSeconds(off, 100), 9);
        Assert.Equal(Program.TickSeconds, DualWieldPanel.OffHandSeconds(off, 100_000), 9);
    }
}

public partial class ConditionalCriticalGatewayTests
{
    [Fact]
    public void Dual_wielded_default_attack_averages_both_hands_in_the_dps_rows()
    {
        using var f = new Contact(new Draws(), null, "Rusted Sword");
        f.Conn.PlayerLife = 51;
        var single = ((IEnumerable<(uint, int)>)Method("FinalPlayerStats").Invoke(null, [f.Conn])!).ToDictionary(s => s.Item1, s => s.Item2);
        EquipOffHand(f, "Rusted Sword");
        f.Refresh();
        var twins = ((IEnumerable<(uint, int)>)Method("FinalPlayerStats").Invoke(null, [f.Conn])!).ToDictionary(s => s.Item1, s => s.Item2);
        // Two identical swords: the average is each hand's own value, i.e. the main hand's display of the pair.
        var main = (SkillCatalog.Unarmed)Method("WeaponFor").Invoke(null, [f.Conn])!;
        var stats = Program.CharacterPlayerStats(f.Conn);
        double factor = CriticalStrikePolicy.DamageFactor(f.Conn.Pools.AttackCriticalStrikeChance, f.Multiplier, 0, 100);
        var mainOnly = SpellDamage.Display((Program.AttackHitConverted(main, stats, true, null).MinTotal,
            Program.AttackHitConverted(main, stats, true, null).MaxTotal), 1 / main.AttackSeconds(stats), factor);
        Assert.Equal(mainOnly.PerUse, twins[StatKeys.DisplayHundredTimesDamagePerSkillUse]);
        Assert.Equal(mainOnly.PerSecond, twins[StatKeys.HundredTimesDamagePerSecond]);
        Assert.Equal((int)Math.Round(100 / main.AttackSeconds(stats)), twins[StatKeys.HundredTimesAttacksPerSecond]);
        // A different off-hand dagger: the rows average the two hands' own values.
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        foreach (var worn in inventory.Items(PlayerInventories.Offhand1).ToList()) inventory.Remove(PlayerInventories.Offhand1, worn.Id);
        EquipOffHand(f, "Glass Shank");
        f.Refresh();
        var mixed = ((IEnumerable<(uint, int)>)Method("FinalPlayerStats").Invoke(null, [f.Conn])!).ToDictionary(s => s.Item1, s => s.Item2);
        var off = (SkillCatalog.Unarmed)Method("OffhandWeaponFor").Invoke(null, [f.Conn])!;
        stats = Program.CharacterPlayerStats(f.Conn);
        factor = CriticalStrikePolicy.DamageFactor(f.Conn.Pools.AttackCriticalStrikeChance, f.Multiplier, 0, 100);
        var mainHit = Program.AttackHitConverted(main, stats, true, null);
        var offHit = Program.AttackHitConverted(off, stats, true, null);
        var expected = DualWieldPanel.Average(mainHit, main.AttackSeconds(stats), offHit,
            DualWieldPanel.OffHandSeconds(off, mixed[StatKeys.OffHandAttackSpeedPct]), factor);
        Assert.Equal(expected.PerUse, mixed[StatKeys.DisplayHundredTimesDamagePerSkillUse]);
        Assert.Equal(expected.PerSecond, mixed[StatKeys.HundredTimesDamagePerSecond]);
        Assert.Equal(expected.HundredTimesAttacksPerSecond, mixed[StatKeys.HundredTimesAttacksPerSecond]);
        Assert.NotEqual(single[StatKeys.HundredTimesDamagePerSecond], mixed[StatKeys.HundredTimesDamagePerSecond]);
    }
}
