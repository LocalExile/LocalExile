using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// Overheat Support (GrantedEffect 12314): "Supported Skills consume all Fortification on use, dealing (6-8)% more
// Damage with Ailments per 3 Fortification consumed". Fortification is consumed once per use, before the hit lands,
// and the more applies to that use's ailments (including a melee skill's projectiles). Stored per skill.
public sealed class OverheatSupportTests
{
    [Fact]
    public void OverheatConsumeLocked_stores_the_per_skill_more_from_consumed_fortification()
    {
        var player = new Player(new(1, "overheat", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "OverheatChar" },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new()
        };
        const ushort skillId = 0x5678;
        var skill = new Dictionary<uint, int>
        {
            [Stat.SupportOverheatAilmentDamagePlusPctFinalPer3FortificationConsumed] = 6,
        };

        // 6 stacks consumed -> 2 groups of three -> 1 + 6*2/100 = 1.12.
        var session = Game.FortificationOf(player);
        Assert.True(session.State.GrantExplicitStacks(0, 6, 0));
        Assert.Equal(6, session.Read(0).Current);
        Game.OverheatConsumeLocked(player, skillId, name => skill.GetValueOrDefault(name), 0);
        Assert.Equal(1.12, player.SupportGems.OverheatMore[skillId], 6);
        Assert.Equal(0, session.Read(0).Current); // consumed

        // A second use with no Fortification replaces the stored value with nothing.
        Game.OverheatConsumeLocked(player, skillId, name => skill.GetValueOrDefault(name), 1);
        Assert.False(player.SupportGems.OverheatMore.ContainsKey(skillId));
        Assert.Equal(1, Game.OverheatAilmentMore(player, skillId));
    }

    [Fact]
    public void OverheatConsumeLocked_does_nothing_for_a_skill_without_the_support()
    {
        var player = new Player(new(1, "overheat-plain", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "OverheatPlain" },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new()
        };
        const ushort skillId = 0x9abc;
        var session = Game.FortificationOf(player);
        Assert.True(session.State.GrantExplicitStacks(0, 9, 0));
        var skill = new Dictionary<uint, int>(); // no per-3 stat
        Game.OverheatConsumeLocked(player, skillId, name => skill.GetValueOrDefault(name), 0);
        Assert.False(player.SupportGems.OverheatMore.ContainsKey(skillId));
        Assert.Equal(1, Game.OverheatAilmentMore(player, skillId));
        // Fortification is still consumed even without the support (the consume is per use of a supported skill, but
        // OverheatConsumeLocked returns early only for the storage; here perThree<=0 so it removes and returns without
        // consuming). Verify the stacks remain.
        Assert.Equal(9, session.Read(0).Current);
    }

    [Fact]
    public void Overheat_does_not_store_a_bonus_for_fewer_than_three_stacks()
    {
        var player = new Player(new(1, "overheat-small", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "OverheatSmall" },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new()
        };
        const ushort skillId = 0xdef0;
        var session = Game.FortificationOf(player);
        Assert.True(session.State.GrantExplicitStacks(0, 2, 0));
        var skill = new Dictionary<uint, int>
        {
            [Stat.SupportOverheatAilmentDamagePlusPctFinalPer3FortificationConsumed] = 6,
        };
        Game.OverheatConsumeLocked(player, skillId, name => skill.GetValueOrDefault(name), 0);
        Assert.False(player.SupportGems.OverheatMore.ContainsKey(skillId));
        Assert.Equal(1, Game.OverheatAilmentMore(player, skillId));
    }

    [Fact]
    public void WithOverheat_scales_an_ignite_settings_block_by_the_stored_more()
    {
        var player = new Player(new(1, "overheat-scale", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "OverheatScale" },
            PlayerLife = 1000,
            PlayerMana = 1000,
            Instance = new()
        };
        const ushort skillId = 0x1111;
        player.SupportGems.OverheatMore[skillId] = 1.12;
        var settings = new MonsterIgnites.Settings(50, 10, 20, 15, 25, 4);
        var scaled = Game.WithOverheat(player, skillId, settings);
        Assert.Equal(10 * 1.12, scaled.Min, 6);
        Assert.Equal(20 * 1.12, scaled.Max, 6);
        Assert.Equal(15 * 1.12, scaled.CriticalMin, 6);
        Assert.Equal(25 * 1.12, scaled.CriticalMax, 6);
        Assert.Equal(4, scaled.Seconds, 6);
        Assert.Equal(50, scaled.Chance);
        // No stored bonus: settings returned unchanged.
        player.SupportGems.OverheatMore.Remove(skillId);
        Assert.Equal(settings, Game.WithOverheat(player, skillId, settings));
    }
}
