using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>Issue #1102: the tree's conditional stats and the keystones that ride on the crit policy.</summary>
public sealed class PassiveConditionTests
{
    [Fact]
    public void A_conditional_passive_stat_counts_only_while_its_condition_holds()
    {
        uint conditional = Program.Stats.Key("physical_damage_while_dual_wielding_+%")!.Value;
        uint plain = Program.Stats.Key("physical_damage_+%")!.Value;
        var passives = new Dictionary<uint, int> { [conditional] = 25 };

        // Dual wielding: the stat moves onto the plain name every consumer already reads.
        var holding = Game.FoldPassiveConditions(passives, new StatConditions.Subject(DualWield: true));
        Assert.Equal(25, holding.GetValueOrDefault(plain));
        Assert.False(holding.ContainsKey(conditional));

        // One weapon only: the stat does not count at all.
        Assert.Empty(Game.FoldPassiveConditions(passives, new StatConditions.Subject(DualWield: false)));

        // A condition this build cannot answer keeps the stat as it was, never dropping or moving it.
        var unknown = Game.FoldPassiveConditions(passives, StatConditions.Subject.Unknown);
        Assert.Equal(25, unknown.GetValueOrDefault(conditional));

        // The Staff Mastery's "increased Defences while wielding a Staff": the tree's plain name is defences_+%, which
        // is not a stat; it folds onto global_defences_+%, which the armour, evasion, energy shield and ward pools read.
        uint staffDefences = Program.Stats.Key("defences_+%_while_wielding_staff")!.Value;
        uint globalDefences = Program.Stats.Key("global_defences_+%")!.Value;
        var staff = new Dictionary<uint, int> { [staffDefences] = 30 };
        var wielding = Game.FoldPassiveConditions(staff, new StatConditions.Subject(
            Flags: new Dictionary<string, bool> { ["_while_wielding_staff"] = true }));
        Assert.Equal(30, wielding.GetValueOrDefault(globalDefences));
        Assert.Empty(Game.FoldPassiveConditions(staff, new StatConditions.Subject(
            Flags: new Dictionary<string, bool> { ["_while_wielding_staff"] = false })));
    }

