using GameServer.Core.World;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

/// <summary>Replica Alberon's Warpath: added attack damage per complete group of final attributes, and "Cannot deal
/// non-Chaos Damage", through the snapshot the Offence panel publishes and the hit the default attack swings with.</summary>
public sealed class AttackAddedPerAttributeTests : IDisposable
{
    // A fresh character in its own temporary save database: nothing equipped, so the unarmed default attack.
    readonly string _save = Path.Combine(Path.GetTempPath(), $"localexile-alberon-{Guid.NewGuid():N}.db");

    public AttackAddedPerAttributeTests() => Saves.Open(_save);

    public void Dispose()
    {
        Saves.Db.Dispose();
        foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(_save + suffix);
    }

    static ClientState Connection() => new(new(1, "alberon", 300, 300, 100, 100, null, []), 1, 1)
    {
        Character = $"alberon-{Guid.NewGuid():N}", PlayerLife = 100, PlayerMana = 80,
        LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
    };

    static uint Key(string name) => Program.Stats.Key(name) ?? throw new InvalidOperationException($"no stat {name}");

    static void Set(ClientState conn, params (string Name, int Value)[] stats)
    {
        foreach (var (name, value) in stats) conn.PlayerStats[Key(name)] = value;
    }

    static (Dictionary<uint, int> Panel, DamagePacket Hit) Attack(ClientState conn)
    {
        var stats = Game.CharacterPlayerStats(conn);
        var hit = Game.AttackHitConverted(Game.WeaponFor(conn), stats, Game.MeleeAttack(conn),
            Game.ConversionFor(conn, -1, 1, attack: true));
        return (stats, hit);
    }

    static int Row(Dictionary<uint, int> panel, string name) => panel.GetValueOrDefault(Key(name));

    [Fact]
    public void Added_attack_damage_per_attribute_counts_complete_groups_on_the_panel_and_in_the_hit()
    {
        var conn = Connection();
        // The pair is named per 50 Strength; the client's description, PoB and RePoE apply it per 80.
        Set(conn, ("strength", 11_833), ("minimum_added_chaos_damage_to_attacks_per_50_strength", 1),
            ("maximum_added_chaos_damage_to_attacks_per_50_strength", 80),
            ("minimum_added_fire_damage_to_attacks_per_10_strength", 1), ("maximum_added_fire_damage_to_attacks_per_10_strength", 2),
            ("dexterity", 39), ("minimum_added_cold_damage_to_attacks_per_10_dexterity", 1),
            ("maximum_added_cold_damage_to_attacks_per_10_dexterity", 2));
        var (panel, hit) = Attack(conn);
        Assert.Equal(147, Row(panel, "main_hand_minimum_chaos_damage"));      // floor(11833 / 80) = 147 groups
        Assert.Equal(11_760, Row(panel, "main_hand_maximum_chaos_damage"));
        Assert.Equal(1_183, Row(panel, "main_hand_minimum_fire_damage"));     // floor(11833 / 10)
        Assert.Equal(2_366, Row(panel, "main_hand_maximum_fire_damage"));
        Assert.Equal(3, Row(panel, "main_hand_minimum_cold_damage"));         // floor(39 / 10)
        Assert.Equal(6, Row(panel, "main_hand_maximum_cold_damage"));
        Assert.Equal(new DamageRange(147, 11_760), hit[DamageType.Chaos]); // the hit the server rolls agrees
        Assert.Equal(new DamageRange(1_183, 2_366), hit[DamageType.Fire]);

        // One Strength short of a group grants nothing more; increases scale the added range like any added damage.
        Set(conn, ("strength", 159), ("chaos_damage_+%", 50));
        (panel, hit) = Attack(conn);
        Assert.Equal(new DamageRange(1.5, 120), hit[DamageType.Chaos]);
        Assert.Equal(1, Row(panel, "main_hand_minimum_chaos_damage"));
        Assert.Equal(120, Row(panel, "main_hand_maximum_chaos_damage"));
        Set(conn, ("strength", 79));
        (panel, hit) = Attack(conn);
        Assert.Equal(0, Row(panel, "main_hand_maximum_chaos_damage"));
        Assert.Equal(0, hit[DamageType.Chaos].Max);
    }

