using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// The player stat snapshot reuses its tail while the tail's inputs are the same (Modules/Core/Game/Player/StatSnapshotCache.cs).
// Every stat source the game changes is changed here in turn, as the game changes it; after each change the snapshot
// must be the one a connection without a reused tail computes (same stats, same order), a repeated snapshot must reuse
// the tail, and the verification mode (on in every Gateway test) compares that reuse with a fresh tail. The connection's
// clock is stopped, so nothing timed changes between the snapshots of a step.
public class StatSnapshotTailTests
{
    [Fact]
    public void Conditional_equipment_cache_rebuilds_when_runtime_condition_inputs_change()
    {
        using var setup = new SnapshotSetup();
        var conn = Connection("ConditionalCache" + Guid.NewGuid().ToString("N"));
        var equipment = Array.Empty<(byte Row, InventoryItem Item)>();
        void Resolve() => Game("EquipmentStatSources", conn, equipment, null, null);
        Resolve();
        var cached = conn.StatSourcesCache;
        Resolve();
        Assert.Same(cached, conn.StatSourcesCache);

        lock (conn.Monsters)
            conn.Auras[1] = new AuraCatalog.Aura(1, 1, "Grace", "Grace", 1, 1, 0, 0);
        Resolve();
        Assert.NotSame(cached, conn.StatSourcesCache);

        // Exercise final combat and published evasion, not just the raw-source cache. A native Kintsugi check caught
        // the previous implementation updating that cache while leaving Pools.Evasion unchanged.
        var live = Connection("ConditionalFinal" + Guid.NewGuid().ToString("N"));
        var inventory = (PlayerInventories)Game("ItemsOf", live)!;
        var items = (ItemCatalog)Game("ItemsFor", live)!;
        var mod = items.Mods.All.Single(m => m.Id == "IncreasedEvasionIfHitRecentlyUnique___1")
            .Roll(ModSection.Explicit, new Random(1));
        var body = ItemCatalog.Create(items.Find("Exquisite Leather")!, random: new Random(1)) with
        {
            Mods = [mod with { Values = [100] }],
        };
        live.PlayerStats[Program.Stats.Key("additional_dexterity")!.Value] = 300;
        inventory.Add(PlayerInventories.BodyArmour, 0, 0, body);
        live.Pools = Pools(live, 85);
        int baseline = (int)Game("PlayerEvasionBase", live, live.Clock.Elapsed.TotalSeconds)!;
        Assert.True(baseline > 0);
        live.RecentEvents.Record(StatConditions.HitEnemy, live.Clock.Elapsed.TotalSeconds);
        Assert.Equal(baseline, (int)Game("PlayerEvasionBase", live, live.Clock.Elapsed.TotalSeconds)!);
        live.RecentEvents.Record(StatConditions.BeenHit, live.Clock.Elapsed.TotalSeconds);
        int active = (int)Game("PlayerEvasionBase", live, live.Clock.Elapsed.TotalSeconds)!;
        Assert.True(active > baseline);
        Assert.Equal(active, Snapshot(live)[Program.Stats.Key("evasion_rating")!.Value]);
        var activePools = live.Pools;
        Snapshot(live);
        Assert.Same(activePools, live.Pools);
        double now = live.Clock.Elapsed.TotalSeconds;
        live.RecentEvents.Record(StatConditions.BeenHit, now - 3.999);
        Assert.Equal(active, (int)Game("PlayerEvasionBase", live, now)!);
        live.RecentEvents.Record(StatConditions.BeenHit, now - 4);
        Assert.Equal(baseline, (int)Game("PlayerEvasionBase", live, now)!);
        live.RecentEvents.Record(StatConditions.BeenHit, now);
        Assert.Equal(active, (int)Game("PlayerEvasionBase", live, now)!);
        live.InvalidateIncomingHits();
        Assert.Equal(baseline, (int)Game("PlayerEvasionBase", live, now)!);
        live.RecentEvents.Record(StatConditions.BeenHit, now);
        Assert.Equal(active, (int)Game("PlayerEvasionBase", live, now)!);
        live.RecentEvents.Clear();
        object states = Rules.GetField("ConditionalItemPools", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        states.GetType().GetMethod("Remove", [typeof(ClientState)])!.Invoke(states, [live]);
        Assert.Equal(baseline, (int)Game("PlayerEvasionBase", live, now)!);
        // Spell sweeps and projectile kills use shared commits. Minion hits must not activate the owner's hit event.
        var target = new SpawnedMonster("condition-target", new PlayerWalk(10, 10, 30), 20, 1, 100);
        Game("ApplyMonsterHitLocked", live, target, 1, null, null, (10, 10), default(global::Game.Cull));
        Assert.False(live.RecentEvents.Happened(StatConditions.HitEnemy, now));
        Game("ApplyMonsterHitLocked", live, target, 1, null, null, null, default(global::Game.Cull));
        Assert.True(live.RecentEvents.Happened(StatConditions.HitEnemy, now));
        Game("ChargesOnKill", live, target);
        Assert.True(live.RecentEvents.Happened(StatConditions.KilledEnemy, now));
        var phaseRoll = items.Mods.All.Single(m => m.Id == "ChanceToDodgeAttacksWhilePhasingUnique___1")
            .Roll(ModSection.Explicit, new Random(1));
        var oldBody = inventory.Items(PlayerInventories.BodyArmour).Single();
        inventory.Remove(PlayerInventories.BodyArmour, oldBody.Id);
        inventory.Add(PlayerInventories.BodyArmour, 0, 0, body with { Mods = [.. body.Mods, phaseRoll with { Values = [50] }] });
        live.Phasing = true;
        int phased = (int)Game("PlayerEvasionBase", live, now)!;
        Assert.True(phased > baseline);
        live.Phasing = false;
        Assert.Equal(baseline, (int)Game("PlayerEvasionBase", live, now)!);
        live.PlayerStats[Program.Stats.Key("additional_intelligence")!.Value] = 100;
        Snapshot(live);
        int intelligence = live.Pools.Intelligence;
        var uniqueRoll = items.Mods.All.Single(m => m.Id == "IncreasedIntelligencePerUniqueUniqueRing14")
            .Roll(ModSection.Explicit, new Random(1));
        var ring = ItemCatalog.Create(items.Find("Paua Ring")!, random: new Random(1)) with
        {
            Rarity = ItemRarity.Unique, Mods = [uniqueRoll with { Values = [10] }],
        };
        var equippedRing = inventory.Add(6, 0, 0, ring); // Ring1, outside the flask/jewel inventories
        var firstUnique = Snapshot(live);
        Assert.Equal(10, firstUnique[Program.Stats.Key("intelligence_+%")!.Value]);
        Assert.True(live.Pools.Intelligence > intelligence);
        int firstIntelligence = live.Pools.Intelligence;
        var currentBody = inventory.Items(PlayerInventories.BodyArmour).Single();
        inventory.Remove(PlayerInventories.BodyArmour, currentBody.Id);
        inventory.Add(PlayerInventories.BodyArmour, 0, 0, currentBody.Item with { Rarity = ItemRarity.Unique });
        Assert.Equal(20, Snapshot(live)[Program.Stats.Key("intelligence_+%")!.Value]);
        Assert.True(live.Pools.Intelligence > firstIntelligence);
        inventory.Remove(6, equippedRing.Id);
        Snapshot(live);
        Assert.Equal(intelligence, live.Pools.Intelligence);
        cached = conn.StatSourcesCache;

        lock (conn.Monsters) conn.RecentEvents.Record(StatConditions.Crit, conn.Clock.Elapsed.TotalSeconds);
        Resolve();
        Assert.NotSame(cached, conn.StatSourcesCache);
        cached = conn.StatSourcesCache;

        lock (conn.Monsters) conn.RecentEvents.Record(StatConditions.Crit, conn.Clock.Elapsed.TotalSeconds + .1);
        Resolve();
        Assert.Same(cached, conn.StatSourcesCache);
        lock (conn.Monsters) conn.RecentEvents.Clear();
        Resolve();
        Assert.NotSame(cached, conn.StatSourcesCache);
        cached = conn.StatSourcesCache;

        lock (conn.Monsters) conn.Pools = conn.Pools with { Strength = conn.Pools.Strength + 1 };
        Resolve();
        Assert.NotSame(cached, conn.StatSourcesCache);

        // The same cache/final-pool path sees Arborix's accepted Dash window and its exact four-second expiry.
        var dashConn = Connection("ConditionalDash" + Guid.NewGuid().ToString("N"));
        var dashInventory = (PlayerInventories)Game("ItemsOf", dashConn)!;
        var dashItems = (ItemCatalog)Game("ItemsFor", dashConn)!;
        dashConn.PlayerStats[Program.Stats.Key("additional_dexterity")!.Value] = 500;
        dashConn.PlayerStats[Program.Stats.Key("additional_strength")!.Value] = 500;
        dashConn.PlayerStats[Program.Stats.Key("additional_intelligence")!.Value] = 500;
        dashInventory.Add(PlayerInventories.Weapon1, 0, 0,
            new UniqueCatalog(dashItems).Create("Arborix", new Random(504))!);
        dashConn.Pools = Pools(dashConn, 100);
        int dashBaseline = dashConn.Pools.Armour;
        var dashCache = dashConn.StatSourcesCache;
        double dashAt = dashConn.Clock.Elapsed.TotalSeconds;
        lock (dashConn.Monsters) dashConn.RecentEvents.Record(StatConditions.UsedDash, dashAt);
        var dashRecent = Snapshot(dashConn);
        Assert.NotSame(dashCache, dashConn.StatSourcesCache);
        Assert.True(Pools(dashConn, 100).Armour > dashBaseline);
        Assert.Equal(0, dashRecent.GetValueOrDefault(Program.Stats.Key("number_of_additional_arrows")!.Value));
        lock (dashConn.Monsters) dashConn.RecentEvents.Clear();
        lock (dashConn.Monsters) dashConn.RecentEvents.Record(StatConditions.UsedDash, dashAt - 4);
        Assert.Equal(dashBaseline, Pools(dashConn, 100).Armour);
        Assert.Equal(2, Snapshot(dashConn).GetValueOrDefault(
            Program.Stats.Key("number_of_additional_arrows")!.Value));
        lock (dashConn.Monsters) dashConn.RecentEvents.Clear();
        lock (dashConn.Monsters) dashConn.RecentEvents.Record(StatConditions.UsedDash, dashAt - 3.999);
        Assert.True(Pools(dashConn, 100).Armour > dashBaseline);
        Assert.Equal(0, Snapshot(dashConn).GetValueOrDefault(
            Program.Stats.Key("number_of_additional_arrows")!.Value));

        Per_charge_leaves_follow_transitions_equipment_and_cache_inputs();
        Ordinary_slot_predicates_follow_live_equipment_and_consumers();
    }

    void Per_charge_leaves_follow_transitions_equipment_and_cache_inputs()
    {
        var conn = Connection("ChargeLeaves" + Guid.NewGuid().ToString("N"));
        var items = (ItemCatalog)Game("ItemsFor", conn)!;
        var inventory = (PlayerInventories)Game("ItemsOf", conn)!;
        var uniques = new UniqueCatalog(items);
        uint Key(string name) => Program.Stats.Key(name) ?? throw new InvalidOperationException(name);
        int Get(Dictionary<uint, int> stats, string name) =>
            Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        double Now() => conn.Clock.Elapsed.TotalSeconds;

        conn.Pools = conn.Pools with { Level = 90 };
        conn.PlayerStats[Key("additional_strength")] = 500;
        conn.PlayerStats[Key("additional_dexterity")] = 500;
        conn.PlayerStats[Key("additional_intelligence")] = 500;
        var shimmeron = uniques.Create("Shimmeron", new Random(1))!;
        var fenumus = uniques.Create("Fenumus' Toxins", new Random(1))!;
        var auxium = uniques.Create("Auxium", new Random(1))!;
        var equippedShimmeron = inventory.Add(PlayerInventories.Weapon1, 0, 0, shimmeron);
        inventory.Add(PlayerInventories.Helmet, 0, 0, fenumus);
        inventory.Add(PlayerInventories.Belt, 0, 0, auxium);

        var zero = Snapshot(conn);
        Assert.Equal(0, Get(zero, "additional_base_critical_strike_chance"));
        Assert.Equal(0, Get(zero, "base_chance_to_poison_on_hit_%"));
        Assert.Equal(0, Get(zero, "base_poison_damage_+%"));
        Assert.Equal(0, Get(zero, "base_mana_leech_from_attack_damage_permyriad"));
        int attackCritBaseline = AddedBaseCriticalStrike.Chance(500, 0, spell: false,
            (string name) => Get(zero, name));
        int spellCritBaseline = AddedBaseCriticalStrike.Chance(500, 0, spell: true,
            (string name) => Get(zero, name));

        void SetCharges(ChargeType type, int count, double at)
        {
            lock (conn.Monsters)
                global::Game.ChargesOf(conn).Admit(at, [new ChargeEvent.Set(type, count)], _ => 0, () => default,
                    alive: true, new Random(1));
        }

        SetCharges(ChargeType.Power, 1, Now());
        var one = Snapshot(conn);
        SetCharges(ChargeType.Power, 3, Now());
        var three = Snapshot(conn);
        var raw = ItemModStats.Sum([shimmeron, fenumus, auxium], Program.Items.Mods);
        Assert.Equal(raw.GetValueOrDefault("additional_critical_strike_chance_per_power_charge_permyriad"),
            Get(one, "additional_base_critical_strike_chance"));
        Assert.Equal(3 * raw.GetValueOrDefault("additional_critical_strike_chance_per_power_charge_permyriad"),
            Get(three, "additional_base_critical_strike_chance"));
        Assert.Equal(3 * raw.GetValueOrDefault("chance_to_poison_on_hit_%_per_power_charge"),
            Get(three, "base_chance_to_poison_on_hit_%"));
        Assert.Equal(3 * raw.GetValueOrDefault("poison_damage_+%_per_power_charge"),
            Get(three, "base_poison_damage_+%"));
        Assert.Equal(3 * raw.GetValueOrDefault("mana_leech_from_attack_damage_permyriad_per_power_charge"),
            Get(three, "base_mana_leech_from_attack_damage_permyriad"));
        int attackCrit = AddedBaseCriticalStrike.Chance(500, 0, spell: false,
            (string name) => Get(three, name));
        int spellCrit = AddedBaseCriticalStrike.Chance(500, 0, spell: true,
            (string name) => Get(three, name));
        Assert.Equal(attackCritBaseline + Get(three, "additional_base_critical_strike_chance"), attackCrit);
        Assert.Equal(spellCritBaseline + Get(three, "additional_base_critical_strike_chance"), spellCrit);
        var poisonSettings = MonsterPoisons.Settings.From([], "spell", (string name) => Get(three, name));
        Assert.Equal(Get(three, "base_chance_to_poison_on_hit_%"), poisonSettings.Chance);
        var attackLeech = LeechSources.From((string name) => Get(three, name), attack: true);
        var spellLeech = LeechSources.From((string name) => Get(three, name), attack: false);
        Assert.True(attackLeech.Mana[DamageType.Physical].Min > 0);
        Assert.Equal(0, spellLeech.Mana.MinTotal);

        // The unchanged snapshot reuses both source and charge-expansion caches without changing the final source view.
        var cached = conn.StatSourcesCache;
        long hits = Counter("StatSnapshotTailHits");
        var repeated = Snapshot(conn);
        Assert.True(Counter("StatSnapshotTailHits") > hits);
        Assert.Same(cached, conn.StatSourcesCache);
        Assert.True(SameInOrder(three, repeated));

        SetCharges(ChargeType.Power, 0, Now());
        var backToZero = Snapshot(conn);
        Assert.Equal(0, Get(backToZero, "additional_base_critical_strike_chance"));
        Assert.Equal(0, Get(backToZero, "base_chance_to_poison_on_hit_%"));
        Assert.Equal(0, Get(backToZero, "base_poison_damage_+%"));
        Assert.Equal(0, Get(backToZero, "base_mana_leech_from_attack_damage_permyriad"));
        Assert.Equal(attackCritBaseline, AddedBaseCriticalStrike.Chance(500, 0, spell: false,
            (string name) => Get(backToZero, name)));
        Assert.Equal(spellCritBaseline, AddedBaseCriticalStrike.Chance(500, 0, spell: true,
            (string name) => Get(backToZero, name)));

        SetCharges(ChargeType.Power, 3, Now());
        Assert.Equal(3 * raw.GetValueOrDefault("additional_critical_strike_chance_per_power_charge_permyriad"),
            Get(Snapshot(conn), "additional_base_critical_strike_chance"));
        lock (conn.Monsters) global::Game.ChargesOf(conn).Refresh(Now() + Charges.BaseDurationMs / 1000.0 + 0.001);
        Assert.Equal(0, Get(Snapshot(conn), "additional_base_critical_strike_chance"));

        SetCharges(ChargeType.Power, 3, Now());
        Assert.Equal(3 * raw.GetValueOrDefault("additional_critical_strike_chance_per_power_charge_permyriad"),
            Get(Snapshot(conn), "additional_base_critical_strike_chance"));
        cached = conn.StatSourcesCache;
        Assert.NotNull(inventory.Remove(PlayerInventories.Weapon1, equippedShimmeron.Id));
        var withoutShimmeron = Snapshot(conn);
        Assert.NotSame(cached, conn.StatSourcesCache);
        Assert.Equal(0, Get(withoutShimmeron, "additional_base_critical_strike_chance"));
        inventory.Add(PlayerInventories.Weapon1, 0, 0, shimmeron);
        uint maxPower = Key("max_power_charges");
        uint minimumPower = Key("base_minimum_power_charges");

        // A maximum is not a held count. A minimum is effective while zero charges are gained.
        SetCharges(ChargeType.Power, 0, Now());
        conn.PlayerStats[maxPower] = 3;
        var maximumOnly = Snapshot(conn);
        Assert.Equal(0, Get(maximumOnly, "additional_base_critical_strike_chance"));
        conn.PlayerStats[minimumPower] = 1;
        var minimum = Snapshot(conn);
        Assert.Equal(raw.GetValueOrDefault("additional_critical_strike_chance_per_power_charge_permyriad"),
            Get(minimum, "additional_base_critical_strike_chance"));
        conn.PlayerStats.Remove(minimumPower);
        conn.PlayerStats.Remove(maxPower);

        // Converting the type to Absorption Charges stops standard per-charge effects; removing the flag restores them.
        SetCharges(ChargeType.Power, 3, Now());
        var conversion = Key("gain_absorption_charges_instead_of_power_charges");
        conn.PlayerStats[conversion] = 1;
        Assert.Equal(0, Get(Snapshot(conn), "additional_base_critical_strike_chance"));
        conn.PlayerStats.Remove(conversion);
        Assert.Equal(3 * raw.GetValueOrDefault("additional_critical_strike_chance_per_power_charge_permyriad"),
            Get(Snapshot(conn), "additional_base_critical_strike_chance"));

        var maligaro = uniques.Create("Maligaro's Cruelty", new Random(1))!;
        var equippedMaligaro = inventory.Add(5, 0, 0, maligaro);
        SetCharges(ChargeType.Power, 3, Now());
        var maligaroStats = Snapshot(conn);
        var maligaroRaw = ItemModStats.Sum([shimmeron, fenumus, auxium, maligaro], Program.Items.Mods);
        Assert.Equal(3 * maligaroRaw.GetValueOrDefault("poison_duration_+%_per_power_charge"),
            Get(maligaroStats, "poison_duration_+%"));
        var poisonDuration = MonsterPoisons.Settings.From([], "spell",
            (string name) => Get(maligaroStats, name)).Seconds;
        Assert.Equal(2 * (1 + Get(maligaroStats, "poison_duration_+%") / 100.0), poisonDuration, 10);
        Assert.NotNull(inventory.Remove(5, equippedMaligaro.Id));
        Assert.Equal(0, Get(Snapshot(conn), "poison_duration_+%"));

        var badge = uniques.Create("Replica Badge of the Brotherhood", new Random(1))!;
        inventory.Add(5, 0, 0, badge);
        SetCharges(ChargeType.Power, 0, Now());
        SetCharges(ChargeType.Endurance, 3, Now());
        var badgeStats = Snapshot(conn);
        var badgeRaw = ItemModStats.Sum([shimmeron, fenumus, auxium, badge], Program.Items.Mods);
        int addedFortification = 3 * badgeRaw.GetValueOrDefault("base_max_fortification_per_endurance_charge");
        Assert.Equal(addedFortification, Get(badgeStats, "base_max_fortification"));
        object fortification = Game("FortificationOf", conn)!;
        var fortificationValue = fortification.GetType().GetMethod("Observe")!.Invoke(
            fortification, [Now(), (Func<string, int>)(name => Get(badgeStats, name))])!;
        Assert.Equal(20 + addedFortification, fortificationValue.GetType().GetProperty("Maximum")!.GetValue(fortificationValue));
        inventory.Remove(5, inventory.Items(5).Single().Id);

        var aylardex = uniques.Create("The Aylardex", new Random(1))!;
        inventory.Add(5, 0, 0, aylardex);
        SetCharges(ChargeType.Endurance, 0, Now());
        SetCharges(ChargeType.Power, 3, Now());
        var aylardexStats = Snapshot(conn);
        var aylardexRaw = ItemModStats.Sum([shimmeron, fenumus, auxium, aylardex], Program.Items.Mods);
        int manaShare = 3 * aylardexRaw.GetValueOrDefault("damage_taken_goes_to_mana_%_per_power_charge");
        Assert.Equal(manaShare, Get(aylardexStats, "base_damage_removed_from_mana_before_life_%"));
        Assert.Equal(manaShare, PlayerDamageTaken.FromMana(100, 0,
            DamagePacket.Of(DamageType.Fire, 100, 100), 1000, (string name) => Get(aylardexStats, name)));
    }

    void Ordinary_slot_predicates_follow_live_equipment_and_consumers()
    {
        var conn = Connection("SlotPredicates" + Guid.NewGuid().ToString("N"));
        var items = (ItemCatalog)Game("ItemsFor", conn)!;
        var inventory = (PlayerInventories)Game("ItemsOf", conn)!;
        var uniques = new UniqueCatalog(items);
        uint Key(string name) => Program.Stats.Key(name) ?? throw new InvalidOperationException(name);
        int Get(Dictionary<uint, int> stats, string name) =>
            Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

        conn.Pools = conn.Pools with { Level = 90 };
        conn.PlayerStats[Key("additional_strength")] = 500;
        conn.PlayerStats[Key("additional_dexterity")] = 500;
        conn.PlayerStats[Key("additional_intelligence")] = 500;
        var whiteWind = uniques.Create("White Wind", new Random(1))!;
        inventory.Add(PlayerInventories.Weapon1, 0, 0, whiteWind);
        var emptyOffHand = Snapshot(conn);
        int coldDamage = Get(emptyOffHand, "cold_damage_+%");
        int suppression = Get(emptyOffHand, "base_spell_suppression_chance_%");
        Assert.True(coldDamage > 0);
        Assert.True(suppression > 0);
        Assert.Equal(suppression, conn.Pools.SpellSuppressionChance);
        var coldHit = PlayerHitDamage.Resolve(DamagePacket.Of(DamageType.Cold, 100, 100),
            (string name) => Get(emptyOffHand, name), new DamageConversion(), spell: false, melee: true);
        Assert.Equal(100 + coldDamage, coldHit.MinTotal);

        // Occupying the real off-hand row disables White Wind, including a quiver where shield/dual-wield flags are false.
        var quiverType = items.All.First(type => type.Class == "Quiver" && type.Supported);
        var quiver = inventory.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(quiverType, random: new Random(1)));
        var occupiedOffHand = Snapshot(conn);
        Assert.Equal(0, Get(occupiedOffHand, "cold_damage_+%"));
        Assert.Equal(0, Get(occupiedOffHand, "base_spell_suppression_chance_%"));
        Assert.Equal(0, conn.Pools.SpellSuppressionChance);
        Assert.NotNull(inventory.Remove(PlayerInventories.Offhand1, quiver.Id));
        Assert.Equal(coldDamage, Get(Snapshot(conn), "cold_damage_+%"));

        var viridi = uniques.Create("Viridi's Veil", new Random(1))!;
        inventory.Add(PlayerInventories.Helmet, 0, 0, viridi);
        var normalRing = ItemCatalog.Create(items.Find("Paua Ring")!, random: new Random(1)) with { Rarity = ItemRarity.Normal };
        var equippedNormalRing = inventory.Add(6, 0, 0, normalRing);
        var normalRings = Snapshot(conn);
        Assert.Equal(0, Get(normalRings, "base_enemy_extra_damage_rolls"));
        var magicRing = normalRing with { Rarity = ItemRarity.Magic };
        var equippedMagicRing = inventory.Add(7, 0, 0, magicRing);
        var magicRings = Snapshot(conn);
        Assert.Equal(-1, Get(magicRings, "base_enemy_extra_damage_rolls"));
        var gambler = GamblerModifiers.From((string name) => Get(magicRings, name));
        Assert.Equal(-1, gambler.EnemyDamageExtraRolls);
        var range = DamagePacket.Of(DamageType.Fire, 1, 1000);
        bool strictlyLower = false;
        for (int seed = 1; seed <= 8; seed++)
        {
            int ordinaryRoll = (int)GamblerModifiers.From(_ => 0).RollIncomingHit(range, new Random(seed)).MinTotal;
            int unluckyRoll = (int)gambler.RollIncomingHit(range, new Random(seed)).MinTotal;
            Assert.True(unluckyRoll <= ordinaryRoll);
            strictlyLower |= unluckyRoll < ordinaryRoll;
        }
        Assert.True(strictlyLower);
        Assert.NotNull(inventory.Remove(7, equippedMagicRing.Id));
        Assert.Equal(0, Get(Snapshot(conn), "base_enemy_extra_damage_rolls"));

        var ascetic = uniques.Create("The Ascetic", new Random(1))!;
        inventory.Add(5, 0, 0, ascetic);
        var normalItem = Snapshot(conn);
        int rarity = Get(normalItem, "item_found_rarity_+%");
        Assert.True(rarity > 0);
        Assert.Equal(0, Get(normalItem, "item_found_quantity_+%"));
        int rarityWithNormal = global::Game.IncreasedItemRarity(conn);
        double quantityWithNormal = global::Game.ItemQuantityFactor(conn);
        Assert.NotNull(inventory.Remove(6, equippedNormalRing.Id));
        inventory.Add(6, 0, 0, magicRing);
        var magicItem = Snapshot(conn);
        int quantity = Get(magicItem, "item_found_quantity_+%");
        Assert.True(quantity > 0);
        Assert.Equal(0, Get(magicItem, "item_found_rarity_+%"));
        Assert.Equal(quantityWithNormal * (100 + quantity) / 100.0, global::Game.ItemQuantityFactor(conn), 10);
        Assert.Equal(rarityWithNormal - rarity, global::Game.IncreasedItemRarity(conn));
    }