    [Fact]
    public void Keystones_never_crit_and_the_tree_s_weapon_rows_reach_the_hit()
    {
        using var f = new MinionUtilityTests.Fixture();
        var resolute = new Dictionary<uint, int> { [Program.Stats.Key("resolute_technique")!.Value] = 1 };
        Assert.True(Game.CriticalProhibitedBy(resolute, attack: true, own: null));
        Assert.False(Game.CriticalProhibitedBy(resolute, attack: false, own: null));
        Assert.True(AttackAccuracyProfile.From(f.Conn.Pools, resolute, Program.Stats, own: null, local: null)
            .CannotBeEvaded);

        var precise = new Dictionary<uint, int> { [Program.Stats.Key("keystone_precise_technique")!.Value] = 1 };
        Assert.True(Game.CriticalProhibitedBy(precise, attack: true, own: null));

        foreach (string keystone in new[] { "keystone_elemental_overload", "keystone_ailment_crit" })
            Assert.True(Game.CriticalHitsDealNoExtraDamage(
                new Dictionary<uint, int> { [Program.Stats.Key(keystone)!.Value] = 1 }));
        Assert.False(Game.CriticalHitsDealNoExtraDamage(new Dictionary<uint, int>()));

        // The tree's "with <weapon type>" rows (issue #1102, batch 1): the plain rows join the common bucket, the
        // physical rows (including two-handed melee) and the per-element rows their own.
        var stats = new Dictionary<uint, int>
        {
            [Program.Stats.Key("mace_damage_+%")!.Value] = 30,
            [Program.Stats.Key("physical_mace_damage_+%")!.Value] = 20,
            [Program.Stats.Key("two_handed_melee_physical_damage_+%")!.Value] = 40,
            [Program.Stats.Key("fire_mace_damage_+%")!.Value] = 15,
            [Program.Stats.Key("sword_damage_+%")!.Value] = 99,
            [Program.Stats.Key("two_handed_weapon_hit_and_ailment_damage_+%")!.Value] = 60,
            [Program.Stats.Key("damage_+%_with_one_handed_weapons")!.Value] = 25,
        };
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var twoHandMace = new SkillCatalog.Unarmed(1000, 10, 20, 10) { ItemClass = "Two Hand Mace" };
        Assert.Equal(90, SupportedAttackDamage.WeaponIncrease(twoHandMace, Get));
        var increases = WeaponTypeDamage.Of(twoHandMace, Get);
        Assert.Equal(60, increases.Physical);
        Assert.Equal(15, Assert.Single(increases.Typed!).Increased);
        // A different family reads its own row and the one-handed rows, not the mace's or the two-handed ones.
        Assert.Equal(124, SupportedAttackDamage.WeaponIncrease(
            new SkillCatalog.Unarmed(1000, 10, 20, 10) { ItemClass = "One Hand Sword" }, Get));

        // The same rows reach a damaging ailment: the family's ailment row and the two-handed melee ailment row join
        // the common bucket, its physical row the physical one. 100 physical * 70% bleed * 60 s * 1.75.
        var ailmentStats = new Dictionary<uint, int>
        {
            [Program.Stats.Key("main_hand_weapon_type")!.Value] = 4,   // two hand mace
            [Program.Stats.Key("mace_ailment_damage_+%")!.Value] = 25,
            [Program.Stats.Key("two_handed_melee_weapon_ailment_damage_+%")!.Value] = 40,
            [Program.Stats.Key("physical_mace_damage_+%")!.Value] = 10,
        };
        int AilmentGet(string name) => Program.Stats.Key(name) is uint key ? ailmentStats.GetValueOrDefault(key) : 0;
        var bleed = AilmentPanelStats.Compute(new AilmentPanelStats.Context("main_hand", false, true),
                DamagePacket.Of(DamageType.Physical, new DamageRange(100, 100)), new DamageConversion(), AilmentGet)
            .ToDictionary(r => r.Stat, r => r.Value);
        Assert.Equal(7350, bleed["maximum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage"]);

        // Unwavering Stance and Arrow Dancing change the rating an enemy attack rolls against, and Unwavering Stance
        // also stops the player's stun.
        int NoKeystones(string name) => 0;
        Assert.Equal(1000, EvasionKeystones.Rating(1000, projectile: true, melee: false, NoKeystones));
        var unwavering = new Dictionary<uint, int> { [Program.Stats.Key("keystone_unwavering_stance")!.Value] = 1 };
        int Unwavering(string name) => Program.Stats.Key(name) is uint key ? unwavering.GetValueOrDefault(key) : 0;
        Assert.Equal(0, EvasionKeystones.Rating(1000, projectile: true, melee: false, Unwavering));
        Assert.Equal(0, PlayerHitRecovery.Chance(100, 1000, Unwavering));
        Assert.True(PlayerHitRecovery.Chance(100, 1000, NoKeystones) > 0);
        var arrowDancing = new Dictionary<uint, int> { [Program.Stats.Key("keystone_projectile_evasion")!.Value] = 1 };
        int Dancing(string name) => Program.Stats.Key(name) is uint key ? arrowDancing.GetValueOrDefault(key) : 0;
        Assert.Equal(2000, EvasionKeystones.Rating(1000, projectile: true, melee: false, Dancing));
        Assert.Equal(750, EvasionKeystones.Rating(1000, projectile: false, melee: true, Dancing));
        Assert.Equal(1000, EvasionKeystones.Rating(1000, projectile: false, melee: false, Dancing));

        // Magebane: Dexterity's inherent evasion bonus is gone (its accuracy bonus is not), and every 15 Dexterity is
        // 1% spell suppression. 300 Dexterity: 15 base evasion stays 15 instead of 24, and 20% suppression.
        var classInfo = Game.SkillsFor(f.Conn).ClassFor(f.Conn.ClassRow);
        var plain = CharacterStats.For(classInfo, 90, new CharacterStats.Bonus(Dexterity: 300));
        var magebane = CharacterStats.For(classInfo, 90, new CharacterStats.Bonus(Dexterity: 300, Magebane: true));
        Assert.Equal(24, plain.Evasion);
        Assert.Equal(15, magebane.Evasion);
        Assert.Equal(0, plain.SpellSuppressionChance);
        Assert.Equal(20, magebane.SpellSuppressionChance);
        Assert.Equal(plain.Accuracy, magebane.Accuracy);

        // Pain Attunement: 30% more Spell Damage on low life, and nothing off it.
        var fire = DamagePacket.Of(DamageType.Fire, new DamageRange(100, 100));
        int SpellGet(string name) => name switch
        {
            "keystone_pain_attunement" => 1,
            "on_low_life" => 1,
            _ => 0,
        };
        Assert.Equal(130, PlayerHitDamage.Resolve(fire, SpellGet, new DamageConversion(), spell: true)[DamageType.Fire].Max);
        int NotLow(string name) => name == "keystone_pain_attunement" ? 1 : 0;
        Assert.Equal(100, PlayerHitDamage.Resolve(fire, NotLow, new DamageConversion(), spell: true)[DamageType.Fire].Max);
    }
}
