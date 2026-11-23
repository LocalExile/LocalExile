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
        var resolute = new Dictionary<uint, int> { [Stat.ResoluteTechnique] = 1 };
        Assert.True(Game.CriticalProhibitedBy(resolute, attack: true, own: null));
        Assert.False(Game.CriticalProhibitedBy(resolute, attack: false, own: null));
        Assert.True(AttackAccuracyProfile.From(f.Player.Pools, resolute, Program.Stats, own: null, local: null)
            .CannotBeEvaded);

        var precise = new Dictionary<uint, int> { [Stat.KeystonePreciseTechnique] = 1 };
        Assert.True(Game.CriticalProhibitedBy(precise, attack: true, own: null));

        foreach (uint keystone in new[] { Stat.KeystoneElementalOverload, Stat.KeystoneAilmentCrit })
            Assert.True(Game.CriticalHitsDealNoExtraDamage(
                new Dictionary<uint, int> { [keystone] = 1 }));
        Assert.False(Game.CriticalHitsDealNoExtraDamage(new Dictionary<uint, int>()));

        // The tree's "with <weapon type>" rows (issue #1102, batch 1): the plain rows join the common bucket, the
        // physical rows (including two-handed melee) and the per-element rows their own.
        var stats = new Dictionary<uint, int>
        {
            [Stat.MaceDamagePlusPct] = 30,
            [Stat.PhysicalMaceDamagePlusPct] = 20,
            [Stat.TwoHandedMeleePhysicalDamagePlusPct] = 40,
            [Stat.FireMaceDamagePlusPct] = 15,
            [Stat.SwordDamagePlusPct] = 99,
            [Stat.TwoHandedWeaponHitAndAilmentDamagePlusPct] = 60,
            [Stat.DamagePlusPctWithOneHandedWeapons] = 25,
        };
        int Get(uint key) => stats.GetValueOrDefault(key);
        var twoHandMace = new SkillCatalog.Unarmed(1000, 10, 20, 10) { ItemClass = "Two Hand Mace" };
        Assert.Equal(90, SupportedAttackDamage.WeaponIncrease(twoHandMace, Get));
        var increases = WeaponTypeDamage.Of(twoHandMace, Get);
        Assert.Equal(60, increases.Physical);
        Assert.Equal(15, Assert.Single(increases.Typed!).Increased);
        // A different family reads its own row and the one-handed rows, not the mace's or the two-handed ones.
        Assert.Equal(124, SupportedAttackDamage.WeaponIncrease(
            new SkillCatalog.Unarmed(1000, 10, 20, 10) { ItemClass = "One Hand Sword" }, Get));
        // A bow is a two-handed weapon (PoB Data.lua weaponTypeInfo): it takes the Two Hand Mastery's row, but not the
        // two-handed melee physical row.
        var bow = new SkillCatalog.Unarmed(1000, 10, 20, 10) { ItemClass = "Bow" };
        Assert.Equal(60, SupportedAttackDamage.WeaponIncrease(bow, Get));
        Assert.Equal(0, WeaponTypeDamage.Of(bow, Get).Physical);

        // The same rows reach a damaging ailment: the family's ailment row and the two-handed melee ailment row join
        // the common bucket, its physical row the physical one. 100 physical * 70% bleed * 60 s * 1.75.
        var ailmentStats = new Dictionary<uint, int>
        {
            [Stat.MainHandWeaponType] = 4,   // two hand mace
            [Stat.MaceAilmentDamagePlusPct] = 25,
            [Stat.TwoHandedMeleeWeaponAilmentDamagePlusPct] = 40,
            [Stat.PhysicalMaceDamagePlusPct] = 10,
        };
        int AilmentGet(uint key) => ailmentStats.GetValueOrDefault(key);
        var bleed = AilmentPanelStats.Compute(new AilmentPanelStats.Context("main_hand", false, true),
                DamagePacket.Of(DamageType.Physical, new DamageRange(100, 100)), new DamageConversion(), AilmentGet)
            .ToDictionary(r => r.Stat, r => r.Value);
        Assert.Equal(7350, bleed[Stat.MaximumBleedingDamageToDealPerMinuteFromNonCriticalMainHandDamage]);

        // Unwavering Stance and Arrow Dancing change the rating an enemy attack rolls against, and Unwavering Stance
        // also stops the player's stun.
        int NoKeystones(uint key) => 0;
        Assert.Equal(1000, EvasionKeystones.Rating(1000, projectile: true, melee: false, NoKeystones));
        var unwavering = new Dictionary<uint, int> { [Stat.KeystoneUnwaveringStance] = 1 };
        int Unwavering(uint key) => unwavering.GetValueOrDefault(key);
        Assert.Equal(0, EvasionKeystones.Rating(1000, projectile: true, melee: false, Unwavering));
        Assert.Equal(0, PlayerHitRecovery.Chance(100, 1000, Unwavering));
        Assert.True(PlayerHitRecovery.Chance(100, 1000, NoKeystones) > 0);
        var arrowDancing = new Dictionary<uint, int> { [Stat.KeystoneProjectileEvasion] = 1 };
        int Dancing(uint key) => arrowDancing.GetValueOrDefault(key);
        Assert.Equal(2000, EvasionKeystones.Rating(1000, projectile: true, melee: false, Dancing));
        Assert.Equal(750, EvasionKeystones.Rating(1000, projectile: false, melee: true, Dancing));
        Assert.Equal(1000, EvasionKeystones.Rating(1000, projectile: false, melee: false, Dancing));

        // Magebane: Dexterity's inherent evasion bonus is gone (its accuracy bonus is not), and every 15 Dexterity is
        // 1% spell suppression. 300 Dexterity: 15 base evasion stays 15 instead of 24, and 20% suppression. The sheet
        // takes generic inputs; Core's PassiveBonus turns the keystone into them.
        var classInfo = Game.SkillsFor(f.Player).ClassFor(f.Player.Character.ClassRow);
        var plain = CharacterStats.For(classInfo, 90, new CharacterStats.Bonus(Dexterity: 300));
        var magebane = CharacterStats.For(classInfo, 90, new CharacterStats.Bonus(Dexterity: 300,
            NoDexterityEvasion: true, SpellSuppressionPerFifteenDexterity: 1));
        Assert.Equal(24, plain.Evasion);
        Assert.Equal(15, magebane.Evasion);
        Assert.Equal(0, plain.SpellSuppressionChance);
        Assert.Equal(20, magebane.SpellSuppressionChance);
        Assert.Equal(plain.Accuracy, magebane.Accuracy);
        var magebaneStats = new Dictionary<uint, int> { [Stat.KeystoneMagebane] = 1 };
        var magebaneBonus = Game.PassiveBonus(magebaneStats, new WeaponProfile(false, false, false, false, false), default);
        Assert.True(magebaneBonus.NoDexterityEvasion);
        Assert.Equal(1, magebaneBonus.SpellSuppressionPerFifteenDexterity);

        // Pain Attunement: 30% more Spell Damage on low life, and nothing off it.
        var fire = DamagePacket.Of(DamageType.Fire, new DamageRange(100, 100));
        int SpellGet(uint key) => key switch
        {
            Stat.KeystonePainAttunement => 1,
            Stat.OnLowLife => 1,
            _ => 0,
        };
        Assert.Equal(130, PlayerHitDamage.Resolve(fire, SpellGet, new DamageConversion(), spell: true)[DamageType.Fire].Max);
        int NotLow(uint key) => key == Stat.KeystonePainAttunement ? 1 : 0;
        Assert.Equal(100, PlayerHitDamage.Resolve(fire, NotLow, new DamageConversion(), spell: true)[DamageType.Fire].Max);

        // Elemental Overload: the window's 40% more reaches a spell, an attack and an elemental ailment; and while the
        // keystone is allocated an ailment is never from a critical strike (no critical ailment multiplier, and the
        // application rolls the hit's chance instead of a critical strike's guaranteed one).
        int Overload(uint key) => key switch
        {
            Stat.KeystoneElementalOverload => 1,
            Stat.KeystoneElementalOverloadDamagePlusPctFinal => 40,
            Stat.CriticalAilmentDotMultiplierPlus => 50,
            _ => 0,
        };
        Assert.Equal(140, PlayerHitDamage.Resolve(fire, Overload, new DamageConversion(), spell: true)[DamageType.Fire].Max);
        Assert.Equal(140, PlayerHitDamage.Resolve(fire, Overload, new DamageConversion(), spell: false)[DamageType.Fire].Max);
        var overloadRows = AilmentPanelStats.Compute(new AilmentPanelStats.Context("main_hand", false, true), fire,
                new DamageConversion(), Overload).ToDictionary(r => r.Stat, r => r.Value);
        // 100 fire * 90% ignite * 60 s * 1.4, the same on a critical strike (its +50% multiplier does not apply).
        Assert.Equal(7560, overloadRows[Stat.MaximumIgniteDamageToDealPerMinuteFromNonCriticalMainHandDamage]);
        Assert.Equal(7560, overloadRows[Stat.MaximumIgniteDamageToDealPerMinuteFromCriticalMainHandDamage]);
        var overloadSettings = MonsterIgnites.Settings.From(overloadRows.Select(kv => (kv.Key, kv.Value)), "main_hand", Overload);
        Assert.True(overloadSettings.NeverFromCrit);
        Assert.False(new MonsterIgnites().TryApply(overloadSettings, critical: true, 0, 0, false, new Random(1)));
        // Without the keystone the critical row keeps its multiplier and a critical strike always ignites.
        int NoOverload(uint key) => key == Stat.CriticalAilmentDotMultiplierPlus ? 50 : 0;
        var plainRows = AilmentPanelStats.Compute(new AilmentPanelStats.Context("main_hand", false, true), fire,
                new DamageConversion(), NoOverload).ToDictionary(r => r.Stat, r => r.Value);
        Assert.Equal(5400, plainRows[Stat.MaximumIgniteDamageToDealPerMinuteFromNonCriticalMainHandDamage]);
        Assert.Equal(8100, plainRows[Stat.MaximumIgniteDamageToDealPerMinuteFromCriticalMainHandDamage]);
        var plainSettings = MonsterIgnites.Settings.From(plainRows.Select(kv => (kv.Key, kv.Value)), "main_hand", NoOverload);
        Assert.False(plainSettings.NeverFromCrit);
        Assert.True(new MonsterIgnites().TryApply(plainSettings, critical: true, 0, 0, false, new Random(1)));

        // The window is per skill: only the skill that crit opens it, for eight seconds, and the keystone's carrier stat
        // is added to that skill's damage snapshot only while its own window is open.
        var events = new RecentEvents();
        events.RecordSkillCrit("ImmortalCall", 10, true);
        Assert.True(events.SkillCritRecently("ImmortalCall", 17.999));
        Assert.False(events.SkillCritRecently("ImmortalCall", 18));
        Assert.False(events.SkillCritRecently("Fireball", 11));
        var withKeystone = new Dictionary<uint, int> { [Stat.KeystoneElementalOverload] = 1 };
        double now = Game.AreaTime(f.Player);
        f.Player.RecentEvents.RecordSkillCrit("ImmortalCall", now, true);
        var bonusKey = Stat.KeystoneElementalOverloadDamagePlusPctFinal;
        var opened = new Dictionary<uint, int>(withKeystone);
        Assert.True(Game.AddElementalOverload(f.Player, "ImmortalCall", opened));
        Assert.Equal(40, opened.GetValueOrDefault(bonusKey));
        var closed = new Dictionary<uint, int>(withKeystone);
        Assert.False(Game.AddElementalOverload(f.Player, "Fireball", closed));
        Assert.Equal(0, closed.GetValueOrDefault(bonusKey));
        // A critical strike without the keystone records nothing and sends nothing (this runs on every hit).
        Assert.Null(Game.ElementalOverloadCritLocked(f.Player, SkillCatalog.FirstSkillId, "Fireball",
            new Dictionary<uint, int>(), critical: true));
        Assert.False(f.Player.RecentEvents.SkillCritRecently("Fireball", now));

        // Perfect Agony: the ailment DoT multiplier becomes the critical strike multiplier (150 + 150 = 300%), other DoT
        // multiplier sources ignored; only critical strikes inflict ailments, and with Elemental Overload none do.
        int Agony(uint key) => key switch
        {
            Stat.KeystoneAilmentCrit => 1,
            Stat.BaseCriticalStrikeMultiplierPlus => 150,
            Stat.DotMultiplierPlus => 100,   // ignored under Perfect Agony
            _ => 0,
        };
        var agonyRows = AilmentPanelStats.Compute(new AilmentPanelStats.Context("main_hand", false, true), fire,
                new DamageConversion(), Agony).ToDictionary(r => r.Stat, r => r.Value);
        Assert.Equal(16200, agonyRows[Stat.MaximumIgniteDamageToDealPerMinuteFromNonCriticalMainHandDamage]);
        var agonySettings = MonsterIgnites.Settings.From(agonyRows.Select(kv => (kv.Key, kv.Value)), "main_hand", Agony);
        Assert.True(agonySettings.OnlyFromCrit);
        Assert.False(new MonsterIgnites().TryApply(agonySettings, critical: false, 0, 0, false, new Random(1)));
        Assert.True(new MonsterIgnites().TryApply(agonySettings, critical: true, 0, 0, false, new Random(1)));
        int Both(uint key) => key is Stat.KeystoneAilmentCrit or Stat.KeystoneElementalOverload ? 1
            : key == Stat.BaseCriticalStrikeMultiplierPlus ? 150 : 0;
        var bothRows = AilmentPanelStats.Compute(new AilmentPanelStats.Context("main_hand", false, true), fire,
                new DamageConversion(), Both).ToDictionary(r => r.Stat, r => r.Value);
        var bothSettings = MonsterIgnites.Settings.From(bothRows.Select(kv => (kv.Key, kv.Value)), "main_hand", Both);
        Assert.False(new MonsterIgnites().TryApply(bothSettings, critical: true, 0, 0, false, new Random(1)));

        // Perfect Agony gates the non-damaging ailments too: a non-critical hit inflicts no chill, freeze or shock, and
        // Elemental Overload makes every hit count as non-critical for ailments, so the two together inflict none.
        Func<uint, int> Elemental(int agony, int overload) => key => key switch
        {
            Stat.KeystoneAilmentCrit => agony,
            Stat.KeystoneElementalOverload => overload,
            _ => 0,
        };
        var coldHit = DamagePacket.Of(DamageType.Cold, 10, 10);
        var lightningHit = DamagePacket.Of(DamageType.Lightning, 10, 10);
        Assert.Empty(new MonsterElementalAilments().Inflict(coldHit, false, 100, 0, Elemental(1, 0), _ => 0, new Random(1)));
        Assert.NotEmpty(new MonsterElementalAilments().Inflict(coldHit, true, 100, 0, Elemental(1, 0), _ => 0, new Random(1)));
        Assert.Empty(new MonsterElementalAilments().Inflict(coldHit, true, 100, 0, Elemental(1, 1), _ => 0, new Random(1)));
        Assert.Empty(new MonsterElementalAilments().Inflict(lightningHit, true, 100, 0, Elemental(1, 1), _ => 0, new Random(1)));
        // Elemental Overload alone: a "critical" cold hit still chills but no longer guarantees a freeze.
        var eoCold = new MonsterElementalAilments().Inflict(coldHit, true, 100, 0, Elemental(0, 1), _ => 0, new Random(1));
        Assert.Contains(eoCold, e => e.Kind == MonsterElementalAilments.Kind.Chill);
        Assert.DoesNotContain(eoCold, e => e.Kind == MonsterElementalAilments.Kind.Freeze);

        // Lethe Shade: ailments on you last twice as long (a reflected one does not), and the damaging-ailment DoT you
        // take is halved while one started within the past second.
        int Lethe(uint key) => key == Stat.KeystoneLetheShade ? 1 : 0;
        Assert.Equal(2, Game.LetheShadeAilmentDuration(Lethe));
        Assert.Equal(1, Game.LetheShadeAilmentDuration(_ => 0));
        var letheBleed = new IncomingDamagingAilments();
        letheBleed.ApplyBleed(0, 10, 5, durationFactor: 2);
        Assert.Equal(10, letheBleed.BleedingUntil(0));   // 5 s doubled
        Assert.Equal(0, letheBleed.LatestStart);
        var reflectedBleed = new IncomingDamagingAilments();
        reflectedBleed.ApplyBleed(0, 10, 5, reflected: true, durationFactor: 2);
        Assert.Equal(5, reflectedBleed.BleedingUntil(0));   // reflection keeps its duration
        PlayerElementalAilments Ignited()
        {
            var a = new PlayerElementalAilments();
            a.Apply(DamagePacket.Of(DamageType.Fire, 100, 100), DamagePacket.Of(DamageType.Fire, 100, 100),
                critical: true, 1000, 0, Lethe, new Random(1), durationFactor: 2);
            return a;
        }
        // 100 fire × 90% × 1.3 critical = 117/s; the halved window takes 58 of it.
        Assert.Equal(117, Ignited().Advance(0, 1, Lethe, 0, 0, false, null, 1));
        Assert.Equal(58, Ignited().Advance(0, 1, Lethe, 0, 0, false, null, .5));

        // Vaal Pact: melee life leech is instant, non-melee life leech is not; non-leech life recovery is blocked while
        // leech itself still recovers.
        int Vaal(uint key) => key == Stat.KeystoneVaalPact ? 1 : 0;
        Assert.True(LifeRecovery.NonLeechBlocked(Vaal));
        Assert.False(LifeRecovery.NonLeechBlocked(_ => 0));
        Player VaalPlayer(uint life) => new(new(1, "vaal", 300, 300, 100, 100, null, []), 1, 1)
            { Pools = new(1, 1000, 1000, 0, 0), PlayerLife = life };
        var vaalTarget = new SpawnedMonster("vaal", new(0, 0, 0), 0, 0, 1000) { Life = 1000 };
        var meleeSources = new LeechSources(DamagePacket.Of(DamageType.Physical, 1000, 1000),
            DamagePacket.Empty, DamagePacket.Empty) { Attack = true, Melee = true };
        var meleeConn = VaalPlayer(800);
        Game.LeechOf(meleeConn).Configure(0,
            LeechLimits.Resolve(Vaal, meleeConn.Pools.Life, meleeConn.Pools.Mana, meleeConn.Pools.EnergyShield), Vaal, _ => null);
        Game.LeechOnHitLocked(meleeConn, vaalTarget, DamagePacket.Of(DamageType.Physical, 500, 500), 500, meleeSources);
        Assert.Equal(850u, meleeConn.PlayerLife);   // the 50 leeched life lands at once
        var rangedConn = VaalPlayer(800);
        Game.LeechOf(rangedConn).Configure(0,
            LeechLimits.Resolve(Vaal, rangedConn.Pools.Life, rangedConn.Pools.Mana, rangedConn.Pools.EnergyShield), Vaal, _ => null);
        Game.LeechOnHitLocked(rangedConn, vaalTarget, DamagePacket.Of(DamageType.Physical, 500, 500), 500, meleeSources with { Melee = false });
        Assert.Equal(800u, rangedConn.PlayerLife);   // non-melee leech is over time
        Assert.Equal(1, Game.LeechOf(rangedConn).Life.Count);
    }
}
