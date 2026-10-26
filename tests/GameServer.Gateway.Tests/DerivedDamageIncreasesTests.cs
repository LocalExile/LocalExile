using System.Text.Json;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class DerivedDamageIncreasesTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "tables", "Stats.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables/Stats.json");
    }

    static Dictionary<uint, int> Stats(params (string Name, int Value)[] values) => values.ToDictionary(
        v => Program.Stats.Key(v.Name) ?? throw new InvalidOperationException($"Missing stat {v.Name}"), v => v.Value);

    static Func<string, int> Reader(IReadOnlyDictionary<uint, int> stats) =>
        name => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    static ClientState Conn(params (string Name, int Value)[] values)
    {
        TestSaves.Ensure();
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1)
        {
            Character = $"Derived{Guid.NewGuid():N}"[..24],
        };
        foreach (var (key, value) in Stats(values)) conn.PlayerStats[key] = value;
        conn.Pools = Program.PoolsFor(conn, 1);
        return conn;
    }

    [Theory]
    [InlineData(50932, "your mind is destroyed", DerivedDamageIncreases.SpellToAttack, 11811, 1)]
    [InlineData(24394, "Scourge of the Wilds", DerivedDamageIncreases.MinionToOwner, 12161, 1)]
    [InlineData(2103, "Steel Willed", DerivedDamageIncreases.AttackPerArmour, 13019, 2)]
    [InlineData(6281, "Forest Tracking", DerivedDamageIncreases.AttackPerEvasion, 2434, 3)]
    public void Real_target_nodes_keep_their_raw_sources(int node, string name, string stat, uint key, int value)
    {
        var tree = new PassiveTree(Data());
        Assert.Equal(name, tree.Find((ushort)node)!.Name);
        Assert.Equal(key, Program.Stats.Key(stat));
        var sources = tree.Stats([(ushort)node]);
        Assert.Equal(value, sources[key]);
        if (node == 24394)
        {
            Assert.Equal(2683u, Program.Stats.Key(DerivedDamageIncreases.MinionPerDexterity));
            Assert.Equal(1, sources[2683]);
            Assert.Equal(100, sources[Program.Stats.Key("base_dexterity")!.Value]);
        }
        // The catalog returns sources, never synthesized attack/generic increases or final defence values.
        Assert.False(sources.ContainsKey(StatKeys.AttackDamagePct));
        Assert.False(sources.ContainsKey(StatKeys.DamagePct));
    }

    [Theory]
    [InlineData(11811)]
    [InlineData(12161)]
    public void Transfer_stats_are_unscalable_flags_not_the_number_150(int key)
    {
        using var rows = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Data(), "tables", "Stats.json")));
        var row = rows.RootElement[key - 1];
        Assert.Equal(4, row.GetProperty("Semantics").GetInt32());
        Assert.False(row.GetProperty("IsScalable").GetBoolean());
    }

    [Theory]
    [InlineData(0, 1, 320)]
    [InlineData(1, 1, 323)]
    [InlineData(2, 1, 323)]
    [InlineData(1, -1, 317)]
    [InlineData(1, -200, 0)]
    public void Spell_transfer_scales_base_and_added_damage_in_the_same_bucket(int flag, int spell, double expected)
    {
        var stats = Stats((DerivedDamageIncreases.SpellToAttack, flag), ("spell_damage_+%", spell),
            ("damage_+%", 20), ("attack_damage_+%", 30), ("physical_damage_+%", 10),
            ("attack_minimum_added_physical_damage", 100), ("attack_maximum_added_physical_damage", 100));

        var hit = Program.AttackHit(new(833, 100, 100, 8), stats, melee: false);

        // (100 base + 100 added) * (1 + 20% + 30% + 10% + 1.5 * spell increase), clamped only after summing.
        Assert.Equal(expected, hit[DamageType.Physical].Min, 8);
        Assert.Equal(expected, hit[DamageType.Physical].Max, 8);
        Assert.Equal(20, stats[StatKeys.DamagePct]);
        Assert.Equal(30, stats[StatKeys.AttackDamagePct]);
    }

    [Theory]
    [InlineData(-1, 203)]
    [InlineData(4, 203)]
    [InlineData(5, 206)]
    [InlineData(104, 263)]
    [InlineData(105, 266)]
    public void Minion_transfer_counts_complete_groups_of_final_dexterity(int dexterity, double expected)
    {
        var stats = Stats((DerivedDamageIncreases.MinionToOwner, 1), ("minion_damage_+%", 1),
            (DerivedDamageIncreases.MinionPerDexterity, 1), ("dexterity", dexterity), ("base_dexterity", 5000));
        var hit = Program.AttackHit(new(833, 200, 200, 8), stats, melee: false);
        Assert.Equal(expected, hit.MinTotal, 8);

        stats[Program.Stats.Key(DerivedDamageIncreases.MinionToOwner)!.Value] = 0;
        Assert.Equal(200, Program.AttackHit(new(833, 200, 200, 8), stats, melee: false).MinTotal);
    }

    [Theory]
    [InlineData(0, 320)]
    [InlineData(1, 323)]
    [InlineData(-1, 317)]
    [InlineData(-200, 0)]
    public void Direct_minion_increases_and_reductions_remain_fractional(int minion, double expected)
    {
        var stats = Stats((DerivedDamageIncreases.MinionToOwner, 1), ("minion_damage_+%", minion),
            ("damage_+%", 20), ("attack_damage_+%", 30), ("physical_damage_+%", 10),
            ("attack_minimum_added_physical_damage", 100), ("attack_maximum_added_physical_damage", 100));
        Assert.Equal(expected, Program.AttackHit(new(833, 100, 100, 8), stats, melee: false).MinTotal, 8);
    }

    [Fact]
    public void Minion_per_dexterity_reductions_transfer_with_the_direct_increase()
    {
        var stats = Stats((DerivedDamageIncreases.MinionToOwner, 1), ("minion_damage_+%", 1),
            (DerivedDamageIncreases.MinionPerDexterity, -1), ("dexterity", 104));
        Assert.Equal(143, Program.AttackHit(new(833, 200, 200, 8), stats, melee: false).MinTotal, 8);
    }

    [Theory]
    [InlineData(449, 300)]
    [InlineData(450, 310)]
    [InlineData(899, 310)]
    [InlineData(900, 320)]
    public void Defence_scaling_uses_complete_450_groups_of_final_ratings(int rating, double expected)
    {
        var stats = Stats((DerivedDamageIncreases.AttackPerArmour, 2), (DerivedDamageIncreases.AttackPerEvasion, 3),
            ("physical_damage_reduction_rating", rating), ("evasion_rating", rating),
            ("base_physical_damage_reduction_rating", 90000), ("base_evasion_rating", 90000),
            ("damage_+%", 50), ("attack_minimum_added_physical_damage", 100), ("attack_maximum_added_physical_damage", 100));
        Assert.Equal(expected, Program.AttackHit(new(833, 100, 100, 8), stats, melee: false).MinTotal, 8);

        stats[Program.Stats.Key(DerivedDamageIncreases.AttackPerArmour)!.Value] = 0;
        stats[Program.Stats.Key(DerivedDamageIncreases.AttackPerEvasion)!.Value] = 0;
        Assert.Equal(300, Program.AttackHit(new(833, 100, 100, 8), stats, melee: false).MinTotal);
    }

    [Fact]
    public void Each_final_defence_has_its_own_signed_coefficient()
    {
        var stats = Stats((DerivedDamageIncreases.AttackPerArmour, 2), (DerivedDamageIncreases.AttackPerEvasion, -3),
            ("physical_damage_reduction_rating", 900), ("evasion_rating", 450));
        Assert.Equal(101, Program.AttackHit(new(833, 100, 100, 8), stats, melee: false).MinTotal);
        stats[Program.Stats.Key("physical_damage_reduction_rating")!.Value] = -450;
        Assert.Equal(97, Program.AttackHit(new(833, 100, 100, 8), stats, melee: false).MinTotal);
    }

    [Fact]
    public void More_speed_critical_and_conditional_modifiers_do_not_transfer()
    {
        var stats = Stats((DerivedDamageIncreases.SpellToAttack, 1), (DerivedDamageIncreases.MinionToOwner, 1),
            ("spell_damage_aura_spell_damage_+%_final", 1000), ("spell_damage_+%_final_from_arcane_surge", 1000),
            ("minion_damage_+%_final_from_luminary", 1000), ("minion_attack_speed_+%", 1000),
            ("minion_cast_speed_+%", 1000), ("minion_critical_strike_chance_+%", 1000),
            ("minion_critical_strike_multiplier_+", 1000), ("minion_damage_+%_if_enemy_hit_recently", 1000),
            ("spell_critical_strike_chance_+%", 1000), ("base_cast_speed_+%", 1000));
        Assert.Equal(default, DerivedDamageIncreases.From(Reader(stats)));
        Assert.Equal(100, Program.AttackHit(new(833, 100, 100, 8), stats, melee: false).MinTotal);
    }

    [Theory]
    [InlineData(false, 224.614, 237.328, 127.14)]
    [InlineData(true, 279.734, 295.568, 158.34)]
    public void Wolf_flat_mana_lightning_melee_context_and_final_Rage_compose(bool melee,
        double physicalMin, double physicalMax, double lightning)
    {
        // The owner's wolf flat bonus is represented by its existing raw attack stats; no minion runtime is needed.
        var stats = Stats(("attack_minimum_added_physical_damage", 6), ("attack_maximum_added_physical_damage", 12),
            (DamageStats.MaxManaAddedLightning, 6), ("maximum_mana", 1000),
            ("damage_+%", 20), ("attack_damage_+%", 30), ("melee_damage_+%", 40),
            (DerivedDamageIncreases.SpellToAttack, 1), ("spell_damage_+%", 1),
            (DerivedDamageIncreases.MinionToOwner, 1), ("minion_damage_+%", 1),
            (DerivedDamageIncreases.AttackPerArmour, 2), ("physical_damage_reduction_rating", 900),
            (DerivedDamageIncreases.AttackPerEvasion, 3), ("evasion_rating", 900),
            ("attack_damage_+%_final_from_rage", 30));

        var hit = Program.AttackHit(new(833, 100, 100, 8), stats, melee);

        Assert.Equal(physicalMin, hit[DamageType.Physical].Min, 8);
        Assert.Equal(physicalMax, hit[DamageType.Physical].Max, 8);
        Assert.Equal(lightning, hit[DamageType.Lightning].Min, 8);
        Assert.Equal(lightning, hit[DamageType.Lightning].Max, 8);
    }

    [Fact]
    public void Conversion_gain_and_attack_effectiveness_do_not_apply_the_derived_increase_twice()
    {
        var stats = Stats((DerivedDamageIncreases.SpellToAttack, 1), ("spell_damage_+%", 1),
            (DerivedDamageIncreases.MinionToOwner, 1), ("minion_damage_+%", 1),
            ("damage_+%", 20), ("attack_damage_+%", 30), ("physical_damage_+%", 10),
            ("attack_minimum_added_physical_damage", 100), ("attack_maximum_added_physical_damage", 100),
            ("physical_damage_%_to_convert_to_lightning", 100), ("lightning_damage_%_to_convert_to_fire", 100),
            ("physical_damage_%_to_add_as_chaos", 50), ("attack_damage_+%_final_from_rage", 30));
        var hit = SpellDamage.WithEffectiveness(Program.AttackHit(new(833, 100, 100, 8), stats, melee: false), 5000)
            .Convert(DamageStats.Conversion(stats, Program.Stats, attack: true));
        Assert.Equal(0, hit[DamageType.Physical].Min);
        Assert.Equal(0, hit[DamageType.Lightning].Min);
        Assert.Equal(211.9, hit[DamageType.Fire].Min, 8);
        Assert.Equal(105.95, hit[DamageType.Chaos].Min, 8);
    }

    [Theory]
    [InlineData(0, 360)]
    [InlineData(1, 417)]
    public void Owner_spell_hit_and_packet_share_generic_transfer_with_base_and_effective_added_damage(int flag, double expected)
    {
        var stats = Stats((DerivedDamageIncreases.MinionToOwner, flag), ("minion_damage_+%", -1),
            (DerivedDamageIncreases.MinionPerDexterity, 1), ("dexterity", 104),
            (DerivedDamageIncreases.SpellToAttack, 1), (DerivedDamageIncreases.AttackPerArmour, 2),
            ("physical_damage_reduction_rating", 900), (DerivedDamageIncreases.AttackPerEvasion, 3), ("evasion_rating", 900),
            ("attack_damage_+%", 1000), ("melee_damage_+%", 1000), ("attack_damage_+%_final_from_rage", 30),
            ("damage_+%", 20), ("spell_damage_+%", 50), ("fire_damage_+%", 10),
            ("spell_minimum_base_fire_damage", 100), ("spell_maximum_base_fire_damage", 100),
            ("spell_minimum_added_fire_damage", 200), ("spell_maximum_added_fire_damage", 200));
        var get = Reader(stats);
        var hit = SpellDamage.Hit(get, addedEffectiveness: .5);
        var packet = SpellDamage.Packet(DamagePacket.Of(DamageType.Fire, 100, 100), get, addedEffectiveness: .5);
        Assert.Equal(expected, hit.Min, 8);
        Assert.Equal(expected, hit.Max, 8);
        Assert.Equal(expected, packet.MinTotal, 8);
        Assert.Equal(expected, packet.MaxTotal, 8);
    }

    [Fact]
    public void Real_Wildspeaker_source_reaches_final_dexterity_panel_attack_and_Fireball_without_synthesized_stats()
    {
        var conn = Conn(("minion_damage_+%", 1));
        conn.MainAscendancy = new(Nodes: [24394]);
        conn.Pools = Program.PoolsFor(conn, 1);
        var player = Program.CharacterPlayerStats(conn);
        Assert.Equal(Program.Skills.ClassFor(conn.ClassRow).Dexterity + 100, conn.Pools.Dexterity);
        Assert.Equal(114, player[Program.Stats.Key("dexterity")!.Value]);
        Assert.Equal(34.5, DerivedDamageIncreases.From(Reader(player)).GenericPercent);
        Assert.Equal(1, player[Program.Stats.Key("minion_damage_+%")!.Value]);
        Assert.Equal(0, player.GetValueOrDefault(StatKeys.DamagePct));
        Assert.Equal(0, player.GetValueOrDefault(StatKeys.AttackDamagePct));

        var weapon = Program.Skills.UnarmedFor(conn.ClassRow);
        var attack = Program.AttackHit(weapon, player, melee: true);
        Assert.Equal(2.73, attack.MinTotal, 8);
        Assert.Equal(6.825, attack.MaxTotal, 8);
        Assert.Equal(2, player[Program.Stats.Key("main_hand_minimum_total_damage")!.Value]);
        Assert.Equal(6, player[Program.Stats.Key("main_hand_maximum_total_damage")!.Value]);
        // FinalPlayerStats calls these after CharacterPlayerStats; they must retain the derived physical rows.
        Assert.Equal(6, weapon.Stats(player, attack).ToDictionary(x => x.Item1, x => x.Item2)[StatKeys.MainHandMaxPhysical]);
        Assert.Equal(6, weapon.OffStats(player, attack).ToDictionary(x => x.Item1, x => x.Item2)[StatKeys.OffHandMaxPhysical]);

        int fireball = Program.Skills.EffectRow("Fireball");
        var skill = Program.Skills.EffectStatsFor(fireball, 1, 0, player, characterStatsWin: true);
        var spell = SpellDamage.Packet(Program.Skills.HitDamage(fireball, 1), Reader(skill));
        Assert.Equal(12.105, spell.MinTotal, 8);
        Assert.Equal(18.83, spell.MaxTotal, 8);

        conn.MainAscendancy = new();
        conn.Pools = Program.PoolsFor(conn, 1);
        var removed = Program.CharacterPlayerStats(conn);
        Assert.Equal(14, removed[Program.Stats.Key("dexterity")!.Value]);
        Assert.Equal(0, DerivedDamageIncreases.From(Reader(removed)).GenericPercent);
        Assert.Equal(5, removed[Program.Stats.Key("main_hand_maximum_total_damage")!.Value]);
    }

    [Fact]
    public void Character_player_stats_hand_final_defences_to_attacks_once()
    {
        var conn = Conn((DerivedDamageIncreases.AttackPerArmour, 2), (DerivedDamageIncreases.AttackPerEvasion, 3));
        conn.Pools = CharacterStats.For(Program.Skills.ClassFor(conn.ClassRow), 1,
            new CharacterStats.Bonus(Armour: 225, ArmourPct: 100, Evasion: 285, EvasionPct: 198));
        var player = Program.CharacterPlayerStats(conn);
        Assert.Equal(450, player[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
        Assert.Equal(900, player[Program.Stats.Key("evasion_rating")!.Value]);
        Assert.Equal(8, DerivedDamageIncreases.From(Reader(player)).AttackPercent);
        Assert.Equal(110, Program.AttackHit(new(833, 100, 100, 8), player, melee: true).MinTotal, 8);
    }

    [Theory]
    [InlineData(0, 420)]
    [InlineData(1, 505.5)]
    public void Hostile_Righteous_Fire_uses_generic_owner_transfer_in_its_increased_bucket(int flag, double expected)
    {
        var conn = Conn((DerivedDamageIncreases.MinionToOwner, flag), ("minion_damage_+%", -1),
            (DerivedDamageIncreases.MinionPerDexterity, 1), ("dexterity", 104), ("damage_+%", 20),
            ("fire_damage_+%", 10), ("elemental_damage_+%", 5), ("burn_damage_+%", 3), ("damage_over_time_+%", 2),
            ("delirium_aura_damage_over_time_+%_final", 50),
            (DerivedDamageIncreases.SpellToAttack, 1), ("spell_damage_+%", 1000),
            (DerivedDamageIncreases.AttackPerArmour, 2), ("physical_damage_reduction_rating", 900),
            (DerivedDamageIncreases.AttackPerEvasion, 3), ("evasion_rating", 900), ("attack_damage_+%_final_from_rage", 30));
        conn.Pools = conn.Pools with { Life = 100, EnergyShield = 100 };
        var rf = new RighteousFireState(1, 2, null, 3, 6000, 5400, 18) { EnergyShieldDamagePctPerMinute = 6000 };
        // (100 life + 100 ES) * (1 + 40% + optional 28.5%) * 1.5 more DoT.
        Assert.Equal(expected, Program.RighteousFireDamagePerSecond(conn, rf), 8);
        conn.PlayerStats[Program.Stats.Key("spell_skills_deal_no_damage")!.Value] = 1;
        Assert.Equal(0, Program.RighteousFireDamagePerSecond(conn, rf));
    }
}
