namespace GameServer.Gateway.Tests;

/// <summary>
/// The overcapped-resistance modifiers (ResistanceOvercap): while one of the flag stats is set, the uncapped
/// resistance above its maximum is a generic increased modifier — +1% evasion per 1% of cold overcap, +1% critical
/// strike chance per 1% of lightning overcap (PoB 16de4b8: CalcDefence.lua's EvasionRatingIncreasedByOvercappedColdRes,
/// CalcOffence.lua 745-748's CritChanceIncreasedByOvercappedLightningRes). The pools derive the increases once; the
/// evade calculations, the panel and the spell consumers read the same stored values.
/// </summary>
public class ResistanceOvercapTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static StatCatalog Catalog() => new(Data());

    static Dictionary<uint, int> Sources(StatCatalog stats, params (string Name, int Value)[] entries)
    {
        var dict = new Dictionary<uint, int>();
        foreach (var (name, value) in entries) dict[stats.Key(name)!.Value] = value;
        return dict;
    }

    // 120% cold and lightning resistance, no maximum raises, no penalty: 45% overcap each.
    static CharacterStats.Bonus OvercappedBonus = new(ColdResistance: 120, LightningResistance: 120);

    [Fact]
    public void The_flags_derive_their_increases_from_the_overcap_only()
    {
        var both = ResistanceOvercap.Increases(120, 75, true, 120, 75, true);
        Assert.Equal(45, both.EvasionPct);
        Assert.Equal(45, both.CriticalStrikeChancePct);
        // Each flag drives its own value only; below or at the maximum nothing is overcapped.
        Assert.Equal(new ResistanceOvercap.Derived(45, 0), ResistanceOvercap.Increases(120, 75, true, 75, 75, true));
        Assert.Equal(new ResistanceOvercap.Derived(0, 45), ResistanceOvercap.Increases(75, 75, true, 120, 75, true));
        Assert.Equal(ResistanceOvercap.Derived.None, ResistanceOvercap.Increases(120, 75, false, 120, 75, false));
    }

    [Fact]
    public void The_overcap_adds_per_point_and_the_maximum_keeps_its_90_cap()
    {
        Assert.Equal(1, ResistanceOvercap.Increases(76, 75, true, 76, 75, true).EvasionPct);
        Assert.Equal(60, ResistanceOvercap.Increases(150, 90, true, 0, 75, false).EvasionPct);
        // A raised maximum is itself capped at 90: 150 over a 120 maximum reads the part above 90 only.
        Assert.Equal(60, ResistanceOvercap.Increases(150, 120, true, 0, 75, false).EvasionPct);
    }

    [Fact]
    public void An_explicit_dev_value_wins_over_the_sources_including_zero()
    {
        var stats = Catalog();
        var sources = Sources(stats, (ResistanceOvercap.ColdEvasionFlag, 1));
        Assert.True(ResistanceOvercap.Enabled(stats, sources, new Dictionary<uint, int>(), ResistanceOvercap.ColdEvasionFlag));
        // /set_stat flag 0 turns the consumer off again (an explicit value replaces, as for every stat).
        Assert.False(ResistanceOvercap.Enabled(stats, sources,
            Sources(stats, (ResistanceOvercap.ColdEvasionFlag, 0)), ResistanceOvercap.ColdEvasionFlag));
        // A flag with no source is on when the dev sets it.
        Assert.True(ResistanceOvercap.Enabled(stats, new Dictionary<uint, int>(),
            Sources(stats, (ResistanceOvercap.LightningCritFlag, 1)), ResistanceOvercap.LightningCritFlag));
        Assert.False(ResistanceOvercap.Enabled(stats, new Dictionary<uint, int>(), new Dictionary<uint, int>(), ResistanceOvercap.LightningCritFlag));
    }

    [Fact]
    public void Applied_keeps_the_resistances_when_no_flag_is_set()
    {
        var stats = Catalog();
        var applied = ResistanceOvercap.Applied(OvercappedBonus, new Dictionary<uint, int>(),
            new Dictionary<uint, int>(), penalty: 0, stats);

        Assert.Equal(120, applied.ColdResistance);
        Assert.Equal(0, applied.ColdMaxResistance); // The bonus stores the raise above the 75 base: none here.
        Assert.Equal(0, applied.ColdOvercapEvasionPct);
        Assert.Equal(0, applied.LightningOvercapCritPct);
    }

    [Fact]
    public void Applied_derives_the_increases_with_the_campaign_penalty()
    {
        var stats = Catalog();
        var sources = Sources(stats, (ResistanceOvercap.ColdEvasionFlag, 1), (ResistanceOvercap.LightningCritFlag, 1));
        var full = ResistanceOvercap.Applied(OvercappedBonus, sources, new Dictionary<uint, int>(), penalty: 0, stats);
        Assert.Equal(45, full.ColdOvercapEvasionPct);
        Assert.Equal(45, full.LightningOvercapCritPct);
        // Act 5 Kitava: 120 - 30 = 90 uncapped, 15 above the 75 maximum. Both penalties: 60, below the maximum.
        var act5 = ResistanceOvercap.Applied(OvercappedBonus, sources, new Dictionary<uint, int>(), penalty: -30, stats);
        Assert.Equal(15, act5.ColdOvercapEvasionPct);
        Assert.Equal(15, act5.LightningOvercapCritPct);
        var act10 = ResistanceOvercap.Applied(OvercappedBonus, sources, new Dictionary<uint, int>(), penalty: -60, stats);
        Assert.Equal(0, act10.ColdOvercapEvasionPct);
        Assert.Equal(0, act10.LightningOvercapCritPct);
        // The resistances themselves keep the raw values (the pools never lose the uncapped numbers).
        Assert.Equal(120, act10.ColdResistance);
        Assert.Equal(120, act10.LightningResistance);
    }

    [Fact]
    public void Applied_counts_aura_and_gear_resistance_already_folded_into_the_bonus()
    {
        // PoolsFor passes the aggregated bonus (passives, items and auras): 80 from sources, 40 from an aura.
        var stats = Catalog();
        var sources = Sources(stats, ("base_cold_damage_resistance_%", 80), (ResistanceOvercap.ColdEvasionFlag, 1));
        var applied = ResistanceOvercap.Applied(new CharacterStats.Bonus(ColdResistance: 120), sources,
            new Dictionary<uint, int>(), penalty: 0, stats);

        Assert.Equal(120, applied.ColdResistance);
        Assert.Equal(45, applied.ColdOvercapEvasionPct);
    }

    [Fact]
    public void Applied_replaces_only_the_stat_the_dev_set()
    {
        var stats = Catalog();
        var sources = Sources(stats, ("base_cold_damage_resistance_%", 80), (ResistanceOvercap.ColdAndLightningResistance, 40),
            (ResistanceOvercap.ColdEvasionFlag, 1));
        // Aggregated 120 (80 + the pair's 40): replacing the pair input with 60 gives 140, overcap 65.
        var applied = ResistanceOvercap.Applied(new CharacterStats.Bonus(ColdResistance: 120), sources,
            Sources(stats, (ResistanceOvercap.ColdAndLightningResistance, 60)), penalty: 0, stats);
        Assert.Equal(140, applied.ColdResistance);
        Assert.Equal(65, applied.ColdOvercapEvasionPct);

        // An explicit 0 replaces that input too: 120 - 80 = 40, below the maximum.
        var zeroed = ResistanceOvercap.Applied(new CharacterStats.Bonus(ColdResistance: 120), sources,
            Sources(stats, ("base_cold_damage_resistance_%", 0)), penalty: 0, stats);
        Assert.Equal(40, zeroed.ColdResistance);
        Assert.Equal(0, zeroed.ColdOvercapEvasionPct);
    }

    [Fact]
    public void Applied_follows_a_dev_maximum_raise_up_to_the_absolute_cap()
    {
        var stats = Catalog();
        var sources = Sources(stats, (ResistanceOvercap.ColdEvasionFlag, 1));
        // +25% maximum cold resistance: 120 over a 90 maximum. The bonus stores the raise above the 75 base.
        var raised = ResistanceOvercap.Applied(new CharacterStats.Bonus(ColdResistance: 120), sources,
            Sources(stats, ("base_maximum_cold_damage_resistance_%", 25)), penalty: 0, stats);
        Assert.Equal(15, raised.ColdMaxResistance);
        Assert.Equal(30, raised.ColdOvercapEvasionPct);
    }

    [Fact]
    public void Sea_Legs_grants_both_flags_and_the_pair_resistance()
    {
        var tree = new PassiveTree(Data());
        var stats = Catalog();
        var node = tree.Find(61696);

        Assert.Equal("Sea Legs", node?.Name);
        var granted = tree.Stats([61696]);
        Assert.Equal(1, granted[stats.Key(ResistanceOvercap.ColdEvasionFlag)!.Value]);
        Assert.Equal(1, granted[stats.Key(ResistanceOvercap.LightningCritFlag)!.Value]);
        Assert.Equal(40, granted[stats.Key(ResistanceOvercap.ColdAndLightningResistance)!.Value]);
    }

    [Fact]
    public void The_spell_consumer_reads_the_derived_increase_once()
    {
        var stats = Catalog();
        uint critKey = stats.Key("critical_strike_chance_+%")!.Value;
        int Get(string name) => stats.Key(name) is uint key && key == critKey ? 45 : 0;

        // 5% base with the derived 45% increased: 725 permyriad, not 5% × 1.90 (which would count it twice).
        Assert.Equal(725, SpellCriticalStrike.Chance(500, Get));
        // Without the derived value the spells see no flag bonus (the attack and spell paths stay isolated).
        Assert.Equal(500, SpellCriticalStrike.Chance(500, _ => 0));
    }

    [Fact]
    public void Pools_fold_the_derived_increases_into_evasion_and_both_crit_chances()
    {
        // Witch level 1: 15 base evasion, 14 dexterity (2%); 120% cold and lightning from the dev inputs, both flags on.
        var stats = Catalog();
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            ResistanceOvercap.Applied(new CharacterStats.Bonus(ColdResistance: 120, LightningResistance: 120),
                Sources(stats, (ResistanceOvercap.ColdEvasionFlag, 1), (ResistanceOvercap.LightningCritFlag, 1)),
                new Dictionary<uint, int>(), penalty: 0, stats));

        Assert.Equal(45, pools.ColdOvercapEvasionPct);
        Assert.Equal(47, pools.EvasionPct); // 2% dexterity + the derived 45%.
        Assert.Equal(22, pools.Evasion); // 15 × 1.47.
        Assert.Equal(45, pools.CriticalStrikeChancePct);
        Assert.Equal(45, pools.SpellCriticalStrikeChancePct);
        Assert.Equal(725, pools.SpellCriticalStrikeChance); // 5% base, 45% increased (permyriad).
    }

    [Fact]
    public void The_derived_evasion_is_additive_with_the_other_increases_not_a_final_multiplier()
    {
        var stats = Catalog();
        var bonus = ResistanceOvercap.Applied(new CharacterStats.Bonus(Evasion: 100, EvasionPct: 50, ColdResistance: 120),
            Sources(stats, (ResistanceOvercap.ColdEvasionFlag, 1)), new Dictionary<uint, int>(), penalty: 0, stats);
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, bonus);

        // 115 flat × (1 + 2% dexterity + 50% increased + 45% overcap) = 226.55; a final multiplier would give 15 × 1.47 × 1.50.
        Assert.Equal(97, pools.EvasionPct);
        Assert.Equal(226, pools.Evasion);
    }

    [Fact]
    public void Iron_Reflexes_converts_the_overcapped_evasion_into_armour_once()
    {
        var stats = Catalog();
        var bonus = ResistanceOvercap.Applied(new CharacterStats.Bonus(ColdResistance: 120),
            Sources(stats, (ResistanceOvercap.ColdEvasionFlag, 1)), new Dictionary<uint, int>(), penalty: 0, stats);
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, bonus with { IronReflexes = true });

        // The converted 15 base evasion keeps the overcapped increase once: 15 × 1.45 = 21.75 armour, no evasion left.
        Assert.Equal(0, pools.Evasion);
        Assert.Equal(45, pools.EvasionPct);
        Assert.Equal(21, pools.Armour);
    }

    [Fact]
    public void Without_the_flags_the_overcap_does_not_reach_the_pools()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(ColdResistance: 120, LightningResistance: 120));

        Assert.Equal(120, pools.ColdResistance); // The uncapped value is kept, never replaced by the capped one.
        Assert.Equal(2, pools.EvasionPct);
        Assert.Equal(15, pools.Evasion);
        Assert.Equal(0, pools.CriticalStrikeChancePct);
        Assert.Equal(0, pools.SpellCriticalStrikeChancePct);
        Assert.Equal(500, pools.SpellCriticalStrikeChance);
    }
}

