using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class UtilityFlasksTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<ItemCatalog> Catalog = new(() => new ItemCatalog(Data()));
    static readonly Lazy<StatCatalog> StatNames = new(() => new StatCatalog(Data()));

    static UtilityFlasks.Utility? Of(string name) => Catalog.Value.UtilityFlaskOf(Catalog.Value.Find(name)!);

    static UtilityFlasks.Utility Utility(params (string, int)[] stats) =>
        new(0, 121, "test", stats, 6.0, 30, 60);

    static Item Flask(int charges = 60, byte quality = 0, ItemRarity rarity = ItemRarity.Normal) =>
        new(0x1111, ["Base", "Charges", "Flask"], 0x2222) { Charges = charges, Quality = quality, Rarity = rarity };

    static int None(string _) => 0;

    static IEnumerable<(string, int)> Plain(IEnumerable<UtilityFlasks.Source> stats) => stats.Select(s => (s.Stat, s.Value));

    [Fact]
    public void The_flasks_come_from_Flasks_dat_BuffDefinitions_and_ComponentCharges()
    {
        var quicksilver = Of("Quicksilver Flask")!;
        Assert.Equal(121, quicksilver.BuffRow);
        Assert.Equal("flask_utility_sprint", quicksilver.BuffId);
        Assert.Equal([("base_movement_velocity_+%", 40)], quicksilver.Stats);
        // RecoveryTime (60), as Path of Building and RePoE 3.29 give it: 6 s, not RecoveryTime2 (20).
        Assert.Equal(6.0, quicksilver.Seconds);
        Assert.Equal((30, 60), (quicksilver.ChargesPerUse, quicksilver.MaxCharges));

        var ruby = Of("Ruby Flask")!;
        Assert.Equal("flask_utility_resist_fire", ruby.BuffId);
        Assert.Equal([("base_fire_damage_resistance_%", 40), ("base_maximum_fire_damage_resistance_%", 5)], ruby.Stats);
        Assert.Equal(8.0, ruby.Seconds);

        Assert.Equal([("base_physical_damage_reduction_rating", 1500)], Of("Granite Flask")!.Stats);
        Assert.Equal([("base_evasion_rating", 1500)], Of("Jade Flask")!.Stats);
        Assert.Equal([("base_chaos_damage_resistance_%", 35)], Of("Amethyst Flask")!.Stats);
        Assert.Equal([("base_resist_all_elements_%", 35)], Of("Bismuth Flask")!.Stats);
        Assert.Equal([("basalt_flask_armour_+%_final", 20)], Of("Basalt Flask")!.Stats);
        // Life and mana flasks and other items are not utility flasks.
        Assert.Null(Of("Small Life Flask"));
        Assert.Null(Catalog.Value.UtilityFlaskOf(Catalog.Value.Row(384)!));   // Driftwood Wand
    }

    [Fact]
    public void Every_flask_has_charges_and_each_buff_stat_is_supported_or_listed()
    {
        // Every utility row of Flasks.dat (by BaseItemTypes name): its buff's stats as the tables give them, how each
        // counts here, its duration and its charges (per use, maximum). A new row or a changed value fails this test.
        // M modelled, P only on the character panel (no ailments, stun or curses on the player here), W Ward (#206),
        // U not applied.
        var expected = new Dictionary<string, (string Stats, double Seconds, int PerUse, int Max)>
        {
            ["Diamond Flask"] = ("critical_strike_chance_+% 100 M", 6, 20, 40),
            ["Ruby Flask"] = ("base_fire_damage_resistance_% 40 M, base_maximum_fire_damage_resistance_% 5 M", 8, 20, 50),
            ["Sapphire Flask"] = ("base_cold_damage_resistance_% 40 M, base_maximum_cold_damage_resistance_% 5 M", 8, 20, 50),
            ["Topaz Flask"] = ("base_lightning_damage_resistance_% 40 M, base_maximum_lightning_damage_resistance_% 5 M", 8, 20, 50),
            ["Granite Flask"] = ("base_physical_damage_reduction_rating 1500 M", 6, 30, 60),
            ["Quicksilver Flask"] = ("base_movement_velocity_+% 40 M", 6, 30, 60),
            ["Amethyst Flask"] = ("base_chaos_damage_resistance_% 35 M", 6.5, 35, 65),
            ["Quartz Flask"] = ("phase_through_objects 1 M, base_spell_suppression_chance_% 10 M", 6, 30, 60),
            ["Jade Flask"] = ("base_evasion_rating 1500 M", 6, 30, 60),
            ["Basalt Flask"] = ("basalt_flask_armour_+%_final 20 M", 8, 40, 60),
            ["Aquamarine Flask"] = ("base_frozen_effect_on_self_+% -60 P", 8, 20, 50),
            ["Stibnite Flask"] = ("stibnite_flask_evasion_rating_+%_final 20 M", 8, 40, 60),
            ["Sulphur Flask"] = ("damage_+% 40 M", 8, 40, 60),
            ["Silver Flask"] = ("onslaught: attack_speed_+% 20 M, base_cast_speed_+% 20 M, base_movement_velocity_+% 20 M", 6, 40, 60),
            ["Bismuth Flask"] = ("base_resist_all_elements_% 35 M", 8.5, 15, 40),
            ["Gold Flask"] = ("base_item_found_rarity_+% 30 M", 5, 60, 80),
            ["Corundum Flask"] = ("stun_threshold_+% 30 P", 7.5, 20, 50),
            ["Iron Flask"] = ("base_ward 200 W", 4, 40, 60),
        };
        static string Code(UtilityFlasks.Support s) => s switch
        {
            UtilityFlasks.Support.Modelled => "M", UtilityFlasks.Support.PanelOnly => "P", UtilityFlasks.Support.Ward => "W", _ => "U",
        };
        static string Describe(IEnumerable<(string Stat, int Value)> stats) =>
            string.Join(", ", stats.Select(s => $"{s.Stat} {s.Value} {Code(UtilityFlasks.SupportOf(s.Stat))}"));

        var found = Enumerable.Range(0, 5000).Select(Catalog.Value.Row).OfType<ItemCatalog.BaseType>()
            .Select(t => (t.Name, U: Catalog.Value.UtilityFlaskOf(t))).Where(x => x.U is not null).ToList();
        Assert.Equal(expected.Keys.Order(), found.Select(f => f.Name).Order());
        foreach (var (name, u) in found)
        {
            var (stats, seconds, perUse, max) = expected[name];
            Assert.Equal(stats, u!.Extra is { } x ? $"{x.BuffId}: {Describe(x.Stats)}" : Describe(u.Stats));
            Assert.Equal((seconds, perUse, max), (u.Seconds, u.ChargesPerUse, u.MaxCharges));
            Assert.Equal((perUse, max), (Catalog.Value.FlaskChargesOf(Catalog.Value.Find(name)!)!.ChargesPerUse, Catalog.Value.FlaskChargesOf(Catalog.Value.Find(name)!)!.MaxCharges));
        }
    }

    [Fact]
    public void Every_flask_at_quality_0_and_20_with_increased_duration_and_effect()
    {
        // Golden values: quality 20 and 30% increased duration multiply (× 1.3 × 1.2 = 1.56); 25% increased effect scales
        // each stat with the fraction dropped (PoB ScaleAddMod); Onslaught keeps its 20s.
        var golden = new Dictionary<string, (double Plain, double Long, string Stronger)>
        {
            ["Diamond Flask"] = (6, 9.36, "critical_strike_chance_+% 125"),
            ["Ruby Flask"] = (8, 12.48, "base_fire_damage_resistance_% 50, base_maximum_fire_damage_resistance_% 6"),
            ["Sapphire Flask"] = (8, 12.48, "base_cold_damage_resistance_% 50, base_maximum_cold_damage_resistance_% 6"),
            ["Topaz Flask"] = (8, 12.48, "base_lightning_damage_resistance_% 50, base_maximum_lightning_damage_resistance_% 6"),
            ["Granite Flask"] = (6, 9.36, "base_physical_damage_reduction_rating 1875"),
            ["Quicksilver Flask"] = (6, 9.36, "base_movement_velocity_+% 50"),
            ["Amethyst Flask"] = (6.5, 10.14, "base_chaos_damage_resistance_% 43"),
            ["Quartz Flask"] = (6, 9.36, "phase_through_objects 1, base_spell_suppression_chance_% 12"),
            ["Jade Flask"] = (6, 9.36, "base_evasion_rating 1875"),
            ["Basalt Flask"] = (8, 12.48, "basalt_flask_armour_+%_final 25"),
            ["Aquamarine Flask"] = (8, 12.48, "base_frozen_effect_on_self_+% -75"),
            ["Stibnite Flask"] = (8, 12.48, "stibnite_flask_evasion_rating_+%_final 25"),
            ["Sulphur Flask"] = (8, 12.48, "damage_+% 50"),
            ["Silver Flask"] = (6, 9.36, "attack_speed_+% 20, base_cast_speed_+% 20, base_movement_velocity_+% 20"),
            ["Bismuth Flask"] = (8.5, 13.26, "base_resist_all_elements_% 43"),
            ["Gold Flask"] = (5, 7.8, "base_item_found_rarity_+% 37"),
            ["Corundum Flask"] = (7.5, 11.7, "stun_threshold_+% 37"),
            ["Iron Flask"] = (4, 6.24, "base_ward 250"),
        };
        var longer = new Dictionary<string, int> { ["local_flask_duration_+%"] = 30, ["local_flask_effect_+%"] = 25 };
        foreach (var (name, (plain, longSeconds, stronger)) in golden)
        {
            var u = Of(name)!;
            Assert.Equal(plain, UtilityFlasks.For(u, Flask(), new Dictionary<string, int>(), None).Seconds, 6);
            var (stats, seconds) = UtilityFlasks.For(u, Flask(quality: 20), longer, None);
            Assert.Equal(longSeconds, seconds, 6);
            Assert.Equal(stronger, string.Join(", ", stats.Select(s => $"{s.Stat} {s.Value}")));
        }
    }

    [Fact]
    public void Iron_Flask_Ward_contributes_to_the_authoritative_pool()
    {
        // Ward contributes to its own pool, independently of Armour/Evasion and plain native raw stats.
        var iron = UtilityFlasks.For(Of("Iron Flask")!, Flask(), new Dictionary<string, int> { ["local_flask_ward_+%_during_effect"] = 30 }, None).Stats;
        var stats = iron.Select(s => (s.Stat, s.Value)).ToList();
        Assert.Equal((200, 30), UtilityFlasks.Ward(stats));
        Assert.Equal(CharacterStats.Bonus.None with { Ward = 200, WardPct = 30 },
            UtilityFlasks.Apply(CharacterStats.Bonus.None, stats));
        Assert.Empty(UtilityFlasks.RawStats(stats, StatNames.Value));
    }

    [Fact]
    public void Panel_only_stats_go_to_the_client_and_change_no_pool()
    {
        var aquamarine = UtilityFlasks.For(Of("Aquamarine Flask")!, Flask(), new Dictionary<string, int>(), None).Stats.Select(s => (s.Stat, s.Value)).ToList();
        Assert.Equal(CharacterStats.Bonus.None, UtilityFlasks.Apply(CharacterStats.Bonus.None, aquamarine));
        var names = StatNames.Value;
        Assert.Equal([("base_frozen_effect_on_self_+%", -60)], UtilityFlasks.RawStats(aquamarine, names).Select(r => (names.Name(r.Key), r.Value)));
    }

    [Fact]
    public void Kills_and_towns_refill_utility_flasks_too()
    {
        TestSaves.Ensure();
        var catalog = Catalog.Value;
        var items = PlayerInventories.For($"Refill{Guid.NewGuid():N}"[..20], catalog);
        var quicksilver = items.Add(PlayerInventories.Flask, 2, 0, ItemCatalog.Create(catalog.Find("Quicksilver Flask")!) with { Charges = 0 });
        var iron = items.Add(PlayerInventories.Flask, 3, 0, ItemCatalog.Create(catalog.Find("Iron Flask")!) with { Charges = 5 });

        var changed = Flasks.Fill(items, catalog, int.MaxValue);

        Assert.Contains(changed, c => c.Id == quicksilver.Id && c.Item.Charges == 60);
        Assert.Contains(changed, c => c.Id == iron.Id && c.Item.Charges == 60);
        // Full flasks take no more.
        Assert.DoesNotContain(Flasks.Fill(items, catalog, int.MaxValue), c => c.Id == quicksilver.Id || c.Id == iron.Id);
    }

    [Fact]
    public void The_silver_flask_grants_onslaught_from_BuffTemplates()
    {
        var silver = Of("Silver Flask")!;
        Assert.Empty(silver.Stats);
        Assert.Equal("onslaught", silver.Extra!.BuffId);
        Assert.Equal(215, silver.Extra.BuffRow);
        Assert.Equal([("attack_speed_+%", 20), ("base_cast_speed_+%", 20), ("base_movement_velocity_+%", 20)], silver.Extra.Stats);
        Assert.Equal(silver.Extra.Stats, Plain(UtilityFlasks.For(silver, Flask(), new Dictionary<string, int>(), None).Stats));
    }

    [Fact]
    public void Quality_and_duration_multiply_and_flask_effect_scales_the_stats()
    {
        var u = Utility(("base_movement_velocity_+%", 40), ("phase_through_objects", 1), ("not_a_flask_stat", 5));

        var plain = UtilityFlasks.For(u, Flask(), new Dictionary<string, int>(), None);
        Assert.Equal(6.0, plain.Seconds, 6);
        Assert.Equal([("base_movement_velocity_+%", 40), ("phase_through_objects", 1)], Plain(plain.Stats));   // unsupported ones left out

        // Path of Building Item.lua: base × (1 + increased) × (1 + quality) × (1 + more).
        Assert.Equal(7.2, UtilityFlasks.For(u, Flask(quality: 20), new Dictionary<string, int>(), None).Seconds, 6);
        Assert.Equal(6 * 1.3 * 1.2, UtilityFlasks.For(u, Flask(quality: 20), new Dictionary<string, int> { ["local_flask_duration_+%"] = 30 }, None).Seconds, 6);
        var tradeOff = UtilityFlasks.For(u, Flask(), new Dictionary<string, int> { ["local_flask_duration_+%"] = -33, ["local_flask_effect_+%"] = 25 }, None);
        Assert.Equal(4.02, tradeOff.Seconds, 6);
        Assert.Equal([("base_movement_velocity_+%", 50), ("phase_through_objects", 1)], Plain(tradeOff.Stats));
        // Reduced effect does not take a flag away.
        var weaker = UtilityFlasks.For(u, Flask(), new Dictionary<string, int> { ["local_flask_effect_+%"] = -25 }, None);
        Assert.Equal([("base_movement_velocity_+%", 30), ("phase_through_objects", 1)], Plain(weaker.Stats));
    }

    [Fact]
    public void Quartz_makes_the_player_phase_and_the_client_reads_the_virtual_stat()
    {
        var quartz = UtilityFlasks.For(Of("Quartz Flask")!, Flask(), new Dictionary<string, int>(), None).Stats.Select(s => (s.Stat, s.Value)).ToList();
        Assert.True(UtilityFlasks.Phasing(quartz));
        Assert.False(UtilityFlasks.Phasing([("base_movement_velocity_+%", 40)]));
        var names = StatNames.Value;
        Assert.Contains(("virtual_phase_through_objects", 1), UtilityFlasks.RawStats(quartz, names).Select(r => (names.Name(r.Key), r.Value)));
        Assert.Equal(10, UtilityFlasks.Apply(CharacterStats.Bonus.None, quartz).SpellSuppressionChance);
    }

    [Fact]
    public void A_phasing_player_walks_through_monsters_but_not_npcs()
    {
        var conn = Connection();
        conn.SetBlocker(0x2100, 30, 30, ClientState.NpcBlockerSize);
        var variety = new MonsterCatalog.Variety(0, "Metadata/Monsters/Test", "test", "Test", null, null);
        lock (conn.Monsters)
            conn.Monsters[0x3000] = new SpawnedMonster("m", new GameServer.Core.World.WalkEstimator(50, 50, 8), 10, 1, 100) { Variety = variety };
        Assert.Equal(2, conn.Blockers.Count);
        conn.Phasing = true;
        Assert.Equal((30, 30), (Assert.Single(conn.Blockers).X, conn.Blockers[0].Y));
    }

    [Fact]
    public void The_characters_flask_duration_and_effect_count()
    {
        var u = Utility(("base_movement_velocity_+%", 40));
        var character = new Dictionary<string, int> { ["flask_duration_+%"] = 20, ["flask_effect_+%"] = 10, ["magic_utility_flask_effect_+%"] = 15 };
        int Get(string name) => character.GetValueOrDefault(name);
        var local = new Dictionary<string, int> { ["local_flask_duration_+%"] = 10, ["local_flask_effect_+%"] = 15 };

        // Increased duration from the tree adds to the flask's own; increased effect likewise.
        var normal = UtilityFlasks.For(u, Flask(), local, Get);
        Assert.Equal(6 * 1.3, normal.Seconds, 6);
        Assert.Equal([("base_movement_velocity_+%", 50)], Plain(normal.Stats));   // 40 × 1.25
        // Magic utility flasks also get magic_utility_flask_effect_+%.
        Assert.Equal([("base_movement_velocity_+%", 56)], Plain(UtilityFlasks.For(u, Flask(rarity: ItemRarity.Magic), local, Get).Stats));   // 40 × 1.4
    }

    static UtilityFlasks.Active Active(uint item, UtilityFlasks.Utility u, double started, ushort buff = 1, ushort? extra = null) =>
        new(item, (byte)item, "test", u, buff, extra, Sources(u), started, started + u.Seconds);

    static List<UtilityFlasks.Source> Sources(UtilityFlasks.Utility u) =>
        [.. UtilityFlasks.For(u, Flask(), new Dictionary<string, int>(), None).Stats];

    [Fact]
    public void An_effect_runs_for_its_duration_and_a_flask_cannot_be_used_again_meanwhile()
    {
        var effects = new UtilityFlasks.Effects();
        var u = Utility(("base_movement_velocity_+%", 40));

        Assert.NotNull(effects.Start(Active(7, u, started: 10)));
        Assert.NotNull(effects.Of(7));
        Assert.Null(effects.Of(8));
        Assert.Equal([("base_movement_velocity_+%", 40)], effects.Stats());

        Assert.Empty(effects.End(15.9, all: false, out bool stop));
        Assert.False(stop);
        Assert.Equal(7u, Assert.Single(effects.End(16, all: false, out stop)).ItemId);
        Assert.True(stop);
        Assert.Null(effects.Of(7));
        Assert.Empty(effects.Stats());
        // The next use starts a ticker again.
        Assert.NotNull(effects.Start(Active(7, u, started: 20)));
    }

    [Fact]
    public void Each_refusal_says_why()
    {
        var u = Utility(("base_movement_velocity_+%", 40));
        var running = Active(7, u, started: 10);

        Assert.Null(UtilityFlasks.Refusal(false, null, Flask(charges: 30), u.Charges, 11));
        Assert.Equal("dead", UtilityFlasks.Refusal(true, null, Flask(), u.Charges, 11));
        Assert.Equal("its effect still runs (2.5 s left)", UtilityFlasks.Refusal(false, running, Flask(), u.Charges, 13.5));
        Assert.Equal("29/60 charges, a use needs 30", UtilityFlasks.Refusal(false, null, Flask(charges: 29), u.Charges, 11));
        // With a flask's reduced charges used (Chemist's 28%: floor(30 × 0.72) = 21).
        Assert.Null(UtilityFlasks.Refusal(false, null, Flask(charges: 21), FlaskCharges.Of(u.Charges, new Dictionary<string, int> { ["local_charges_used_+%"] = -28 }), 11));
    }

    [Fact]
    public void A_ticker_that_exits_lets_the_next_use_start_another()
    {
        var effects = new UtilityFlasks.Effects();
        var u = Utility(("base_movement_velocity_+%", 40));
        int first = effects.Start(Active(1, u, started: 0))!.Value;
        Assert.Null(effects.Start(Active(2, Utility(("base_evasion_rating", 1)) with { BuffRow = 124 }, started: 0)));

        // The ticker failed (a send threw, the connection broke …) with effects still running.
        effects.TickerExited(first);
        Assert.False(effects.Ticking);
        int second = effects.Start(Active(3, u, started: 1))!.Value;
        Assert.NotEqual(first, second);
        // A late exit of the old ticker leaves the new one alone.
        effects.TickerExited(first);
        Assert.True(effects.Ticking);
        effects.TickerExited(second);
        Assert.False(effects.Ticking);
    }

    [Fact]
    public void Two_kinds_stack_and_death_ends_all()
    {
        var effects = new UtilityFlasks.Effects();
        var quicksilver = Utility(("base_movement_velocity_+%", 40));
        var ruby = new UtilityFlasks.Utility(0, 117, "ruby", [("base_fire_damage_resistance_%", 40)], 8, 20, 50);

        effects.Start(Active(1, quicksilver, 0, buff: 1));
        Assert.Null(effects.Start(Active(2, ruby, 0, buff: 2)));
        Assert.Equal([("base_movement_velocity_+%", 40), ("base_fire_damage_resistance_%", 40)], effects.Stats());

        Assert.Equal(2, effects.End(1.5, all: true, out bool stop).Count);
        Assert.True(stop);
        Assert.False(effects.Any);
    }

    [Fact]
    public void Two_flasks_of_one_kind_keep_their_own_timers_and_only_the_strongest_applies()
    {
        // poewiki "Effects of flasks": each has an independent duration and only the strongest applies; when it ends, the
        // other one goes on. Here a 20% quality Quicksilver with 25% increased effect (50%, 7.2 s) and a plain one (40%, 6 s).
        var effects = new UtilityFlasks.Effects();
        var quicksilver = Utility(("base_movement_velocity_+%", 40));
        var strong = UtilityFlasks.For(quicksilver, Flask(quality: 20), new Dictionary<string, int> { ["local_flask_effect_+%"] = 25 }, None);
        var plain = UtilityFlasks.For(quicksilver, Flask(), new Dictionary<string, int>(), None);
        Assert.Equal((50, 7.2), (strong.Stats[0].Value, Math.Round(strong.Seconds, 6)));

        effects.Start(new UtilityFlasks.Active(1, 0, "plain", quicksilver, 1, null, plain.Stats, 0, plain.Seconds));
        Assert.Equal([("base_movement_velocity_+%", 40)], effects.Stats());
        effects.Start(new UtilityFlasks.Active(2, 1, "strong", quicksilver, 2, null, strong.Stats, 0.5, 0.5 + strong.Seconds));
        Assert.Equal([("base_movement_velocity_+%", 50)], effects.Stats());   // not 90

        // The plain one ends at 6 s; the strong one runs on to 7.7 s.
        Assert.Equal(1u, Assert.Single(effects.End(6, all: false, out _)).ItemId);
        Assert.Equal([("base_movement_velocity_+%", 50)], effects.Stats());
        Assert.Equal(2u, Assert.Single(effects.End(7.7, all: false, out bool stop)).ItemId);
        Assert.True(stop);

        // The other order: the stronger ends first and the weaker one's rest applies again.
        effects.Start(new UtilityFlasks.Active(2, 1, "strong", quicksilver, 3, null, strong.Stats, 10, 10 + 1));
        effects.Start(new UtilityFlasks.Active(1, 0, "plain", quicksilver, 4, null, plain.Stats, 10.5, 10.5 + plain.Seconds));
        Assert.Equal([("base_movement_velocity_+%", 50)], effects.Stats());
        effects.End(11, all: false, out _);
        Assert.Equal([("base_movement_velocity_+%", 40)], effects.Stats());
    }

    [Fact]
    public void During_effect_mods_count_while_the_flask_runs_and_scale_with_its_effect()
    {
        var jade = Of("Jade Flask")!;
        // of the Armadillo (60% increased armour during effect), of the Cheetah (14% movement speed), of the Rainbow (20%
        // additional elemental resistances), of Incision (55% critical strike chance), an immunity with less duration.
        var local = new Dictionary<string, int>
        {
            ["local_flask_armour_+%_while_healing"] = 60, ["local_flask_movement_speed_+%_while_healing"] = 14,
            ["local_flask_resistances_+%_while_healing"] = 20, ["local_flask_critical_strike_chance_+%_during_flask_effect"] = 55,
            ["local_flask_immune_to_shock_during_flask_effect"] = 1, ["local_flask_duration_+%_final"] = -40,
        };
        var (stats, seconds) = UtilityFlasks.For(jade, Flask(), local, None);
        Assert.Equal(6 * 0.6, seconds, 6);
        Assert.Equal(["buff 124: base_evasion_rating 1500", "mod: physical_damage_reduction_rating_+% 60",
            "mod: base_movement_velocity_+% 14", "mod: base_resist_all_elements_% 20", "mod: critical_strike_chance_+% 55",
            "mod: base_immune_to_shock 1"], stats.Select(s => $"{(s.Effect.StartsWith("mod") ? "mod" : s.Effect)}: {s.Stat} {s.Value}"));
        // 25% increased effect scales the mods too (rounded down); a flag stays 1.
        local["local_flask_effect_+%"] = 25;
        Assert.Equal([1875, 75, 17, 25, 68, 1], UtilityFlasks.For(jade, Flask(), local, None).Stats.Select(s => s.Value));

        var b = UtilityFlasks.Apply(CharacterStats.Bonus.None, UtilityFlasks.Strongest(stats));
        Assert.Equal((1500, 60, 14, 20, 20, 20, 55), (b.Evasion, b.ArmourPct, b.MovementVelocityPct, b.FireResistance, b.ColdResistance, b.LightningResistance, b.CriticalStrikeChancePct));
        // The immunity only reaches the panel: no ailments are inflicted here.
        Assert.Equal(UtilityFlasks.Support.PanelOnly, UtilityFlasks.SupportOf("base_immune_to_shock"));
    }

    [Fact]
    public void The_wiki_example_one_suffix_takes_the_strongest_different_ones_stack()
    {
        // poewiki "Effects of flasks": a Jade Flask (5 s) with +20% movement speed, 0.5 s later a Ruby Flask (3.5 s) with
        // +30% movement speed, 0.5 s later a Granite Flask. Movement speed 30% while both run, armour on top; when the Ruby
        // ends, the Jade's 20% for its last second. (The 3.29 movement suffix rolls up to 14%; the example's numbers.)
        var effects = new UtilityFlasks.Effects();
        UtilityFlasks.Active Use(uint item, string name, int movement, double at, double seconds)
        {
            var u = Of(name)!;
            var stats = UtilityFlasks.For(u, Flask(), new Dictionary<string, int> { ["local_flask_movement_speed_+%_while_healing"] = movement }, None).Stats;
            return new(item, (byte)item, name, u, (ushort)item, null, stats, at, at + seconds);
        }
        int Movement() => effects.Stats().Where(s => s.Stat == "base_movement_velocity_+%").Sum(s => s.Value);

        effects.Start(Use(1, "Jade Flask", 20, 0, 5));
        Assert.Equal(20, Movement());
        effects.Start(Use(2, "Ruby Flask", 30, 0.5, 3.5));
        Assert.Equal(30, Movement());
        effects.Start(Use(3, "Granite Flask", 0, 1, 4));
        Assert.Equal(30, Movement());
        Assert.Contains(("base_physical_damage_reduction_rating", 1500), effects.Stats());
        Assert.Contains(("base_evasion_rating", 1500), effects.Stats());
        Assert.Equal(2u, Assert.Single(effects.End(4, all: false, out _)).ItemId);   // the Ruby ends at 4 s
        Assert.Equal(20, Movement());
        Assert.Equal([1u, 3u], effects.End(5, all: false, out _).Select(e => e.ItemId).Order());   // the Jade and the Granite at 5 s
        Assert.Equal(0, Movement());
    }

    [Fact]
    public void The_strongest_is_the_largest_value_of_one_effect_and_stat()
    {
        Assert.Equal([("base_movement_velocity_+%", 50), ("base_movement_velocity_+%", 8)], UtilityFlasks.Strongest(
        [
            new("buff 121", "base_movement_velocity_+%", 40), new("buff 121", "base_movement_velocity_+%", 50),
            new("mod", "base_movement_velocity_+%", 8),   // a different effect stacks
        ]));
        // A penalty of one effect: the largest size wins (Aquamarine-like reduced effect on you).
        Assert.Equal([("base_frozen_effect_on_self_+%", -75)], UtilityFlasks.Strongest(
            [new("buff 126", "base_frozen_effect_on_self_+%", -60), new("buff 126", "base_frozen_effect_on_self_+%", -75)]));
    }

    [Fact]
    public void An_effect_ends_when_its_flask_leaves_its_cell()
    {
        var effects = new UtilityFlasks.Effects();
        effects.Start(Active(4, Utility(("base_movement_velocity_+%", 40)), started: 0));

        Assert.Empty(effects.End(1, all: false, out _, gone: _ => false));
        Assert.Equal(4u, Assert.Single(effects.End(1, all: false, out bool stop, gone: e => e.ItemId == 4)).ItemId);
        Assert.True(stop);
    }

    [Fact]
    public void Leaving_its_cell_ends_an_effect_while_the_unconfirmed_rule_is_on()
    {
        var effect = Active(4, Utility(("base_movement_velocity_+%", 40)), started: 0);   // item 4 in cell 4
        var flask = Flask();
        InventoryItem[] there = [new(4, 4, 0, flask)], moved = [new(4, 2, 0, flask)], gone = [new(5, 4, 0, flask)];

        // The current setting (not confirmed by any source): picked up, moved or swapped, the effect ends.
        Assert.True(UtilityFlasks.EndsWhenFlaskLeavesItsCell);
        Assert.False(UtilityFlasks.LeftItsCell(effect, there));
        Assert.True(UtilityFlasks.LeftItsCell(effect, moved));
        Assert.True(UtilityFlasks.LeftItsCell(effect, gone));
        Assert.True(UtilityFlasks.LeftItsCell(effect, []));
        // Flipped, the effect runs on wherever the flask is.
        Assert.False(UtilityFlasks.LeftItsCell(effect, [], ends: false));
        Assert.False(UtilityFlasks.LeftItsCell(effect, moved, ends: false));
    }

    [Fact]
    public void Running_flasks_add_to_the_pools()
    {
        var b = UtilityFlasks.Apply(new CharacterStats.Bonus(FireResistance: 10, ArmourPct: 100, ArmourMorePct: 15, MovementVelocityPct: 5),
        [
            ("base_fire_damage_resistance_%", 40), ("base_maximum_fire_damage_resistance_%", 5),
            ("base_resist_all_elements_%", 35), ("base_chaos_damage_resistance_%", 35),
            ("base_physical_damage_reduction_rating", 1500), ("basalt_flask_armour_+%_final", 20),
            ("base_evasion_rating", 1500), ("stibnite_flask_evasion_rating_+%_final", 20),
            ("base_movement_velocity_+%", 40), ("attack_speed_+%", 20), ("critical_strike_chance_+%", 100),
            ("base_spell_suppression_chance_%", 10),
        ]);

        Assert.Equal(10 + 40 + 35, b.FireResistance);
        Assert.Equal(35, b.ColdResistance);
        Assert.Equal(35, b.LightningResistance);
        Assert.Equal(35, b.ChaosResistance);
        Assert.Equal(5, b.FireMaxResistance);
        Assert.Equal(1500, b.Armour);
        Assert.Equal(38, b.ArmourMorePct);   // 15% more and 20% more: 1.15 × 1.2 = 1.38
        Assert.Equal(20, b.EvasionMorePct);
        Assert.Equal(45, b.MovementVelocityPct);
        Assert.Equal(20, b.AttackSpeedPct);
        Assert.Equal(100, b.CriticalStrikeChancePct);
        Assert.Equal(100, b.SpellCriticalStrikeChancePct);
        Assert.Equal(10, b.SpellSuppressionChance);

        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, b);
        Assert.Equal((int)(1500 * 2 * 1.38), pools.Armour);   // flat armour raised by increased armour and both mores
        Assert.Equal(GameServer.Core.World.DamageModel.DefaultMaxResistance + 5, pools.FireMaxResistance);
        Assert.Equal(45, pools.MovementVelocityPct);

        Assert.Same(b, UtilityFlasks.Apply(b, []));
    }

    [Fact]
    public void Maximum_resistances_crit_damage_and_cast_speed_also_go_out_as_plain_stats()
    {
        var stats = StatNames.Value;
        var raw = UtilityFlasks.RawStats(
        [
            ("base_maximum_fire_damage_resistance_%", 5), ("critical_strike_chance_+%", 100), ("damage_+%", 40),
            ("base_cast_speed_+%", 20), ("base_movement_velocity_+%", 40), ("base_fire_damage_resistance_%", 40),
            ("base_item_found_rarity_+%", 30),
        ], stats).ToDictionary(s => stats.Name(s.Key), s => s.Value);

        Assert.Equal(new Dictionary<string, int>
        {
            ["base_maximum_fire_damage_resistance_%"] = 5, ["critical_strike_chance_+%"] = 100, ["damage_+%"] = 40,
            ["cast_speed_+%"] = 20, ["base_item_found_rarity_+%"] = 30,   // the drops and the panel read the Gold Flask's
        }, raw);
    }

    [Fact]
    public void The_character_stats_get_the_running_flasks_plain_stats_on_top()
    {
        var names = StatNames.Value;
        uint crit = names.Key("critical_strike_chance_+%")!.Value, cast = names.Key("cast_speed_+%")!.Value;
        // What CharacterPlayerStats has before (passives' 30% crit, Haste's 5% cast speed), then the Diamond and Silver flasks.
        var stats = new Dictionary<uint, int> { [crit] = 30, [cast] = 5 };

        UtilityFlasks.AddRawStats(stats, [("critical_strike_chance_+%", 100), ("base_cast_speed_+%", 20), ("attack_speed_+%", 20)], names);

        Assert.Equal(130, stats[crit]);
        Assert.Equal(25, stats[cast]);
        Assert.Equal(2, stats.Count);   // attack speed goes through the pools, not as a plain stat
        UtilityFlasks.AddRawStats(stats, [], names);
        Assert.Equal(130, stats[crit]);
    }

    // The character as PoolsFor makes it, with the running flasks (Apply), and the walk estimate from its pools.
    static void Recompute(ClientState conn)
    {
        conn.Pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1, UtilityFlasks.Apply(CharacterStats.Bonus.None, conn.UtilityFlaskEffects.Stats()));
        conn.ApplyMovementSpeed();
    }

    static ClientState Connection() => new(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);

    [Fact]
    public void A_use_recomputes_the_pools_and_walk_speed_and_its_end_puts_them_back()
    {
        var conn = Connection();
        var ruby = new UtilityFlasks.Utility(0, 117, "ruby", [("base_fire_damage_resistance_%", 40)], 8, 20, 50);
        var quicksilver = Utility(("base_movement_velocity_+%", 40));

        var ticker = UtilityFlasks.Start(conn, Active(1, quicksilver, 0), () => Recompute(conn));
        UtilityFlasks.Start(conn, Active(2, ruby, 0, buff: 2), () => Recompute(conn));
        Assert.NotNull(ticker);
        Assert.Equal(Program.WalkSpeed * 1.4, conn.Walk.Speed, 6);
        Assert.Equal(40, conn.Pools.FireResistance);

        // Quicksilver's 6 s are up, Ruby's 8 s are not.
        var ended = UtilityFlasks.EndDue(conn, 6, dead: false, gone: null, () => Recompute(conn), out bool stop);
        Assert.Equal(1u, Assert.Single(ended).ItemId);
        Assert.False(stop);
        Assert.Equal(Program.WalkSpeed, conn.Walk.Speed, 6);
        Assert.Equal(40, conn.Pools.FireResistance);

        // Death ends the rest.
        ended = UtilityFlasks.EndDue(conn, 6.5, dead: true, gone: null, () => Recompute(conn), out stop);
        Assert.Equal(2u, Assert.Single(ended).ItemId);
        Assert.True(stop);
        Assert.Equal(0, conn.Pools.FireResistance);
        // Nothing due: nothing is recomputed.
        Assert.Empty(UtilityFlasks.EndDue(conn, 7, dead: false, gone: null, () => throw new InvalidOperationException(), out _));
    }

    [Fact]
    public void A_pause_holds_an_effect_and_its_end_comes_after_the_resume()
    {
        var conn = Connection();
        var quicksilver = Utility(("base_movement_velocity_+%", 40)) with { Seconds = 0.5 };
        conn.Clock.Restart();
        Thread.Sleep(50);
        double started = conn.Clock.Elapsed.TotalSeconds, ends = started + quicksilver.Seconds;
        UtilityFlasks.Start(conn, Active(1, quicksilver, started), () => Recompute(conn));
        Thread.Sleep(50);

        // Paused for longer than the whole effect: the connection clock stands still, so it does not end.
        conn.Clock.Stop();
        double pausedAt = conn.Clock.Elapsed.TotalSeconds;
        Assert.True(pausedAt > started && pausedAt < ends);
        Thread.Sleep(700);
        Assert.Equal(pausedAt, conn.Clock.Elapsed.TotalSeconds);
        Assert.Empty(UtilityFlasks.EndDue(conn, conn.Clock.Elapsed.TotalSeconds, false, null, () => Recompute(conn), out _));
        Assert.Equal(Program.WalkSpeed * 1.4, conn.Walk.Speed, 6);

        // Resumed: the rest of it runs out on the clock, after the resume.
        conn.Clock.Start();
        var wall = System.Diagnostics.Stopwatch.StartNew();
        while (conn.Clock.Elapsed.TotalSeconds < ends && wall.Elapsed.TotalSeconds < 10) Thread.Sleep(10);
        Assert.True(wall.Elapsed.TotalSeconds >= ends - pausedAt - 0.05);   // the rest, not the whole, after the resume
        Assert.Single(UtilityFlasks.EndDue(conn, conn.Clock.Elapsed.TotalSeconds, false, null, () => Recompute(conn), out _));
        Assert.Equal(Program.WalkSpeed, conn.Walk.Speed, 6);
    }

    [Fact]
    public void The_silver_flask_puts_and_takes_its_buff_and_onslaught()
    {
        var silver = Of("Silver Flask")!;
        var effect = new UtilityFlasks.Active(9, 3, "Silver Flask", silver, 5, 6, Sources(silver), 10, 16);

        var start = UtilityFlasks.StartPackets(effect, 0x1000).Select(Convert.ToHexString).ToList();
        // flask_utility_haste (row 130): flask-bar cell 3 as source skill, f32 6.0 (0x40C00000) at +0x68.
        Assert.Equal("01C1" + "00001000" + "00000000" + "0000" + "0005" + "0082" + "00001000" + "0003" + "02" + "40C00000" + "00000000", start[0]);
        // onslaught (row 215): no cell (shown in the buff row), the same duration.
        Assert.Equal("01C1" + "00001000" + "00000000" + "0000" + "0006" + "00D7" + "00001000" + "0000" + "02" + "40C00000" + "00000000", start[1]);
        Assert.Equal(2, start.Count);

        Assert.Equal(["01C2" + "00001000" + "00000000" + "0000" + "0005", "01C2" + "00001000" + "00000000" + "0000" + "0006"],
            UtilityFlasks.EndPackets(effect, 0x1000).Select(Convert.ToHexString));
        // A flask without an extra buff sends one of each.
        var quicksilver = Of("Quicksilver Flask")!;
        var single = new UtilityFlasks.Active(1, 2, "Quicksilver Flask", quicksilver, 7, null, Sources(quicksilver), 0, 6);
        Assert.Single(UtilityFlasks.StartPackets(single, 0x1000));
        Assert.Single(UtilityFlasks.EndPackets(single, 0x1000));
    }
}
