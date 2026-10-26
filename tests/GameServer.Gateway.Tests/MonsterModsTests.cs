using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>Magic and rare monster mods (MonsterMods.cs, 14-monster-packs.md §5): the roll rules against the extracted
/// Mods.dat, the effects on the server's numbers, the hooks the hits use, persistence and the unsupported mods.</summary>
public class MonsterModsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<(ModCatalog Mods, MonsterCatalog Monsters, MonsterModCatalog Rolls)> Setup = new(() =>
    {
        var mods = new ModCatalog(Data());
        return (mods, new MonsterCatalog(Data()), new MonsterModCatalog(mods, Data()) { RollUnsupported = false });
    });

    static ModCatalog Mods => Setup.Value.Mods;
    static MonsterModCatalog Rolls => Setup.Value.Rolls;
    static MonsterCatalog.Variety Variety(int row) => Setup.Value.Monsters.Find(row.ToString())!;
    static MonsterCatalog.Variety Corpse => Variety(1159);    // Hungry Corpse: undead, zombie, physical_affinity …
    static MonsterCatalog.Variety Crab => Variety(4515);      // Savage Crab: beast, not_dex, not_int …
    static MonsterCatalog.Variety Cannibal => Variety(2842);  // Cannibal: not_str, ranged …
    static ModCatalog.Mod Mod(string id) => Mods.ById(id)!;

    static List<ModCatalog.Mod> Affixes(MonsterModSet set) => [.. set.Affixes.Select(a => Mods.ByHash(a.Hash)!)];

    static MonsterModSet With(MonsterRarity rarity, int level, params string[] affixes) =>
        Rolls.Roll(Corpse, level, rarity, new Random(0), alone: true, count: affixes.Length, forced: [.. affixes.Select(Mod)]);

    // ---- The pool ----

    [Fact]
    public void The_pool_holds_monster_domain_affixes_up_to_the_level_with_a_weight_for_the_tags()
    {
        var tags = Rolls.TagsOf(Corpse, MonsterRarity.Rare, alone: false);
        var pool = Rolls.Pool(tags, 20);
        Assert.NotEmpty(pool);
        Assert.All(pool, p =>
        {
            Assert.Equal(MonsterModCatalog.Domain, p.Mod.Domain);
            Assert.Contains(p.Mod.GenerationType, new[] { 1, 2 });
            Assert.InRange(p.Mod.Level, 0, 20);
            Assert.Equal(p.Mod.WeightFor(tags), p.Weight);
            Assert.True(p.Weight > 0);
        });
        Assert.Contains(pool, p => p.Mod.Id == "MonsterModIncreasedLife" && p.Weight == 8000);
        // Level 24 mods (of the Hydra) open later.
        Assert.DoesNotContain(pool, p => p.Mod.Id == "MonsterModLifeRegeneration");
        Assert.Contains(Rolls.Pool(tags, 24), p => p.Mod.Id == "MonsterModLifeRegeneration");
    }

    [Fact]
    public void The_monsters_tags_decide_the_weights()
    {
        // Blurred (evasion) has not_dex 0: the crab (not_dex) never gets it, the corpse can.
        Assert.DoesNotContain(Rolls.Pool(Rolls.TagsOf(Crab, MonsterRarity.Rare, false), 50), p => p.Mod.Id == "MonsterModIncreasedEvasionRating");
        Assert.Contains(Rolls.Pool(Rolls.TagsOf(Corpse, MonsterRarity.Rare, false), 50), p => p.Mod.Id == "MonsterModIncreasedEvasionRating");
        // Carapaced has not_str 0: the cannibal (not_str) never gets it.
        Assert.DoesNotContain(Rolls.Pool(Rolls.TagsOf(Cannibal, MonsterRarity.Rare, false), 50), p => p.Mod.Id == "MonsterModPhysicalDamageReduction");
        Assert.Contains(Rolls.Pool(Rolls.TagsOf(Corpse, MonsterRarity.Rare, false), 50), p => p.Mod.Id == "MonsterModPhysicalDamageReduction");
    }

    [Fact]
    public void The_rarity_tag_keeps_the_rare_only_mods_off_magic_monsters()
    {
        Assert.Contains("magic", Rolls.TagsOf(Corpse, MonsterRarity.Magic, false));
        Assert.Contains("rare", Rolls.TagsOf(Corpse, MonsterRarity.Rare, false));
        Assert.Contains("not_part_of_pack", Rolls.TagsOf(Corpse, MonsterRarity.Rare, alone: true));
        // of Haste: magic 0, default 3000.
        Assert.DoesNotContain(Rolls.Pool(Rolls.TagsOf(Corpse, MonsterRarity.Magic, false), 50), p => p.Mod.Id == "MonsterModSpeedAura");
        Assert.Contains(Rolls.Pool(Rolls.TagsOf(Corpse, MonsterRarity.Rare, false), 50), p => p.Mod.Id == "MonsterModSpeedAura" && p.Weight == 3000);
    }

    // ---- Counts, families, weights ----

    [Fact]
    public void A_magic_monster_has_one_affix_and_no_name()
    {
        var rng = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            var set = Rolls.Roll(Corpse, 30, MonsterRarity.Magic, rng);
            Assert.Single(set.Affixes);
            Assert.Empty(set.NameWords);
        }
    }

    [Fact]
    public void Rares_have_two_or_three_affixes_in_the_campaign_and_two_to_four_in_maps()
    {
        var rng = new Random(2);
        var campaign = Enumerable.Range(0, 2000).Select(_ => Rolls.Roll(Corpse, 40, MonsterRarity.Rare, rng).Affixes.Count()).ToList();
        Assert.All(campaign, n => Assert.InRange(n, 2, 3));
        Assert.InRange(campaign.Count(n => n == 2) / 2000.0, 0.70, 0.80);
        var maps = Enumerable.Range(0, 2000).Select(_ => Rolls.Roll(Corpse, 75, MonsterRarity.Rare, rng).Affixes.Count()).ToList();
        Assert.All(maps, n => Assert.InRange(n, 2, 4));
        Assert.InRange(maps.Count(n => n == 4) / 2000.0, 0.17, 0.23);
        Assert.Equal(0, MonsterModCatalog.AffixCount(MonsterRarity.Normal, 75, rng));
        Assert.Equal(0, MonsterModCatalog.AffixCount(MonsterRarity.Unique, 75, rng));
    }

    [Fact]
    public void A_family_is_taken_once_and_the_rolls_stay_in_the_pool()
    {
        var rng = new Random(3);
        var tags = Rolls.TagsOf(Corpse, MonsterRarity.Rare, false);
        var pool = Rolls.Pool(tags, 80).Select(p => p.Mod.Id).ToHashSet();
        for (int i = 0; i < 500; i++)
        {
            var mods = Affixes(Rolls.Roll(Corpse, 80, MonsterRarity.Rare, rng, count: 4));
            var families = mods.SelectMany(m => m.Families).ToList();
            Assert.Equal(families.Count, families.Distinct().Count());
            Assert.All(mods, m => Assert.Contains(m.Id, pool));
            // Prefixes first, as the client builds the name from them.
            Assert.Equal(mods.OrderBy(m => m.GenerationType == 2 ? 1 : 0).Select(m => m.Id), mods.Select(m => m.Id));
        }
    }

    [Fact]
    public void Low_level_monsters_roll_only_low_level_mods()
    {
        var rng = new Random(4);
        for (int i = 0; i < 300; i++)
            Assert.All(Affixes(Rolls.Roll(Corpse, 5, MonsterRarity.Rare, rng, count: 2)), m => Assert.InRange(m.Level, 0, 5));
    }

    [Fact]
    public void Affixes_are_drawn_by_their_spawn_weights()
    {
        // At level 1 the corpse's pool is Fecund 8000 and seven mods of 5000: Fecund comes 8/43 of the time.
        var tags = Rolls.TagsOf(Corpse, MonsterRarity.Magic, false);
        var pool = Rolls.Pool(tags, 1);
        int total = pool.Sum(p => p.Weight);
        var rng = new Random(5);
        int fecund = Enumerable.Range(0, 20_000).Count(_ => Affixes(Rolls.Roll(Corpse, 1, MonsterRarity.Magic, rng))[0].Id == "MonsterModIncreasedLife");
        Assert.InRange(fecund / 20_000.0, 8000.0 / total - 0.015, 8000.0 / total + 0.015);
    }

    // ---- Rarity's own mods and the rare name ----

    [Fact]
    public void Every_magic_and_rare_monster_has_its_raritys_own_mods_hidden()
    {
        var magic = Rolls.Roll(Corpse, 10, MonsterRarity.Magic, new Random(6));
        var hidden = magic.Mods.Where(m => m.Section == ModSection.Hidden).Select(m => Mods.ByHash(m.Hash)!.Id).ToList();
        Assert.Equal(Enumerable.Range(1, 11).Select(i => $"MonsterMagic{i}"), hidden);
        var rare = Rolls.Roll(Corpse, 10, MonsterRarity.Rare, new Random(6));
        Assert.Equal(12, rare.Mods.Count(m => m.Section == ModSection.Hidden));
        Assert.Empty(Rolls.Roll(Corpse, 10, MonsterRarity.Normal, new Random(6)).Mods);
    }

    [Fact]
    public void A_rare_monster_is_named_from_the_monster_word_lists_by_its_tags()
    {
        var set = Rolls.Roll(Crab, 10, MonsterRarity.Rare, new Random(7));
        Assert.Equal(2, set.NameWords.Count);
        string name = Rolls.NameOf(set.NameWords);
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.DoesNotContain("Broken", name);   // the default-weighted placeholders never win over the beast words
        // The suffix is lower case and joins the prefix.
        Assert.True(char.IsUpper(name[0]));
    }

    // ---- Effects ----

    [Fact]
    public void Life_takes_the_increase_the_rarity_and_the_rarity_table()
    {
        var rare = With(MonsterRarity.Rare, 1, "MonsterModIncreasedLife");
        Assert.Equal(40, rare.Effects["maximum_life_+%"]);
        Assert.Equal(390, rare.Effects["monster_life_+%_final_from_rarity"]);
        Assert.Equal(33, rare.Effects[MonsterModCatalog.RarityTableLifeStat]);   // RareMonsterLifeScalingPerLevel level 1
        Assert.Equal((int)Math.Floor(100 * 1.4 * 4.9 * 1.33), rare.Effects.MaxLife(100));
        var magic = Rolls.Roll(Corpse, 70, MonsterRarity.Magic, new Random(0), count: 0);
        int table = Rolls.RarityTableLifePct(MonsterRarity.Magic, 70);
        Assert.InRange(table, 74, 75);   // MagicMonsterLifeScalingPerLevel: +50% at level 1 to +75%
        Assert.Equal((int)Math.Floor(100 * 2.48 * (1 + table / 100.0)), magic.Effects.MaxLife(100));
        Assert.Equal(100, MonsterModSet.None.Effects.MaxLife(100));
    }

    [Fact]
    public void Damage_takes_the_rarity_the_increases_and_the_extra_elements()
    {
        var plain = With(MonsterRarity.Rare, 40);
        var hit = plain.Effects.Hit(DamagePacket.Of(DamageType.Physical, 110, 200));
        // Rare: 50% more, 33% less per hit, rounded as before: 110 × 1.5 × 0.67 = 110.55 → 111.
        Assert.Equal(111, hit[DamageType.Physical].Min);
        Assert.Equal(201, hit[DamageType.Physical].Max);
        var fire = With(MonsterRarity.Rare, 40, "MonsterModExtraFireDamage", "MonsterModExtraChaosDamage").Effects
            .Hit(DamagePacket.Of(DamageType.Physical, 110, 200));
        Assert.Equal(111, fire[DamageType.Physical].Min);
        Assert.Equal(56, fire[DamageType.Fire].Min);                       // 50% of 111, rounded
        Assert.Equal(Math.Round((111 + 56) * 0.15), fire[DamageType.Chaos].Min);
        var aura = With(MonsterRarity.Rare, 40, "MonsterModPhysicalDamageAura").Effects.Hit(DamagePacket.Of(DamageType.Physical, 100, 100));
        Assert.Equal(Math.Round(100 * 1.4 * 1.5 * 0.67), aura[DamageType.Physical].Min);
    }

    [Fact]
    public void Speed_mods_shorten_the_attack_and_speed_up_the_walk()
    {
        var quick = With(MonsterRarity.Rare, 40, "MonsterModAttackCastMovementSpeed").Effects;
        Assert.Equal(1.5 / 1.3 / 1.33, quick.AttackSeconds(1.5), 6);
        Assert.Equal(25 + 40, quick.MovementPct);   // MonsterRare8 +25%, Quick +40%
        Assert.Equal(30 * 1.65, quick.MovementSpeed(30), 6);
        var magic = With(MonsterRarity.Magic, 40, "MonsterModSpeedAura").Effects;   // not on magic by weight, forced here
        Assert.Equal(10 + 30, magic.MovementPct);
    }

    [Fact]
    public void Defences_accuracy_crit_and_regeneration_come_from_their_stats()
    {
        var e = With(MonsterRarity.Rare, 60, "MonsterModResistantToFire", "MonsterModIncreasedEvasionRating",
            "MonsterModIncreasedAccuracy", "MonsterModIncreasedCriticalStrikeChance").Effects;
        Assert.Equal(75, e.Resistance(DamageType.Fire));
        Assert.Equal(0, e.Resistance(DamageType.Cold));
        Assert.Equal(400, e.Evasion(200));
        Assert.Equal(600, e.Accuracy(300));
        Assert.Equal(0, e.CriticalChance);   // of Incision: 300% more of no base chance is none
        Assert.Equal(0, MonsterModSet.None.Effects.CriticalChance);
        Assert.Equal(10_000, With(MonsterRarity.Rare, 60, "MonsterModAlwaysCrit").Effects.CriticalChance);
        var touched = With(MonsterRarity.Rare, 70, "MonsterModKitavaTouched").Effects;
        Assert.Equal(40, touched.Resistance(DamageType.Lightning));
        Assert.Equal(25, touched.Resistance(DamageType.Chaos));
        Assert.Equal(15, touched.PhysicalReduction);
        Assert.Equal(1000 * 180 / 6000.0, With(MonsterRarity.Rare, 60, "MonsterModLifeRegeneration").Effects.LifeRegenPerSecond(1000), 6);
    }

    // ---- The hooks the hits use ----

    static SpawnedMonster Monster(MonsterModSet mods, uint life = 1000, IReadOnlyList<HitAnimation>? clips = null) =>
        new("Corpse@60", new WalkEstimator(10, 10, 30, null, 100, 100), 60, 3, life)
        { Mods = mods, Life = life, Variety = Corpse, Level = 60, HitAnimations = clips ?? [] };

    [Fact]
    public void The_players_hit_meets_the_monsters_resistance_reduction_and_damage_taken()
    {
        var hit = DamagePacket.Of(DamageType.Fire, 100, 100).Add(DamageType.Physical, new DamageRange(100, 100));
        Assert.Equal(200, Program.MonsterTakes(Monster(MonsterModSet.None), hit, new Random(1), spell: false));
        // of the Magma: 75% fire resistance.
        Assert.Equal(125, Program.MonsterTakes(Monster(With(MonsterRarity.Rare, 60, "MonsterModResistantToFire")), hit, new Random(1), false));
        // Carapaced: 50% less physical.
        Assert.Equal(150, Program.MonsterTakes(Monster(With(MonsterRarity.Rare, 60, "MonsterModPhysicalDamageReduction")), hit, new Random(1), false));
        // Spell Resistant: 50% less from spells only.
        var spellResistant = Monster(With(MonsterRarity.Rare, 60, "MonsterModSpellResistant"));
        Assert.Equal(100, Program.MonsterTakes(spellResistant, hit, new Random(1), spell: true));
        Assert.Equal(200, Program.MonsterTakes(spellResistant, hit, new Random(1), spell: false));
        // The god-touched resistances add up with of the Magma and stop at 75%.
        Assert.Equal(75, DamageModel.Capped(With(MonsterRarity.Rare, 70, "MonsterModResistantToFire", "MonsterModKitavaTouched").Effects.Resistance(DamageType.Fire), 75));
    }

    [Fact]
    public void Stalwart_blocks_half_the_attacks_and_no_spell()
    {
        var m = Monster(With(MonsterRarity.Rare, 60, "MonsterModAttackBlock"));
        var rng = new Random(9);
        int blocked = Enumerable.Range(0, 4000).Count(_ => Program.MonsterBlocks(m, rng, spell: false));
        Assert.InRange(blocked / 4000.0, 0.47, 0.53);
        Assert.DoesNotContain(true, Enumerable.Range(0, 200).Select(_ => Program.MonsterBlocks(m, rng, spell: true)));
        Assert.DoesNotContain(true, Enumerable.Range(0, 200).Select(_ => Program.MonsterBlocks(Monster(MonsterModSet.None), rng, false)));
    }

    [Fact]
    public void A_blocked_hit_applies_nothing_of_the_hit()
    {
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);
        var m = Monster(With(MonsterRarity.Rare, 60, "MonsterModAttackBlock"));
        conn.LastEnemyHitAt = -5;
        Assert.Null(Program.HitUnlessBlockedLocked(conn, m, Program.Blocked));
        Assert.Equal(1000u, m.Life);
        Assert.Equal(-5, conn.LastEnemyHitAt);
        Assert.Equal(0, m.Impale.Stacks);
        // An unblocked hit lands: life and the "hit recently" time change.
        Assert.Equal(990u, Program.HitUnlessBlockedLocked(conn, m, 10)!.Value.Life);
        Assert.NotEqual(-5, conn.LastEnemyHitAt);
    }

    [Fact]
    public void Toughness_takes_crits_at_their_normal_damage()
    {
        Assert.Equal(100, Program.CriticalMultiplierOn(Monster(With(MonsterRarity.Rare, 60, "MonsterModNoExtraDamageFromCrits")), 150));
        Assert.Equal(150, Program.CriticalMultiplierOn(Monster(MonsterModSet.None), 150));
    }

    [Fact]
    public void A_monsters_crit_and_executioners_raise_its_hit_on_the_player()
    {
        var rolled = DamagePacket.Of(DamageType.Physical, 100, 100);
        var always = Monster(With(MonsterRarity.Rare, 60, "MonsterModAlwaysCrit"));
        var crit = Program.MonsterModHit(always, rolled, 100, 100, new Random(1), out bool critical);
        Assert.True(critical);
        Assert.Equal(130, crit[DamageType.Physical].Min, 6);
        // The player's reduced extra damage from critical strikes lowers the 130%, never below 100%.
        Assert.Equal(110, Program.MonsterModHit(always, rolled, 100, 100, new Random(1), out _, () => 20)[DamageType.Physical].Min, 6);
        Assert.Equal(100, Program.MonsterModHit(always, rolled, 100, 100, new Random(1), out _, () => 50)[DamageType.Physical].Min, 6);
        // Executioner's: 20% increased per 10% of the player's missing life; 45% missing → 4 steps.
        var exec = Monster(With(MonsterRarity.Rare, 60, "MonsterModDamagePerMissingLife"));
        var hit = Program.MonsterModHit(exec, rolled, 55, 100, new Random(1), out bool execCrit);
        Assert.False(execCrit);
        Assert.Equal(180, hit[DamageType.Physical].Min, 6);
    }

    [Fact]
    public void A_monster_without_a_crit_mod_never_crits()
    {
        var rolled = DamagePacket.Of(DamageType.Physical, 100, 100);
        var rng = new Random(12);
        foreach (var m in new[] { Monster(MonsterModSet.None), Monster(With(MonsterRarity.Rare, 60, "MonsterModIncreasedCriticalStrikeChance")),
            Monster(Rolls.Roll(Corpse, 60, MonsterRarity.Unique, rng)) })
            for (int i = 0; i < 2000; i++)
            {
                var hit = Program.MonsterModHit(m, rolled, 100, 100, rng, out bool critical);
                Assert.False(critical);
                Assert.Equal(100, hit[DamageType.Physical].Min, 6);
            }
    }

    [Fact]
    public void Hillock_and_every_raritys_hit_are_as_before()
    {
        // Main's rule: max(1, round(base × (1 + more) × (1 − faster))) per end of the range (MonsterCatalog.RarityAttack).
        var hillock = Setup.Value.Monsters.Find("Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal")!;
        foreach (var rarity in new[] { MonsterRarity.Normal, MonsterRarity.Magic, MonsterRarity.Rare, MonsterRarity.Unique })
            foreach (byte level in new byte[] { 1, 13, 45, 70 })
            {
                var (min, max) = Setup.Value.Monsters.BaseDamage(hillock, level)!.Value;
                var (factor, _) = MonsterCatalog.RarityAttack(rarity);
                var hit = Rolls.Roll(hillock, level, rarity, new Random(0), count: 0).Effects.Hit(DamagePacket.Of(DamageType.Physical, min, max));
                Assert.Equal(Math.Max(1, (int)Math.Round(min * factor)), hit[DamageType.Physical].Min);
                Assert.Equal(Math.Max(1, (int)Math.Round(max * factor)), hit[DamageType.Physical].Max);
                Assert.Equal(hit[DamageType.Physical].Max, hit.MaxTotal);
            }
    }

    [Fact]
    public void Righteous_fires_burn_meets_the_fire_resistance()
    {
        var burn = new DamageOverTime(DamageType.Fire, 100, 10);
        var m = Monster(With(MonsterRarity.Rare, 60, "MonsterModResistantToFire"));
        Assert.Equal(25, Program.MonsterBurn(m, burn, 1).Damage);
        Assert.Equal(100, Program.MonsterBurn(Monster(MonsterModSet.None), burn, 1).Damage);
        // Spell Resistant is for spell hits only.
        Assert.Equal(100, Program.MonsterBurn(Monster(With(MonsterRarity.Rare, 60, "MonsterModSpellResistant")), burn, 1).Damage);
    }

    [Fact]
    public void Regenerating_monsters_regain_life_and_full_ones_send_nothing()
    {
        var hydra = Monster(With(MonsterRarity.Rare, 60, "MonsterModLifeRegeneration"), life: 1000);
        hydra.Life = 500;
        var full = Monster(With(MonsterRarity.Rare, 60, "MonsterModLifeRegeneration"));
        var plain = Monster(MonsterModSet.None);
        plain.Life = 10;
        var changed = Program.RegenerateMonstersLocked(new Dictionary<uint, SpawnedMonster> { [1] = hydra, [2] = full, [3] = plain }, 2);
        Assert.Equal([(1u, 560u)], changed);   // 3% of 1000 per second
        Assert.Equal(10u, plain.Life);
    }

    [Fact]
    public void Unwavering_monsters_do_not_flinch()
    {
        var m = Monster(With(MonsterRarity.Rare, 60, "MonsterModCannotBeStunned"), clips: [new HitAnimation(3, 0.5)]);
        Assert.Null(HitReaction.TryFlinch(m, 900, 10, new Random(1)));
        var plain = Monster(MonsterModSet.None, clips: [new HitAnimation(3, 0.5)]);
        Assert.NotNull(HitReaction.TryFlinch(plain, 900, 10, new Random(1)));
    }

    // ---- Client stats and blob ----

    [Fact]
    public void The_client_gets_the_final_life_the_walk_speed_and_the_size()
    {
        var set = With(MonsterRarity.Rare, 1, "MonsterModIncreasedLife");
        int max = set.Effects.MaxLife(100);
        var stats = Program.MonsterModClientStats(set.Effects, (uint)max, 100).ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(390, stats[StatKeys.MonsterLifeFromRarityPct]);
        Assert.Equal(max, stats[StatKeys.MaximumLife]);
        Assert.Equal(25, stats[StatKeys.MovementVelocityPct]);
        Assert.Empty(Program.MonsterModClientStats(MonsterModSet.None.Effects, 100, 100));
    }

    [Fact]
    public void The_blob_carries_the_affixes_in_the_explicit_list_and_the_name_words()
    {
        var set = With(MonsterRarity.Rare, 60, "MonsterModIncreasedLife");
        var monster = new MonsterObject(1, 2, 100, MonsterObject.BaseComponents, Rarity: MonsterRarity.Rare)
        { Mods = set.Mods, NameWords = [0x11223344, 0x55667788] };
        string hex = Convert.ToHexString(monster.Blob());
        var fecund = Mod("MonsterModIncreasedLife");
        int hidden = set.Mods.Count(m => m.Section == ModSection.Hidden);
        // flags: rare 2 | identified 8 | explicit 0x20 | name words 0x100 | hidden 0x800 = 0x92a, then u8 0.
        Assert.StartsWith("2A0900" + $"{hidden:X2}", hex[60..]);
        string tail = "02" + "44332211" + "88776655" + "01" + $"{fecund.Hash & 0xff:X2}{fecund.Hash >> 8:X2}" + "28" + "00";
        Assert.Contains(tail + "0000", hex);   // the explicit list, then the own flags and Buffs
    }

    // ---- Persistence ----

    [Fact]
    public void A_kept_instance_keeps_each_monsters_mods()
    {
        var set = With(MonsterRarity.Rare, 60, "MonsterModResistantToCold", "MonsterModIncreasedLife");
        var saved = Assert.Single(SavedMonster.Save([Monster(set)], now: 0));
        Assert.Same(set, saved.Mods);
        var store = new InstanceStore(TimeProvider.System, TimeSpan.FromMinutes(6));
        var state = store.Enter(9, 1);
        state.Monsters = [saved];
        store.Leave(state);
        var back = Assert.Single(store.Enter(9, 1).Monsters!);
        Assert.Equal(set.Mods, back.Mods.Mods);
        Assert.Equal(set.NameWords, back.Mods.NameWords);
    }

    [Fact]
    public void Magic_monsters_of_one_name_share_their_mods_in_an_instance_and_kept_mods_stay()
    {
        ClientState Conn() => new(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1) { Instance = new InstanceState() };
        var conn = Conn();
        var first = Program.MonsterModsFor(conn, Corpse, 10, MonsterRarity.Magic, null, alone: false);
        Assert.Same(first, Program.MonsterModsFor(conn, Corpse, 10, MonsterRarity.Magic, null, alone: false));
        // Another instance, a rare, or a monster on its own rolls its own.
        Assert.NotSame(first, Program.MonsterModsFor(Conn(), Corpse, 10, MonsterRarity.Magic, null, alone: false));
        Assert.NotSame(Program.MonsterModsFor(conn, Corpse, 10, MonsterRarity.Rare, null, alone: false),
            Program.MonsterModsFor(conn, Corpse, 10, MonsterRarity.Rare, null, alone: false));
        Assert.NotSame(first, Program.MonsterModsFor(conn, Corpse, 10, MonsterRarity.Magic, null, alone: true));
        // A kept monster's mods come back as they were.
        var kept = With(MonsterRarity.Rare, 10, "MonsterModIncreasedLife");
        Assert.Same(kept, Program.MonsterModsFor(conn, Corpse, 10, MonsterRarity.Rare, kept, alone: false));
    }

    [Fact]
    public void A_shared_magic_roll_goes_only_to_the_varieties_that_can_have_it()
    {
        // The Coast's Savage Crabs: the small ones (not_str) can be Blurred, the large one (not_dex) cannot.
        var small = Variety(4501);
        var otherSmall = Variety(4497);
        var large = Crab;
        Assert.Equal(small.Name, large.Name);
        var shared = new Dictionary<string, MonsterModSet>
        {
            [small.Name] = Rolls.Roll(small, 2, MonsterRarity.Magic, new Random(1), count: 1, forced: [Mod("MonsterModIncreasedEvasionRating")]),
        };
        var rng = new Random(13);
        Assert.Same(shared[small.Name], Rolls.SharedMagic(shared, otherSmall, 2, rng));
        var forLarge = Rolls.SharedMagic(shared, large, 2, rng);
        Assert.NotSame(shared[small.Name], forLarge);
        Assert.DoesNotContain(Affixes(forLarge), m => m.Id == "MonsterModIncreasedEvasionRating");
        Assert.Same(forLarge, Rolls.SharedMagic(shared, large, 2, rng));   // the large ones share theirs
        // The Coast's cannibals: of Rime has fire_affinity 0, so a fire thrower never shares it.
        var cannibal = Cannibal;
        var thrower = Variety(2849);
        Assert.Contains("fire_affinity", Rolls.TagsOf(thrower, MonsterRarity.Magic, false));
        shared[cannibal.Name] = Rolls.Roll(cannibal, 2, MonsterRarity.Magic, new Random(1), count: 1, forced: [Mod("MonsterModExtraColdDamage")]);
        for (int i = 0; i < 20; i++)
        {
            var forThrower = Rolls.SharedMagic(new Dictionary<string, MonsterModSet>(shared), thrower, 2, new Random(i));
            Assert.DoesNotContain(Affixes(forThrower), m => m.Id == "MonsterModExtraColdDamage");
        }
    }

    [Fact]
    public void A_regenerated_life_is_not_sent_after_a_kill()
    {
        var hydra = Monster(With(MonsterRarity.Rare, 60, "MonsterModLifeRegeneration"));
        hydra.Life = 500;
        var monsters = new Dictionary<uint, SpawnedMonster> { [7] = hydra };
        var (id, life) = Assert.Single(Program.RegenerateMonstersLocked(monsters, 1));
        Assert.True(Program.RegenStillCurrent(monsters, id, life));
        // A kill lands before the 0x1b1 goes out: the old life would revive it on the client.
        hydra.Life = 0;
        hydra.Dead = true;
        Assert.False(Program.RegenStillCurrent(monsters, id, life));
        // A hit in between changed the life: its own 0x1b1 is the current one.
        hydra.Dead = false;
        hydra.Life = life - 100;
        Assert.False(Program.RegenStillCurrent(monsters, id, life));
        Assert.False(Program.RegenStillCurrent(new Dictionary<uint, SpawnedMonster>(), id, life));   // despawned
    }

    // ---- Unsupported mods ----

    [Theory]
    [InlineData("MonsterModIncreasedLife", "Full")]
    [InlineData("MonsterModResistantToFire", "Full")]           // the ignite duration: no ailments on monsters
    [InlineData("MonsterModHexproof", "Full")]                  // no curses to stop
    [InlineData("MonsterModExtraFireDamage", "Partial")]        // the fire exposure is missing
    [InlineData("MonsterModSpeedAura", "Partial")]              // the aura's allies are missing
    [InlineData("MonsterModAccuracyAndCritAura", "Partial")]    // its accuracy acts; its increased crit has no base
    [InlineData("MonsterModIncreasedCriticalStrikeChance", "None")]  // of Incision: increases a 0% base
    [InlineData("MonsterModAlwaysCrit", "Full")]
    [InlineData("MonsterModBerserker", "None")]                 // its buff's trigger is server logic
    [InlineData("MonsterModInfernoBolt", "None")]               // a retaliation skill
    [InlineData("MonsterModBurningGroundOnDeath", "None")]      // a monster on death
    [InlineData("MonsterModApplyIgnite", "None")]               // ailments on the player
    [InlineData("MonsterModGainCharges", "None")]               // no stats: charges
    [InlineData("MonsterModEnergyShieldAura", "None")]          // monster energy shield
    public void Each_mod_is_classified_by_what_the_server_can_honour(string id, string expected)
    {
        Assert.Equal(expected, MonsterModCatalog.SupportOf(Mod(id)).ToString());
    }

    [Fact]
    public void Unsupported_mods_stay_out_of_the_pool_unless_asked_for()
    {
        var tags = Rolls.TagsOf(Corpse, MonsterRarity.Rare, false);
        var pool = Rolls.Pool(tags, 80);
        Assert.All(pool, p => Assert.NotEqual(MonsterModCatalog.Support.None, MonsterModCatalog.SupportOf(p.Mod)));
        var all = Rolls.Pool(tags, 80, unsupported: true);
        Assert.Contains(all, p => p.Mod.Id == "MonsterModInfernoBolt");
        Assert.True(all.Count > pool.Count);
        var everything = new MonsterModCatalog(Mods, Data()) { RollUnsupported = true };
        var rng = new Random(11);
        Assert.Contains(Enumerable.Range(0, 300).SelectMany(_ => Affixes(everything.Roll(Corpse, 80, MonsterRarity.Rare, rng, count: 4))),
            m => MonsterModCatalog.SupportOf(m) == MonsterModCatalog.Support.None);
    }

    [Fact]
    public void Without_mods_dat_the_rarity_still_gives_its_life_and_damage()
    {
        var empty = new MonsterModCatalog(new ModCatalog("no-such-folder"), "no-such-folder");
        var set = empty.Roll(Corpse, 1, MonsterRarity.Unique, new Random(1));
        Assert.Empty(set.Mods);
        Assert.Equal(MonsterCatalog.LifeWithRarity(39, MonsterRarity.Unique), set.Effects.MaxLife(39));
        var (damage, speed) = MonsterCatalog.RarityAttack(MonsterRarity.Unique);
        Assert.Equal(Math.Round(100 * damage), set.Effects.Hit(DamagePacket.Of(DamageType.Physical, 100, 100))[DamageType.Physical].Min);
        Assert.Equal(1.0 / speed, set.Effects.AttackSeconds(1), 6);
    }
}
