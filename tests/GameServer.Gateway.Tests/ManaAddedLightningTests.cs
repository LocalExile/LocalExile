using GameServer.Core.World;
using DamageType = GameServer.Core.World.DamageType;

namespace GameServer.Gateway.Tests;

// Task #215: the Whisperer ascendancy notable "we know all" (PassiveSkills.dat graph id 22720) grants
// attack_skills_have_added_lightning_damage_equal_to_%_of_maximum_mana (Stats.dat row 11881, client key 11882;
// the node's other stat, base_mana_cost_-%, is a mana-cost change handled elsewhere). Path of Building pinned
// 16de4b82: ModParser.lua lines 5636–5639 map the value to BOTH LightningMin and LightningMax as BASE with PercentStat Mana and the
// Attack flag, so the packet is added lightning of min = max = percent% of the maximum mana pool (invariance
// under spending/reservation, unlike PoE1 logic built on the current pool). Here: max mana 1000, 6% → 60-60,
// not 1-60. The bonus joins the added-lightning range before the increases, so increased/decreased damage and
// later conversion scale it like any other added lightning, and only the client-visible rows round it.
public class ManaAddedLightningTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static Func<string, int> Reader(StatCatalog catalog, Dictionary<uint, int> stats) =>
        name => catalog.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

    static Dictionary<uint, int> With(StatCatalog catalog, (string Name, int Value) tag, int mana)
    {
        var stats = new Dictionary<uint, int> { [catalog.Key("maximum_mana")!.Value] = mana };
        if (tag.Value != 0) stats[catalog.Key(tag.Name)!.Value] = tag.Value;
        return stats;
    }

    [Fact]
    public void The_attack_hit_has_added_lightning_min_and_max_equal_to_percent_of_maximum_mana()
    {
        var catalog = new StatCatalog(Data());
        var get = Reader(catalog, With(catalog, (DamageStats.MaxManaAddedLightning, 6), 1000));

        var packet = DamageStats.Added(DamagePacket.Of(DamageType.Physical, 10, 20), get, spell: false);

        Assert.Equal(10, packet[DamageType.Physical].Min);
        Assert.Equal(20, packet[DamageType.Physical].Max);
        // 6% of 1000 hits both bounds: 60-60, not 1-60.
        Assert.Equal(60, packet[DamageType.Lightning].Min);
        Assert.Equal(60, packet[DamageType.Lightning].Max);
        Assert.Equal(0, packet[DamageType.Fire].Min);
        Assert.Equal(0, packet[DamageType.Cold].Min);
        Assert.Equal(0, packet[DamageType.Chaos].Min);
    }

    [Fact]
    public void Increased_and_decreased_damage_scale_the_pool_bonus_like_other_added_lightning()
    {
        var catalog = new StatCatalog(Data());
        var stats = With(catalog, (DamageStats.MaxManaAddedLightning, 6), 1000);
        // increased damage and increased melee: both attack-increase stats must scale the bonus
        stats[catalog.Key("damage_+%")!.Value] = 30;
        stats[catalog.Key("melee_damage_+%")!.Value] = 20;

        var packet = DamageStats.Added(DamagePacket.Of(DamageType.Physical, 100, 100), Reader(catalog, stats), spell: false);

        // The added-lightning bonus scales like any added range here; the weapon base's increases are
        // applied in HitDamage instead (Added only adds to the packet).
        Assert.Equal(60.0 * 1.5, packet[DamageType.Lightning].Min);
        Assert.Equal(60.0 * 1.5, packet[DamageType.Lightning].Max);
        Assert.Equal(100, packet[DamageType.Physical].Min);
    }

    [Fact]
    public void Increased_lightning_or_elemental_only_scales_the_bonus_the_same_way()
    {
        var catalog = new StatCatalog(Data());
        var stats = With(catalog, (DamageStats.MaxManaAddedLightning, 6), 1000);
        stats[catalog.Key("lightning_damage_+%")!.Value] = 50;

        var packet = DamageStats.Added(DamagePacket.Empty, Reader(catalog, stats), spell: false);

        Assert.Equal(90, packet[DamageType.Lightning].Min);
        Assert.Equal(90, packet[DamageType.Lightning].Max);
    }

    [Fact]
    public void Conversion_treats_the_pool_bonus_as_lightning()
    {
        var catalog = new StatCatalog(Data());
        var stats = With(catalog, (DamageStats.MaxManaAddedLightning, 6), 1000);
        stats[catalog.Key("lightning_damage_%_to_convert_to_cold")!.Value] = 50;

        var packet = DamageStats.Added(DamagePacket.Empty, Reader(catalog, stats), spell: false)
            .Convert(DamageStats.Conversion(stats, catalog, attack: true));

        Assert.Equal(30, packet[DamageType.Lightning].Min);
        Assert.Equal(30, packet[DamageType.Cold].Min);
        Assert.Equal(60, packet.MinTotal);
    }

    [Fact]
    public void Spell_hits_do_not_consume_the_stat()
    {
        var catalog = new StatCatalog(Data());
        var get = Reader(catalog, With(catalog, (DamageStats.MaxManaAddedLightning, 6), 1000));

        var attack = DamageStats.Added(DamagePacket.Empty, get, spell: false);
        var spell = DamageStats.Added(DamagePacket.Empty, get, spell: true);

        Assert.Equal(60, attack[DamageType.Lightning].Min);
        Assert.Equal(0, spell[DamageType.Lightning].Min);
        Assert.Equal(0, spell[DamageType.Lightning].Max);
    }

    [Fact]
    public void A_zero_stat_adds_nothing()
    {
        var catalog = new StatCatalog(Data());
        var get = Reader(catalog, With(catalog, (DamageStats.MaxManaAddedLightning, 0), 1000));

        var packet = DamageStats.Added(DamagePacket.Empty, get, spell: false);

        Assert.True(packet[DamageType.Lightning].IsZero);
    }

    [Fact]
    public void A_negative_stat_improves_nothing_beyond_the_non_negative_clamp()
    {
        var catalog = new StatCatalog(Data());
        var get = Reader(catalog, With(catalog, (DamageStats.MaxManaAddedLightning, -6), 1000));

        var packet = DamageStats.Added(DamagePacket.Empty, get, spell: false);

        // A negative range clamps to zero where the packet is assembled, as any added-damage range does.
        Assert.True(packet[DamageType.Lightning].IsZero);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public void A_non_positive_maximum_mana_adds_nothing(int maxMana)
    {
        var catalog = new StatCatalog(Data());
        var get = Reader(catalog, With(catalog, (DamageStats.MaxManaAddedLightning, 6), maxMana));

        var packet = DamageStats.Added(DamagePacket.Empty, get, spell: false);

        Assert.True(packet[DamageType.Lightning].IsZero);
    }

    [Fact]
    public void The_bonus_reads_the_maximum_pool_so_spending_and_reserving_never_change_it()
    {
        var catalog = new StatCatalog(Data());
        var stats = With(catalog, (DamageStats.MaxManaAddedLightning, 6), 1000);
        var before = DamageStats.Added(DamagePacket.Empty, Reader(catalog, stats), spell: false);

        // Spending mana is not in this dictionary at all: the packet is built from the character's resolved stats,
        // where "maximum_mana" is the full pool (ClassAttributes) and the current mana lives outside it on the
        // connection, so a spend or a reserved aura cannot appear here. Changing the pool — a level, an equipment,
        // a passive change — is what actually changes the bonus.
        stats[catalog.Key("maximum_mana")!.Value] = 2000;
        var after = DamageStats.Added(DamagePacket.Empty, Reader(catalog, stats), spell: false);

        Assert.Equal(60, before[DamageType.Lightning].Min);
        Assert.Equal(120, after[DamageType.Lightning].Min);
    }

    [Fact]
    public void The_damage_effectiveness_still_scales_the_pool_bonus_later()
    {
        var catalog = new StatCatalog(Data());
        var get = Reader(catalog, With(catalog, (DamageStats.MaxManaAddedLightning, 6), 1000));

        var packet = DamageStats.Added(DamagePacket.Empty, get, spell: false, effectiveness: 1.5);

        Assert.Equal(90, packet[DamageType.Lightning].Min);
        Assert.Equal(90, packet[DamageType.Lightning].Max);
    }

    [Fact]
    public void The_fraction_stays_until_the_existing_roll_and_panel_stages()
    {
        var catalog = new StatCatalog(Data());
        var get = Reader(catalog, With(catalog, (DamageStats.MaxManaAddedLightning, 6), 997));

        var packet = DamageStats.Added(DamagePacket.Empty, get, spell: false);
        Assert.Equal(997.0 * 0.06, packet[DamageType.Lightning].Min);
        Assert.Equal(997.0 * 0.06, packet[DamageType.Lightning].Max);

        // The panel rounds a whole range down the way it rounds any range; a roll floors at the existing stage too.
        var rolled = packet.RollTotal(new Random(206));
        Assert.Equal((int)Math.Floor(997.0 * 0.06), rolled);
    }

    [Fact]
    public void The_panel_rows_show_the_pool_bonus()
    {
        var catalog = new StatCatalog(Data());
        var get = Reader(catalog, With(catalog, (DamageStats.MaxManaAddedLightning, 6), 1000));

        var packet = DamageStats.Added(DamagePacket.Of(DamageType.Physical, 10, 20), get, spell: false);
        var rows = PanelStats.HandDamage(packet, "main_hand");

        Assert.Contains(("main_hand_minimum_lightning_damage", 60), rows);
        Assert.Contains(("main_hand_maximum_lightning_damage", 60), rows);
        Assert.Contains(("main_hand_minimum_total_damage", 70), rows);
        Assert.Contains(("main_hand_maximum_total_damage", 80), rows);
    }

    [Fact]
    public void The_shared_attack_packet_constructor_applies_the_stat()
    {
        // Program.Stats is resolved to an absolute data folder (GameDataFiles.ResolveFolder), like the server's own
        // use; this runs the same AttackHit path the panel and every attack caller share.
        var stats = new Dictionary<uint, int>
        {
            [Program.Stats.Key("maximum_mana")!.Value] = 1000,
            [Program.Stats.Key(DamageStats.MaxManaAddedLightning)!.Value] = 6,
        };

        var hit = Program.AttackHit(new SkillCatalog.Unarmed(833, 2, 5, 8), stats);

        Assert.Equal(5, hit[DamageType.Physical].Max);
        Assert.Equal(60, hit[DamageType.Lightning].Min);
        Assert.Equal(60, hit[DamageType.Lightning].Max);
    }

    [Fact]
    public void The_game_data_folder_resolves_above_the_current_directory()
    {
        string folder = GameDataFiles.ResolveFolder();

        Assert.True(Path.IsPathRooted(folder));
        Assert.True(File.Exists(Path.Combine(folder, "tables", "Stats.json")));
    }

    [Fact]
    public void The_data_grid_grants_the_stat_with_the_value_six()
    {
        var catalog = new StatCatalog(Data());
        // Stats row 11881 + 1 == the client key 11882.
        Assert.Equal(11882u, catalog.Key(DamageStats.MaxManaAddedLightning));

        var tree = new PassiveTree(Data());
        var node = tree.Find(22720);
        Assert.NotNull(node);
        Assert.Equal("we know all", node.Name);
        // The damage stat itself; the node's base_mana_cost_-% drawback is another change's scope.
        Assert.Contains(((uint)11882, 6), node.Stats);
    }
}