/// <summary>
/// The overcapped-resistance modifiers through the real Gateway paths: the dev inputs (/set_stat) on a fresh
/// connection's <see cref="ClientState.PlayerStats"/> rebuild <see cref="Program.PoolsFor"/>'s pools at once, and
/// <see cref="Program.CharacterPlayerStats"/> hands the derived critical increase to the spell consumers. A bare
/// connection is an unarmed Witch without armour: the numbers below are its exact pools.
/// </summary>
public class OvercapPoolIntegrationTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static ClientState Conn(StatCatalog stats, params (string Name, int Value)[] devStats)
    {
        TestSaves.Ensure();
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);
        lock (conn.Monsters)
        {
            foreach (var (name, value) in devStats) conn.PlayerStats[stats.Key(name)!.Value] = value;
            conn.Pools = Program.PoolsFor(conn, 1);
        }
        return conn;
    }

    static readonly (string, int)[] BothFlagsAndResistances =
    [
        (ResistanceOvercap.ColdEvasionFlag, 1), (ResistanceOvercap.LightningCritFlag, 1),
        ("base_cold_damage_resistance_%", 120), ("base_lightning_damage_resistance_%", 120),
    ];

    [Fact]
    public void Dev_flag_and_resistance_inputs_move_the_pools_at_once()
    {
        var stats = new StatCatalog(Data());
        var conn = Conn(stats, BothFlagsAndResistances);

        Assert.Equal(45, conn.Pools.ColdOvercapEvasionPct);
        Assert.Equal(45, conn.Pools.LightningOvercapCritPct);
        Assert.Equal(47, conn.Pools.EvasionPct); // The Witch's 2% dexterity + the derived 45%.
        Assert.Equal(22, conn.Pools.Evasion); // 15 × 1.47; the monster attacks and the panel read this pools value.
        Assert.Equal(45, conn.Pools.CriticalStrikeChancePct);
        Assert.Equal(45, conn.Pools.SpellCriticalStrikeChancePct);
        Assert.Equal(725, conn.Pools.SpellCriticalStrikeChance);
        Assert.Equal(120, conn.Pools.ColdResistance); // Uncapped raw, with the capped value beside it.
        Assert.Equal(75, conn.Pools.ColdMaxResistance);

        // The spell consumers read the same derived increase from the player's raw stats, exactly once.
        var player = Program.CharacterPlayerStats(conn);
        Assert.Equal(45, player[stats.Key("critical_strike_chance_+%")!.Value]);
    }

    [Fact]
    public void A_dev_flag_at_zero_disables_its_consumer_only()
    {
        var stats = new StatCatalog(Data());
        var on = Conn(stats, BothFlagsAndResistances);
        Assert.Equal(22, on.Pools.Evasion);
        Assert.Equal(45, on.Pools.CriticalStrikeChancePct);

        var coldOff = Conn(stats, [.. BothFlagsAndResistances, (ResistanceOvercap.ColdEvasionFlag, 0)]);
        Assert.Equal(15, coldOff.Pools.Evasion); // Back to the bare 15 × 1.02; the overcap is gone, not capped.
        Assert.Equal(2, coldOff.Pools.EvasionPct);
        Assert.Equal(0, coldOff.Pools.ColdOvercapEvasionPct);
        Assert.Equal(45, coldOff.Pools.CriticalStrikeChancePct); // The lightning consumer keeps working.
    }

    [Fact]
    public void A_kitava_penalty_change_moves_the_derived_increases()
    {
        var stats = new StatCatalog(Data());
        var conn = Conn(stats, (ResistanceOvercap.ColdEvasionFlag, 1), (ResistanceOvercap.LightningCritFlag, 1),
            ("base_cold_damage_resistance_%", 120), ("base_lightning_damage_resistance_%", 120));

        lock (conn.Monsters)
        {
            conn.QuestFlags.Add("a5q6-CompletedBoss");
            conn.Pools = Program.PoolsFor(conn, 1);
            Assert.Equal(15, conn.Pools.ColdOvercapEvasionPct); // 120 - 30 = 90 uncapped, 15 above the maximum.
            Assert.Equal(15, conn.Pools.LightningOvercapCritPct);

            conn.QuestFlags.Add("a10q3-SeenBoss");
            conn.Pools = Program.PoolsFor(conn, 1);
            Assert.Equal(0, conn.Pools.ColdOvercapEvasionPct); // 60 uncapped, below the maximum.
            Assert.Equal(0, conn.Pools.LightningOvercapCritPct);
        }
    }
}
