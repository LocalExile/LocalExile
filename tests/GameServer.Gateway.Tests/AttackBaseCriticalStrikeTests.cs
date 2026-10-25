using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The attack's base critical strike chance override (issue #211 foundation): the raw stats
/// <c>override_weapon_base_critical_strike_chance</c> (Slayer Overwhelm 800, Blind Prophet's Sightless 900, permyriad)
/// replace the armed main hand's WeaponTypes base (with its local mods) before the global increases — Path of
/// Building's CalcOffence.lua replaces the attack pass's <c>source.CritChance</c> with the WeaponBaseCritChance
/// override only when that hand's type is not "None" (unarmed) — and the unarmed base
/// <c>base_melee_critical_strike_chance_while_unarmed_%</c> (Behemoth Final Blow 1000 = 10%) does the same with the
/// empty weapon base. The pool mirrors <c>PoolsFor</c>'s line; the spell crit route keeps its own extracted bases.
/// </summary>
public class AttackBaseCriticalStrikeTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<(PassiveTree Tree, ItemCatalog Catalog, StatCatalog Stats)> Setup =
        new(() => (new PassiveTree(Data()), new ItemCatalog(Data()), new StatCatalog(Data())));

    static ItemCatalog Catalog => Setup.Value.Catalog;

    /// <summary>PoolsFor's composition: the raw base (override or weapon) under the global increases, permyriad out.</summary>
    static int PoolChance(bool weaponInMainHand, int weaponBase, int increasedPct, Func<string, int> raw) =>
        (int)Math.Round(DamageModel.CriticalStrikeChance(
            AttackBaseCriticalStrike.OfMainHand(weaponInMainHand, weaponBase, raw), increasedPct) * 100);

    static Func<string, int> Raw(params (string Name, int Value)[] stats) => name =>
    {
        var found = stats.FirstOrDefault(s => s.Name == name);
        return found.Name == name ? found.Value : 0;
    };

    static string OverrideStat => AttackBaseCriticalStrike.WeaponOverrideStat;
    static string UnarmedStat => AttackBaseCriticalStrike.UnarmedBaseStat;

    [Theory]
    [InlineData("global_cannot_crit")]
    [InlineData("virtual_global_cannot_crit")]
    [InlineData("cannot_critical_strike_with_attacks")]
    public void Attack_crit_prohibitions_win_over_both_base_grants(string prohibition)
    {
        var raw = Raw((OverrideStat, 900), (UnarmedStat, 1000), (prohibition, 1));
        Assert.Equal(0, AttackBaseCriticalStrike.Chance(true, 830, 100, raw));
        Assert.Equal(0, AttackBaseCriticalStrike.Chance(false, 0, 100, raw));
    }

    [Theory]
    [InlineData(500, 1, 505)]
    [InlineData(850, 1, 859)]
    [InlineData(900, -100, 0)]
    [InlineData(900, 2000, 10000)]
    public void Final_chance_rounds_permyriad_once_and_clamps(int baseChance, int increased, int expected)
        => Assert.Equal(expected, AttackBaseCriticalStrike.Chance(true, 830, increased, Raw((OverrideStat, baseChance))));

    // --- The raw stats in the data (Agents/docs/protocol/11-skills.md §crit) ---

    [Fact]
    public void Slayer_Overwhelm_grants_the_weapon_base_override_800()
    {
        var (tree, _, stats) = Setup.Value;
        var node = tree.Find((ushort)17315);
        Assert.NotNull(node);
        Assert.Equal("Overwhelm", node.Name);
        Assert.Equal(800, tree.Stats([17315])[stats.Key(OverrideStat)!.Value]);
    }

    [Fact]
    public void Blind_Prophets_Sightless_grants_the_weapon_base_override_900()
    {
        var (tree, _, stats) = Setup.Value;
        var node = tree.Find(24698);
        Assert.NotNull(node);
        Assert.Equal("Sightless", node.Name);
        Assert.Equal(900, tree.Stats([24698])[stats.Key(OverrideStat)!.Value]);
    }

    [Fact]
    public void Behemoth_Final_Blow_grants_the_unarmed_base_1000()
    {
        var (tree, _, stats) = Setup.Value;
        var node = tree.Find(10135);
        Assert.NotNull(node);
        Assert.Equal("Final Blow", node.Name);
        Assert.Equal(1000, tree.Stats([10135])[stats.Key(UnarmedStat)!.Value]);
    }

    // --- The override replaces the armed weapon's base (PoolsFor's armed branch) ---

    [Theory]
    [InlineData(800)] // Slayer Overwhelm: below the wand's own 8.30% and still wins.
    [InlineData(900)] // Blind Prophet's Sightless.
    [InlineData(500)] // Any present value replaces, even a lower one.
    public void An_armed_weapon_uses_the_override_instead_of_its_own_base(int permyriad)
    {
        var wand = Catalog.Row(384);
        Assert.NotNull(wand);
        Assert.Equal(830, Catalog.WeaponCritical(wand));
        Assert.Equal(permyriad, PoolChance(weaponInMainHand: true, 830, 0, Raw((OverrideStat, permyriad))));
    }

    [Fact]
    public void Without_the_override_the_armed_weapon_keeps_its_base()
    {
        // A refunded tree removes the node's the key from the raw stats, and the weapon base is back.
        Assert.Equal(830, PoolChance(weaponInMainHand: true, 830, 0, Raw()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-300)]
    public void An_override_at_or_below_zero_is_not_present(int permyriad)
    {
        Assert.Equal(830, PoolChance(weaponInMainHand: true, 830, 0, Raw((OverrideStat, permyriad))));
    }

    [Fact]
    public void The_global_increases_keep_applying_on_top_of_the_override()
    {
        // PoB CalcOffence.lua: the pass's base is the override, then (baseCrit + base) × (1 + inc/100).
        // 8.00% with +100% increased arrives at 16.00% (permyriad 1600), like the spell route's known base.
        Assert.Equal(1600, PoolChance(weaponInMainHand: true, 830, 100, Raw((OverrideStat, 800))));
        Assert.Equal(800, PoolChance(weaponInMainHand: true, 830, 0, Raw((OverrideStat, 800))));
    }

    // --- The weapon's local mods stay on the weapon path, not on the override ---

    [Fact]
    public void A_local_weapon_crit_mod_scales_the_weapon_base_it_replaces()
    {
        var baseRow = Catalog.Row(384);
        Assert.NotNull(baseRow);
        var stats = ItemModStats.Local(
            new(1, ["Base", "Mods"], 0)
            {
                Mods = [Catalog.Mods.ById("LocalCriticalStrikeChance1")!.With(ModSection.Explicit, [14], Random.Shared)],
            }, Catalog.Mods);
        int weaponBase = Catalog.WeaponCritical(baseRow) * (100 + stats["local_critical_strike_chance_+%"]) / 100;
        // PoolsFor: WeaponProfileFor.Critical = WeaponCritical(main) × (100 + local) / 100 — here 9.46%.
        Assert.Equal(946, weaponBase);
        Assert.Equal(946, PoolChance(weaponInMainHand: true, weaponBase, 0, Raw()));
        // The override replaces that raised base wholesale; the local mod does not scale it again.
        Assert.Equal(900, PoolChance(weaponInMainHand: true, weaponBase, 0, Raw((OverrideStat, 900))));
    }

    // --- The unarmed context: its own unrelated base, never the weapon override ---

    [Fact]
    public void Unarmed_uses_its_own_base_instead_of_the_weapon_override()
    {
        // PoB's guard (source.type ~= "None"): unarmed with both raw stats present stays on the unarmed base.
        Assert.Equal(1000, PoolChance(weaponInMainHand: false, 0, 0, Raw((OverrideStat, 800), (UnarmedStat, 1000))));
    }

    [Fact]
    public void The_unarmed_base_is_absent_without_its_stat()
    {
        Assert.Equal(0, PoolChance(weaponInMainHand: false, 0, 0, Raw((OverrideStat, 800))));
        Assert.Equal(0, PoolChance(weaponInMainHand: false, 0, 0, Raw()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-300)]
    public void An_unarmed_base_at_or_below_zero_stays_at_zero(int permyriad)
    {
        Assert.Equal(0, PoolChance(weaponInMainHand: false, 0, 0, Raw((UnarmedStat, permyriad))));
    }

    [Fact]
    public void The_armed_context_never_takes_the_unarmed_base()
    {
        Assert.Equal(830, PoolChance(weaponInMainHand: true, 830, 0, Raw((UnarmedStat, 1000))));
    }

    // --- The spells keep their own route ---

    [Fact]
    public void The_spell_crit_pool_ignores_the_raw_bases()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(CriticalStrikeChancePct: 100, SpellCriticalStrikeChancePct: 50));

        // The spell chance stays at Fireball's extracted 5% under +50%: the pure base stats never reach it
        // (SpellCriticalStrike keeps SpellBaseCriticalStrikeChance out of this stat family).
        Assert.Equal(750, pools.SpellCriticalStrikeChance);
    }
}