    [Fact]
    public void A_reused_tail_is_the_fresh_one_through_every_stat_source()
    {
        using var setup = new SnapshotSetup();
        Assert.True((bool)Rules.GetField("VerifyStatSnapshotTail", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!);
        string character = "StatTail" + Guid.NewGuid().ToString("N");
        var conn = Connection(character);
        var items = (ItemCatalog)Game("ItemsFor", conn)!;
        Dictionary<uint, int>? previous = null;
        uint Key(string name) => Program.Stats.Key(name) ?? throw new InvalidOperationException(name);
        double Now() => conn.Clock.Elapsed.TotalSeconds;
        void RebuildPools() { lock (conn.Monsters) conn.Pools = Pools(conn, conn.Pools.Level); }

        // changes: whether this step must change the snapshot (a step that only moves an input the tail reads, such as
        // the stored life, may leave every published value as it was).
        void Step(string what, bool changes = true)
        {
            long misses = Counter("StatSnapshotTailMisses"), hits = Counter("StatSnapshotTailHits");
            var first = Snapshot(conn);
            var again = Snapshot(conn);
            Assert.True(Counter("StatSnapshotTailHits") > hits, $"{what}: the unchanged snapshot reused its tail");
            ForgetTail(conn);
            var fresh = Snapshot(conn);
            Assert.True(SameInOrder(fresh, first), $"{what}: {Differences(fresh, first)}");
            Assert.True(SameInOrder(fresh, again), $"{what} (reused): {Differences(fresh, again)}");
            if (previous is not null && changes)
            {
                Assert.True(Counter("StatSnapshotTailMisses") > misses, $"{what}: the changed inputs computed the tail again");
                Assert.False(SameInOrder(previous, fresh), $"{what}: the snapshot did not change");
            }
            previous = fresh;
        }

        Step("start");
        lock (conn.Monsters) conn.PlayerStats[Key("critical_strike_chance_+%")] = 50;
        Step("/set_stat");
        lock (conn.Monsters) conn.PlayerStats.Remove(Key("critical_strike_chance_+%"));
        Step("/set_stat removed");
        lock (conn.Monsters) conn.Pools = Pools(conn, 41);
        Step("level up");
        var tree = (PassiveTree)Game("PassivesFor", conn)!;
        ushort node = Enumerable.Range(1, ushort.MaxValue - 1).Select(i => (ushort)i)
            .First(i => tree.Find(i) is { Stats.Count: > 0, IsAscendancy: false, IsStart: false, IsKeystone: false, IsMastery: false,
                IsJewelSocket: false, IsProxy: false, IsExpansion: false });
        lock (conn.Monsters) conn.Passives.Add(node);
        RebuildPools();
        Step("passive allocated");
        lock (conn.Monsters) conn.Passives.Remove(node);
        RebuildPools();
        Step("passive refunded");

        var inventory = (PlayerInventories)Game("ItemsOf", conn)!;
        var wand = inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(items.Find("Driftwood Wand")!, random: new Random(1)));
        RebuildPools();
        Step("main hand equipped");
        var offWand = inventory.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(items.Find("Goat's Horn")!, random: new Random(1)));
        RebuildPools();
        Step("off hand equipped (dual wield)");
        var gem = ItemCatalog.Create(items.Find("Fireball")!, random: new Random(1));
        Assert.NotNull(inventory.Remove(PlayerInventories.Weapon1, wand.Id));
        wand = inventory.Add(PlayerInventories.Weapon1, 0, 0, wand.Item with { Sockets = [new ItemSocket(SocketColour.Blue, gem)] });
        RebuildPools();
        Step("gem socketed", changes: false);
        Assert.NotNull(inventory.Remove(PlayerInventories.Offhand1, offWand.Id));
        inventory.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(items.Find("Imbued Wand")!, random: new Random(1)));
        RebuildPools();
        Step("off hand swapped for one whose requirements are not met");
        Assert.NotNull(inventory.Remove(PlayerInventories.Weapon1, wand.Id));
        RebuildPools();
        Step("main hand unequipped");

        lock (conn.Monsters) conn.PlayerLife = 1;
        Step("low life");
        lock (conn.Monsters) conn.PlayerLife = conn.PlayerMaxLife;
        Step("full life");
        lock (conn.Monsters) conn.BloodCharges = 2;
        Step("blood charges");
        void SetFrenzy(bool gain)
        {
            lock (conn.Monsters)
            {
                object session = Game("ChargesOf", conn)!;
                var state = (Charges.State)session.GetType().GetProperty("State")!.GetValue(session)!;
                var limits = (Charges.Limits)session.GetType().GetMethod("Limits")!.Invoke(session, [ChargeType.Frenzy])!;
                if (gain) state.Gain(ChargeType.Frenzy, 2, Now(), limits);
                else state.Set(ChargeType.Frenzy, 0, Now(), limits);
            }
        }
        SetFrenzy(gain: true);
        Step("frenzy charges gained");
        SetFrenzy(gain: false);
        Step("frenzy charges lost");
        lock (conn.Monsters)
        {
            object fortification = Game("FortificationOf", conn)!;
            Assert.True((bool)fortification.GetType().GetMethod("Hit")!.Invoke(fortification, [Now(), 1_000_000m, 1000m, MonsterRarity.Unique,
                (Func<string, int>)(name => name == "fortify_on_hit" ? 1 : 0), new Random(1)])!);
        }
        Step("fortified");
        lock (conn.Monsters) { object f = Game("FortificationOf", conn)!; f.GetType().GetMethod("Clear")!.Invoke(f, []); }
        Step("fortification gone");
        lock (conn.Monsters) conn.PlayerLife = conn.PlayerMaxLife / 2;
        Step("hit", changes: false);
        lock (conn.Monsters)
        {
            object leech = Game("LeechOf", conn)!;
            leech.GetType().GetMethod("Hit")!.Invoke(leech, [Now(), (500.0, 0.0, 0.0), false, false, false, true]);
        }
        Step("leeching");
        lock (conn.Monsters)
        {
            conn.PlayerLife = conn.PlayerMaxLife;
            object leech = Game("LeechOf", conn)!;
            leech.GetType().GetMethod("Clear")!.Invoke(leech, [Now()]);
        }
        Step("leech stopped");
        lock (conn.Monsters)
            conn.ElementalAilments.Apply(DamagePacket.Of(DamageType.Cold, 400, 400), DamagePacket.Of(DamageType.Cold, 400, 400),
                false, conn.PlayerMaxLife, Now(), _ => 0, new Random(1));
        Step("chilled");
        lock (conn.Monsters) conn.ElementalAilments.Clear();
        Step("chill gone");

        var diamond = items.Find("Diamond Flask")!;
        conn.UtilityFlaskEffects.Start(new UtilityFlasks.Active(1, 0, diamond.Name, items.UtilityFlaskOf(diamond)!, 1, null,
            [new UtilityFlasks.Source("buff", "critical_strike_chance_+%", 100)], Now(), Now() + 60));
        RebuildPools();
        Step("flask used");
        conn.UtilityFlaskEffects.End(Now(), all: true, out _);
        RebuildPools();
        Step("flask effect ended");

        lock (conn.Monsters) conn.Pools = conn.Pools with { EnergyShield = 200 };
        conn.EnergyShield.SetMaximum(200, refill: true);
        Step("energy shield");
        lock (conn.Monsters)
        {
            conn.EnergyShield.Damage(100, false, Now());
            conn.EnergyShield.Advance(Now() + 2.1, Now() + 2.2, _ => 0, alive: true);
            Assert.True(conn.EnergyShield.Recharging);
        }
        Step("energy shield recharging");

        // A new area is a new connection: its snapshot starts without a tail of its own.
        conn = Connection(character);
        previous = null;
        long before = Counter("StatSnapshotTailMisses");
        Snapshot(conn);
        Assert.True(Counter("StatSnapshotTailMisses") > before, "a new area's connection computes its tail");
        Step("area changed");
        Assert.Equal(0, Counter("StatSnapshotTailMismatches"));
    }

    [Fact]
    public void Warm_stages_still_observe_rampage_and_reconcile_ailments_with_current_action_speed()
    {
        using var setup = new SnapshotSetup();
        var conn = Connection("StatEffects" + Guid.NewGuid().ToString("N"));
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        conn.PlayerStats[Key(RampageSession.Source)] = 1;
        Snapshot(conn);
        conn.Rampage.Restore(new(40, 5), conn.Clock.Elapsed.TotalSeconds);
        var rampaging = Snapshot(conn);
        Assert.Equal(40, rampaging[Key("current_rampage_stacks")]);
        Snapshot(conn); // All three pure stages are warm.
        // An area's frozen transfer time can move independently of every cached input.
        conn.LeavingArea = true;
        conn.RampageTransferAt = conn.Clock.Elapsed.TotalSeconds + 6;
        var expired = Snapshot(conn);
        Assert.Equal(0, conn.Rampage.Current.Stacks);
        Assert.Equal(0, expired[Key("current_rampage_stacks")]);
        conn.LeavingArea = false;
        conn.RampageTransferAt = null;

        void Chill() => conn.ElementalAilments.Apply(DamagePacket.Of(DamageType.Cold, 400, 400),
            DamagePacket.Of(DamageType.Cold, 400, 400), false, conn.PlayerMaxLife,
            conn.Clock.Elapsed.TotalSeconds, _ => 0, new Random(1));
        conn.PlayerStats[Key("action_speed_-%")] = -20;
        Chill();
        var chilled = Snapshot(conn);
        Assert.Equal(-20, chilled[Key("action_speed_-%")]); // Explicit overrides still replace the final row.
        Assert.Equal(-10, chilled[Key("intermediary_combined_action_speed_+%")]); // 30% chill + 20% faster.
        conn.PlayerStats[Key("action_speed_cannot_be_reduced_below_base")] = 1;
        var floored = Snapshot(conn);
        Assert.Equal(0, floored[Key("intermediary_combined_action_speed_+%")]);
        conn.PlayerStats[Key("cannot_be_chilled")] = 1;
        Snapshot(conn); // Reconcile removes chill and warms all stages.
        Chill(); // Add an effect after the warm snapshot without changing any stat input.
        Snapshot(conn);
        Assert.Equal(0, conn.ElementalAilments.Snapshot(conn.Clock.Elapsed.TotalSeconds).Chill);
        Assert.Equal(0, Counter("StatSnapshotTailMismatches"));
    }

    [Fact]
    public void Wand_mastery_points_apply_only_while_wielding_a_wand()
    {
        using var setup = new SnapshotSetup();
        var conn = Connection("WandMastery" + Guid.NewGuid().ToString("N"));
        var items = (ItemCatalog)Game("ItemsFor", conn)!;
        var tree = (PassiveTree)Game("PassivesFor", conn)!;
        const ushort node = 53828; // Wand Mastery
        var effects = tree.EffectsFor(node);
        Assert.Equal(6, effects.Count);
        ushort Effect(string id) => effects.Single(e => e.Id == id).Hash;
        uint Key(string name) => Program.Stats.Key(name) ?? throw new InvalidOperationException(name);
        void Rebuild() { lock (conn.Monsters) conn.Pools = Pools(conn, conn.Pools.Level); }
        void Select(string id)
        {
            lock (conn.Monsters)
            {
                if (!conn.Passives.Contains(node)) conn.Passives.Add(node);
                // The production path replaces the dictionary; the stat-source cache keys it by reference.
                conn.MasteryEffects = new(conn.MasteryEffects) { [node] = Effect(id) };
            }
            Rebuild();
        }

        var inventory = (PlayerInventories)Game("ItemsOf", conn)!;
        var wand = inventory.Add(PlayerInventories.Weapon1, 0, 0,
            ItemCatalog.Create(items.Find("Driftwood Wand")!, random: new Random(1)));
        Rebuild();
        int baseAccuracy = conn.Pools.Accuracy;

        // Wand Attacks fire an additional Projectile.
        Select("Wand3");
        Assert.Equal(1, Snapshot(conn).GetValueOrDefault(Key("attacks_number_of_additional_projectiles")));

        // 0.5% of Attack Damage Leeched as Life and Mana: a generic mastery effect (its stats carry no wand
        // condition), so it applies with any weapon and is not one of the points the wand gates.
        Select("Wand5");
        Assert.Equal(50, Snapshot(conn).GetValueOrDefault(Key("base_life_leech_from_attack_damage_permyriad")));
        Assert.Equal(50, Snapshot(conn).GetValueOrDefault(Key("base_mana_leech_from_attack_damage_permyriad")));

        // Gain Accuracy Rating equal to Intelligence: the flat accuracy is the character's intelligence.
        Select("Wand6");
        Assert.Equal(baseAccuracy + conn.Pools.Intelligence, conn.Pools.Accuracy);

        // Without a wand the wand-only effects stop applying.
        Assert.NotNull(inventory.Remove(PlayerInventories.Weapon1, wand.Id));
        Rebuild();
        Assert.Equal(0, Snapshot(conn).GetValueOrDefault(Key("attacks_number_of_additional_projectiles")));
        Assert.Equal(baseAccuracy, conn.Pools.Accuracy);
    }

    // Keep the snapshot test's rules, saves and lifetime scoped. Starting the whole module host would register the
    // optional Phrecia provider for unrelated admission tests and retain a database those tests can close.
    sealed class SnapshotSetup : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), $"localexile-snapshot-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly GameServer.Modding.IModuleLifetime? previousLifetime = global::Game.CombatLifetime;
        readonly bool previousVerify = global::Game.VerifyStatSnapshotTail;
        readonly ModuleLifetime lifetime = new("Game");
        public SnapshotSetup()
        {
            Saves.Open(path);
            global::Game.VerifyStatSnapshotTail = true;
            global::Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        }
        public void Dispose()
        {
            lifetime.Stop(ex => throw ex);
            global::Game.CombatLifetime = previousLifetime;
            global::Game.VerifyStatSnapshotTail = previousVerify;
            Program.SetGameLogic(previousLogic!);
            GameServer.Gateway.Tests.TestEnvironment.ReleaseSaves(path);
        }
    }

    static Type Rules => typeof(global::Game);

    // A rule of the Game module, by name and argument count.
    static object? Game(string method, params object?[] args) =>
        Rules.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(null, args);

    static long Counter(string name) => (long)Rules.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    static CharacterStats.Pools Pools(ClientState conn, int level) => (CharacterStats.Pools)Game("PoolsFor", conn, level)!;

    // The connection's reused tail goes, as on a connection that never had one.
    static void ForgetTail(ClientState conn)
    {
        object tails = Rules.GetField("StatSnapshotTails", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        tails.GetType().GetMethod("Remove", [typeof(ClientState)])!.Invoke(tails, [conn]);
    }

    static ClientState Connection(string character)
    {
        var conn = new ClientState(AreaLayouts.For(AreaLayouts.LioneyesWatch, 1), 1, 1)
        {
            Character = character, ClassRow = 1, PlayerSent = true, Instance = new(),
        };
        // Stopped, as the Escape menu's pause stops it: no timed value changes during the test.
        conn.Clock.Stop();
        conn.Pools = Pools(conn, 40);
        conn.PlayerLife = conn.Pools.Life;
        conn.PlayerMana = conn.Pools.Mana;
        return conn;
    }

    static Dictionary<uint, int> Snapshot(ClientState conn)
    {
        lock (conn.Monsters) return (Dictionary<uint, int>)Game("CharacterPlayerStatsCore", conn, null)!;
    }

    static bool SameInOrder(Dictionary<uint, int> a, Dictionary<uint, int> b) => a.SequenceEqual(b);

    static string Differences(Dictionary<uint, int> expected, Dictionary<uint, int> actual) =>
        string.Join(", ", expected.Keys.Union(actual.Keys).Where(k => expected.GetValueOrDefault(k) != actual.GetValueOrDefault(k))
            .Take(8).Select(k => $"{Program.Stats.Name(k)} {expected.GetValueOrDefault(k)} vs {actual.GetValueOrDefault(k)}"))
        + (expected.OrderBy(kv => kv.Key).SequenceEqual(actual.OrderBy(kv => kv.Key)) ? " (order only)" : "");
}
