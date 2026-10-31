using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ChargeGainsTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] stats) =>
        name => stats.Where(s => s.Name == name).Sum(s => s.Value);

    static readonly WeaponProfile Unarmed = new(false, false, false, false, false, Unarmed: true, Melee: true);

    static ChargeEvent.Hit Hit(bool critical = false, bool spell = false, MonsterRarity rarity = MonsterRarity.Normal,
        string? skill = null, Func<string, int>? skillStats = null, bool killed = false, bool? melee = null) =>
        new(critical, spell, melee, rarity, skill, skillStats, killed);

    static List<(ChargeType, int)> Roll(ChargeEvent e, Func<string, int> get, Random rng, WeaponProfile? weapon = null) =>
        [.. ChargeGains.Roll(e, get, weapon ?? Unarmed, rng).Select(g => (g.Type, g.Count))];

    [Fact]
    public void Every_gain_stat_exists_in_Stats_dat()
    {
        var stats = new StatCatalog(ChargePanelStatsTestsData.Folder);
        foreach (var rule in ChargeGains.Rules) Assert.True(stats.Key(rule.Stat) is not null, rule.Stat);
        foreach (var p in ChargeGains.Periodics) Assert.True(stats.Key(p.Stat) is not null, p.Stat);
        foreach (var type in Charges.Types) Assert.True(stats.Key(ChargeGains.ToMaximumStat(type)) is not null, ChargeGains.ToMaximumStat(type));
    }

    [Fact]
    public void A_kill_chance_rolls_once_below_the_chance()
    {
        var get = Stats(("add_frenzy_charge_on_kill_%_chance", 30));
        Assert.Equal([(ChargeType.Frenzy, 1)], Roll(new ChargeEvent.Kill(MonsterRarity.Normal), get, new ScriptedRandom(29)));
        Assert.Empty(Roll(new ChargeEvent.Kill(MonsterRarity.Normal), get, new ScriptedRandom(30)));
        // A hit is not a kill.
        Assert.Empty(Roll(Hit(), get, new ScriptedRandom()));
    }

    [Fact]
    public void A_full_chance_or_a_flag_gains_without_a_roll()
    {
        var rng = new ScriptedRandom();
        var get = Stats(("endurance_charge_on_kill_%", 100), ("add_power_charge_on_kill_%_chance", 250), ("add_frenzy_charge_on_kill", 1));
        Assert.Equal([(ChargeType.Endurance, 1), (ChargeType.Frenzy, 1), (ChargeType.Power, 1)],
            Roll(new ChargeEvent.Kill(MonsterRarity.Normal), get, rng).Order());
        Assert.Equal(0, rng.Asked);
    }

    [Fact]
    public void Each_source_stat_is_its_own_roll()
    {
        // 40% power on kill (roll 39: gained) and 40% on kill for any of the three (roll 50: not gained).
        var get = Stats(("add_power_charge_on_kill_%_chance", 40), ("power_frenzy_or_endurance_charge_on_kill_%", 40));
        Assert.Equal([(ChargeType.Power, 1)], Roll(new ChargeEvent.Kill(MonsterRarity.Normal), get, new ScriptedRandom(39, 50)));
    }

    [Fact]
    public void A_random_charge_is_drawn_from_the_three_in_order()
    {
        var get = Stats(("power_frenzy_or_endurance_charge_on_kill_%", 100));
        Assert.Equal([(ChargeType.Endurance, 1)], Roll(new ChargeEvent.Kill(MonsterRarity.Normal), get, new ScriptedRandom(0)));
        Assert.Equal([(ChargeType.Frenzy, 1)], Roll(new ChargeEvent.Kill(MonsterRarity.Normal), get, new ScriptedRandom(1)));
        Assert.Equal([(ChargeType.Power, 1)], Roll(new ChargeEvent.Kill(MonsterRarity.Normal), get, new ScriptedRandom(2)));
    }

    [Fact]
    public void Critical_and_non_critical_gains_follow_the_hit()
    {
        var get = Stats(("add_power_charge_on_critical_strike_%", 100), ("gain_power_charge_on_non_critical_strike_%", 100),
            ("chance_to_gain_endurance_charge_on_crit_%", 100));
        Assert.Equal([(ChargeType.Endurance, 1), (ChargeType.Power, 1)], Roll(Hit(critical: true), get, new ScriptedRandom()).Order());
        Assert.Equal([(ChargeType.Power, 1)], Roll(Hit(critical: false), get, new ScriptedRandom()));
    }

    [Fact]
    public void Weapon_conditions_decide_melee_wand_claw_shield_and_dual_wield_gains()
    {
        var melee = Stats(("chance_to_gain_endurance_charge_on_melee_crit_%", 100));
        var bow = new WeaponProfile(false, false, false, Bow: true, false, TwoHand: true);
        Assert.Single(Roll(Hit(critical: true), melee, new ScriptedRandom()));
        Assert.Empty(Roll(Hit(critical: true), melee, new ScriptedRandom(), bow));
        Assert.Empty(Roll(Hit(critical: true, spell: true), melee, new ScriptedRandom()));
        var wand = new WeaponProfile(false, false, false, false, false, OneHand: true, Wand: true);
        var wandCrit = Stats(("gain_power_charge_on_critical_strike_with_wands_%", 100));
        Assert.Single(Roll(Hit(critical: true), wandCrit, new ScriptedRandom(), wand));
        Assert.Empty(Roll(Hit(critical: true), wandCrit, new ScriptedRandom()));
        var claw = new WeaponProfile(false, false, false, false, false, OneHand: true, Melee: true, Claw: true);
        var nonCrit = Stats(("power_charge_on_non_critical_strike_%_chance_with_claws_daggers", 100));
        Assert.Single(Roll(Hit(), nonCrit, new ScriptedRandom(), claw));
        Assert.Empty(Roll(Hit(), nonCrit, new ScriptedRandom()));
        var shield = new WeaponProfile(Shield: true, false, false, false, false);
        var dual = new WeaponProfile(false, DualWield: true, false, false, false);
        var kills = Stats(("endurance_charge_on_kill_percent_chance_while_holding_shield", 100), ("add_frenzy_charge_on_kill_%_chance_while_dual_wielding", 100));
        Assert.Equal([(ChargeType.Endurance, 1)], Roll(new ChargeEvent.Kill(MonsterRarity.Normal), kills, new ScriptedRandom(), shield));
        Assert.Equal([(ChargeType.Frenzy, 1)], Roll(new ChargeEvent.Kill(MonsterRarity.Normal), kills, new ScriptedRandom(), dual));
    }

    [Fact]
    public void Rare_or_unique_hits_and_the_skills_own_chance_add_to_the_characters()
    {
        var get = Stats(("gain_frenzy_charge_on_hitting_rare_or_unique_enemy_%", 100), ("add_frenzy_charge_on_skill_hit_%", 10));
        // Flicker Strike's own 15% plus the character's 10%: one roll at 25%.
        var flicker = Stats(("add_frenzy_charge_on_skill_hit_%", 15));
        Assert.Equal([(ChargeType.Frenzy, 1)], Roll(Hit(skillStats: flicker), get, new ScriptedRandom(24)));
        Assert.Empty(Roll(Hit(skillStats: flicker), get, new ScriptedRandom(25)));
        Assert.Equal([(ChargeType.Frenzy, 1), (ChargeType.Frenzy, 1)], Roll(Hit(rarity: MonsterRarity.Rare, skillStats: flicker), get, new ScriptedRandom(0)));
        // Power Siphon's 20% on a rare or unique enemy, not on a magic one.
        var siphon = Stats(("chance_to_gain_power_charge_on_rare_or_unique_enemy_hit_%", 20));
        Assert.Equal([(ChargeType.Power, 1)], Roll(Hit(rarity: MonsterRarity.Unique, skillStats: siphon), Stats(), new ScriptedRandom(19)));
        Assert.Empty(Roll(Hit(rarity: MonsterRarity.Magic, skillStats: siphon), Stats(), new ScriptedRandom()));
    }

    [Fact]
    public void The_Frenzy_gem_gains_on_hit_and_Power_Siphon_on_a_kill()
    {
        Assert.Equal([(ChargeType.Frenzy, 1)], Roll(Hit(skill: "Frenzy"), Stats(), new ScriptedRandom()));
        Assert.Empty(Roll(Hit(skill: "FrenzyAltX"), Stats(), new ScriptedRandom()));
        // Once per use: the other monsters an area Frenzy hits give nothing.
        Assert.Empty(Roll(new ChargeEvent.Hit(false, false, null, MonsterRarity.Normal, "Frenzy", Primary: false), Stats(), new ScriptedRandom()));
        Assert.Equal([(ChargeType.Power, 1)], Roll(Hit(skill: "PowerSiphon", killed: true), Stats(), new ScriptedRandom()));
        Assert.Empty(Roll(Hit(skill: "PowerSiphon"), Stats(), new ScriptedRandom()));
    }

    [Fact]
    public void Being_hit_and_blocking_have_their_own_gains()
    {
        var get = Stats(("chance_to_gain_endurance_charge_when_hit_%", 100), ("chance_to_gain_frenzy_charge_on_block_attack_%", 100),
            ("power_charge_on_spell_block_%_chance", 100), ("chance_to_gain_endurance_charge_on_block_%", 100));
        Assert.Equal([(ChargeType.Endurance, 1)], Roll(new ChargeEvent.WhenHit(), get, new ScriptedRandom()));
        Assert.Equal([(ChargeType.Endurance, 1), (ChargeType.Frenzy, 1)], Roll(new ChargeEvent.Block(Spell: false), get, new ScriptedRandom()).Order());
        Assert.Equal([(ChargeType.Endurance, 1), (ChargeType.Power, 1)], Roll(new ChargeEvent.Block(Spell: true), get, new ScriptedRandom()).Order());
        // A block is not a hit taken.
        Assert.DoesNotContain((ChargeType.Endurance, 1), Roll(new ChargeEvent.Block(false), Stats(("chance_to_gain_endurance_charge_when_hit_%", 100)), new ScriptedRandom()));
    }

    [Fact]
    public void Periodic_gains_follow_game_time_and_their_conditions()
    {
        var timers = new ChargeGains.Timers();
        var get = Stats(("gain_X_frenzy_charges_per_second", 1), ("body_armour_implicit_gain_power_charge_every_x_ms", 2500),
            ("gain_endurance_charge_per_second_if_have_been_hit_recently", 2));
        var rng = new ScriptedRandom();
        Assert.Empty(timers.Advance(0.9, get, hitRecently: false, moving: false, rng));
        Assert.Equal([new ChargeGains.Gain(ChargeType.Frenzy, 1, "gain_X_frenzy_charges_per_second")], timers.Advance(0.1, get, false, false, rng));
        // Two more seconds (1.5 + 0.5): two more Frenzy Charges, and the Power Charge of the 2.5-second timer.
        var gains = timers.Advance(1.5, get, false, false, rng).Concat(timers.Advance(0.5, get, false, false, rng)).ToList();
        Assert.Equal(2, gains.Count(g => g.Type == ChargeType.Frenzy));
        Assert.Single(gains, g => g.Type == ChargeType.Power);
        // Hit recently: 2 endurance charges each second, and the timer starts again when the condition stops holding.
        Assert.DoesNotContain(timers.Advance(0.6, get, hitRecently: true, false, rng), g => g.Type == ChargeType.Endurance);
        Assert.DoesNotContain(timers.Advance(0.6, get, hitRecently: false, false, rng), g => g.Type == ChargeType.Endurance);
        Assert.DoesNotContain(timers.Advance(0.6, get, hitRecently: true, false, rng), g => g.Type == ChargeType.Endurance);
        Assert.Equal([new ChargeGains.Gain(ChargeType.Endurance, 2, "gain_endurance_charge_per_second_if_have_been_hit_recently")],
            timers.Advance(0.4, get, hitRecently: true, false, rng).Where(g => g.Type == ChargeType.Endurance));
        Assert.Equal(0, rng.Asked);
    }

    [Fact]
    public void A_negative_charge_per_second_stat_loses_one_charge()
    {
        // Replica Fragility, Replica Pacifism and Replica Powerlessness store -1 in the stat: the tooltip has no
        // number ("Lose an Endurance Charge each second"), so the count is negative and takes a charge away.
        var timers = new ChargeGains.Timers();
        var get = Stats(("gain_X_endurance_charges_per_second", -1), ("gain_X_power_charges_per_second", -1));
        var gains = timers.Advance(1.0, get, hitRecently: false, moving: false, new ScriptedRandom());
        Assert.Equal([new ChargeGains.Gain(ChargeType.Endurance, -1, "gain_X_endurance_charges_per_second"),
            new ChargeGains.Gain(ChargeType.Power, -1, "gain_X_power_charges_per_second")], gains);
        Assert.Equal(2, timers.Advance(1.0, get, false, false, new ScriptedRandom()).Count);
    }

    [Fact]
    public void Stationary_gains_wait_while_moving()
    {
        var timers = new ChargeGains.Timers();
        var get = Stats(("number_of_endurance_charges_to_gain_every_4_seconds_while_stationary", 1));
        var rng = new ScriptedRandom();
        Assert.Empty(timers.Advance(3.9, get, false, moving: false, rng));
        Assert.Empty(timers.Advance(0.2, get, false, moving: true, rng));
        Assert.Empty(timers.Advance(3.9, get, false, moving: false, rng));
        Assert.Single(timers.Advance(0.1, get, false, moving: false, rng));
    }
}

/// <summary>The data folder the Gateway tests read (data/tables).</summary>
static class ChargePanelStatsTestsData
{
    public static string Folder
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
            throw new DirectoryNotFoundException("data/tables");
        }
    }
}