    [Fact]
    public void Damage_type_prohibitions_apply_to_the_hit_its_ailments_and_damage_over_time()
    {
        var conn = Connection();
        Set(conn, ("strength", 800), ("minimum_added_chaos_damage_to_attacks_per_50_strength", 1),
            ("maximum_added_chaos_damage_to_attacks_per_50_strength", 80),
            ("attack_minimum_added_fire_damage", 10), ("attack_maximum_added_fire_damage", 20));
        var (panel, hit) = Attack(conn);
        Assert.True(Row(panel, "main_hand_maximum_physical_damage") > 0);
        Assert.True(Row(panel, "maximum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage") > 0);
        Assert.True(Row(panel, "maximum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage") > 0);
        int poisonWithPhysical = Row(panel, "maximum_poison_damage_to_deal_per_minute_from_non_critical_main_hand_damage");

        Set(conn, ("deal_no_non_chaos_damage", 1));
        (panel, hit) = Attack(conn);
        Assert.Equal([DamageType.Chaos], hit.Components.Where(c => c.Range.Max > 0).Select(c => c.Type));
        Assert.Equal(new DamageRange(10, 800), hit[DamageType.Chaos]);
        foreach (string type in new[] { "physical", "fire", "cold", "lightning" })
            Assert.Equal(0, Row(panel, $"main_hand_maximum_{type}_damage"));
        Assert.Equal(10, Row(panel, "main_hand_minimum_total_damage"));
        Assert.Equal(800, Row(panel, "main_hand_maximum_total_damage"));
        Assert.Equal(0, Row(panel, "maximum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage"));
        Assert.Equal(0, Row(panel, "maximum_ignite_damage_to_deal_per_minute_from_critical_main_hand_damage"));
        // Poison keeps its chaos base only: 30% of 800 per second, per minute.
        int poison = Row(panel, "maximum_poison_damage_to_deal_per_minute_from_non_critical_main_hand_damage");
        Assert.Equal(14_400, poison);
        Assert.True(poison < poisonWithPhysical);
        // A fire damage over time from the same caster (Righteous Fire's burn, an ignite) deals nothing; chaos does.
        var context = new SkillDamageContext(SkillHitKind.Spell, panel);
        Assert.Equal(0, context.OverTime(new DamageOverTime(DamageType.Fire, 100, 1)).DamagePerSecond);
        Assert.Equal(100, context.OverTime(new DamageOverTime(DamageType.Chaos, 100, 1)).DamagePerSecond);

        // Each damaging ailment is gated on its own type: "Deal no Chaos Damage" stops poison even from physical.
        Set(conn, ("deal_no_non_chaos_damage", 0), ("base_deal_no_chaos_damage", 1));
        (panel, hit) = Attack(conn);
        Assert.Equal(0, hit[DamageType.Chaos].Max);
        Assert.True(Row(panel, "maximum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage") > 0);
        Assert.Equal(0, Row(panel, "maximum_poison_damage_to_deal_per_minute_from_non_critical_main_hand_damage"));
        // "Attacks deal no Physical Damage" needs the attack context: the attack loses physical and bleed,
        // a spell's physical damage over time does not.
        Set(conn, ("base_deal_no_chaos_damage", 0), ("attacks_deal_no_physical_damage", 1));
        (panel, hit) = Attack(conn);
        Assert.Equal(0, hit[DamageType.Physical].Max);
        Assert.Equal(0, Row(panel, "maximum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage"));
        Assert.Equal(14_400, Row(panel, "maximum_poison_damage_to_deal_per_minute_from_non_critical_main_hand_damage"));
        Assert.Equal(100, new SkillDamageContext(SkillHitKind.Spell, panel)
            .OverTime(new DamageOverTime(DamageType.Physical, 100, 1)).DamagePerSecond);
        Assert.Equal(0, new SkillDamageContext(SkillHitKind.Attack, panel)
            .OverTime(new DamageOverTime(DamageType.Physical, 100, 1)).DamagePerSecond);
    }
}
