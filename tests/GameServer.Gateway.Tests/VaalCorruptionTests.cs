using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class VaalCorruptionTests
{
    sealed class OutcomeRandom(int outcome, bool forceMaxLink = false, int? nextRange = null) : Random
    {
        readonly Random _fallback = new(17);
        bool _usedOutcome;

        public override int Next(int maxValue)
        {
            if (!_usedOutcome && maxValue == 4) { _usedOutcome = true; return outcome; }
            if (forceMaxLink && maxValue == 36) return 0;
            return _fallback.Next(maxValue);
        }

        public override int Next(int minValue, int maxValue) =>
            nextRange is int v && v >= minValue && v < maxValue ? v : _fallback.Next(minValue, maxValue);

        public override double NextDouble() => _fallback.NextDouble();
    }

    [Fact]
    public void Vaal_orb_equipment_and_flasks_follow_329_retail_outcomes_and_admissions()
    {
        using var fixture = new MinionUtilityTests.Fixture();
        var player = fixture.Player;
        var items = Game.ItemsFor(player);
        var rolls = Game.RollsFor(player);
        var vaalHash = items.Find(CurrencyEffects.VaalOrb)!.Hash;

        Item Apply(Item target, Random rng) =>
            Assert.IsType<Item>(Game.CurrencyResult(player, target, vaalHash, rng));

        // 1. Equipment (Armour, Weapons, Shields, Quivers, Jewellery — Normal/Rare/Unique): 4-way retail outcomes.
        var bodyType = items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!;
        var body = ItemCatalog.Create(bodyType, [new(SocketColour.White), new(SocketColour.White)], [2]) with { ItemLevel = 84 };

        // Outcome 0: no change other than Corrupted.
        var unchanged = Apply(body, new OutcomeRandom(0));
        Assert.Equal(body with { Corrupted = true }, unchanged);

        // Outcome 1 (3.29): one random white socket turns coloured (non-white).
        var coloured = Apply(body, new OutcomeRandom(1));
        Assert.True(coloured.Corrupted);
        Assert.Equal(1, coloured.Sockets.Count(s => s.Colour == SocketColour.White));
        Assert.Equal(1, coloured.Sockets.Count(s => SocketColours.IsColoured(s.Colour)));

        // Socketless gear (Ring, Amulet, Belt, Quiver) fizzles outcome 1 to no-change, and rolls GenType=5 implicits on outcome 2.
        foreach (string id in new[]
        {
            "Metadata/Items/Rings/Ring1",
            "Metadata/Items/Amulets/Amulet1",
            "Metadata/Items/Belts/Belt1",
            "Metadata/Items/Quivers/QuiverNew1",
        })
        {
            var t = items.Find(id)!;
            var gear = ItemCatalog.Create(t) with { ItemLevel = 84 };
            Assert.Equal(gear with { Corrupted = true }, Apply(gear, new OutcomeRandom(1)));
            var imp = Apply(gear, new OutcomeRandom(2));
            Assert.True(imp.Corrupted);
            var implicitMod = Assert.Single(imp.Mods, m => m.Section == ModSection.Implicit);
            var modDef = items.Mods.ByHash(implicitMod.Hash)!;
            Assert.Equal(ItemRolls.CorruptedImplicit, modDef.GenerationType);
            Assert.True(modDef.Level <= 84 && 84 <= modDef.MaxLevel);
        }

        // MaxLevel enforcement on Domain 1 GenerationType=5 implicits (low-tier flat damage implicits have MaxLevel=30).
        var wandType = items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!;
        var highPool = rolls.CorruptedImplicitPool(wandType, 84);
        var lowPool = rolls.CorruptedImplicitPool(wandType, 10);
        Assert.NotEmpty(highPool);
        Assert.All(highPool, p => Assert.True(p.Mod.Level <= 84 && 84 <= p.Mod.MaxLevel));
        Assert.Contains(lowPool, p => p.Mod.MaxLevel == 30);
        Assert.DoesNotContain(highPool, p => p.Mod.MaxLevel == 30);

        // Eldritch implicit interaction: corrupting a 2-Eldritch-implicit helmet replaces ONLY ONE implicit.
        var helmetType = items.Find("Metadata/Items/Armours/Helmets/HelmetStr1")!;
        var eldritchHelmet = rolls.EldritchEmber(
            rolls.EldritchIchor(ItemCatalog.Create(helmetType) with { ItemLevel = 84 }, helmetType, 1, new Random(1))!,
            helmetType, 1, new Random(2))!;
        var corruptedEldritch = Apply(eldritchHelmet, new OutcomeRandom(2));
        var eldritchImplicits = corruptedEldritch.Mods.Where(m => m.Section == ModSection.Implicit)
            .Select(m => items.Mods.ByHash(m.Hash)!.GenerationType).ToList();
        Assert.Equal(2, eldritchImplicits.Count);
        Assert.Contains(ItemRolls.CorruptedImplicit, eldritchImplicits);
        Assert.Contains(eldritchImplicits, g => g is ItemRolls.EaterImplicit or ItemRolls.ExarchImplicit);

        // Stygian Vise losing its implicit removes empty Abyssal socket.
        var stygian = ItemCatalog.Create(items.Find(AbyssJewels.StygianVise)!) with { ItemLevel = 84 };
        Assert.Single(stygian.Sockets);
        var stygianCorrupted = Apply(stygian, new OutcomeRandom(2));
        Assert.Empty(stygianCorrupted.Sockets);

        // Outcome 3: Brick Unique equipment (Tabula Rasa) to 6-affix Corrupted Rare, restoring base art, clearing hidden mods,
        // and hitting the 1/36 max-socket full-link roll deterministically.
        var tabula = Game.UniquesFor(player).Create("Tabula Rasa", new Random(1))!;
        Assert.Contains(tabula.Mods, m => m.Section == ModSection.Hidden);
        var simpleRobe = items.Find("Metadata/Items/Armours/BodyArmours/BodyInt1")!;
        var brickedSixLink = Apply(tabula, new OutcomeRandom(3, forceMaxLink: true));
        Assert.True(brickedSixLink.Corrupted);
        Assert.Equal(ItemRarity.Rare, brickedSixLink.Rarity);
        Assert.Equal(simpleRobe.VisualIdentity, brickedSixLink.VisualIdentity);
        Assert.DoesNotContain(brickedSixLink.Mods, m => m.Section == ModSection.Hidden);
        Assert.Equal(6, brickedSixLink.Mods.Count(m => m.Section == ModSection.Explicit));
        Assert.Equal(6, brickedSixLink.Sockets.Count);
        Assert.Equal([6], brickedSixLink.Links);

        // 1/36 max-socket full-link brick respects retail ItemLevel socket caps (ilvl 1 Body Armour caps at 2L, not 6L).
        var lowLevelBody = ItemCatalog.Create(bodyType, [new(SocketColour.White)], [1]) with { ItemLevel = 1 };
        var lowBrickedMaxLink = Apply(lowLevelBody, new OutcomeRandom(3, forceMaxLink: true));
        Assert.Equal(2, lowBrickedMaxLink.Sockets.Count);
        Assert.Equal([2], lowBrickedMaxLink.Links);

        // Bricking uniques with extra/abyssal/custom sockets reconciles sockets down to the base's surviving local mods.
        var darkness = Game.UniquesFor(player).Create("Darkness Enthroned", new Random(1))!;
        Assert.Equal(2, darkness.Sockets.Count);
        var brickedDarkness = Apply(darkness, new OutcomeRandom(3));
        Assert.Single(brickedDarkness.Sockets);
        Assert.Equal(SocketColour.Abyss, brickedDarkness.Sockets[0].Colour);

        var voidfletcher = Game.UniquesFor(player).Create("Voidfletcher", new Random(1))!;
        Assert.Equal(3, voidfletcher.Sockets.Count);
        var brickedVoidfletcher = Apply(voidfletcher, new OutcomeRandom(3));
        Assert.Single(brickedVoidfletcher.Sockets);

        var uulNetol = Game.UniquesFor(player).Create("Uul-Netol's Vow", new Random(1))!;
        Assert.Single(uulNetol.Sockets);
        Assert.Single(Apply(uulNetol, new OutcomeRandom(3)).Sockets);

        var socketlessAmulet = ItemCatalog.Create(items.Find("Metadata/Items/Amulets/Amulet1")!, [new(SocketColour.White)], [1]) with { ItemLevel = 84 };
        Assert.Empty(Apply(socketlessAmulet, new OutcomeRandom(3)).Sockets);

        // Fractured mods survive equipment Vaal brick, and socketed gems are never deleted.
        var fracturedMod = items.Mods.ById("IncreasedLife7")!.Roll(ModSection.Explicit, new Random(1));
        var gem = ItemCatalog.Create(items.Find("Metadata/Items/Gems/SkillGemFireball")!);
        var fracturedBody = body with
        {
            Rarity = ItemRarity.Rare,
            NameWords = [111u, 222u],
            FracturedCount = 1,
            Mods = [fracturedMod],
            Sockets = [new(SocketColour.White, gem), new(SocketColour.White)],
            Links = [2],
        };
        var brickedFractured = Apply(fracturedBody, new OutcomeRandom(3));
        Assert.Equal(fracturedMod, brickedFractured.Mods.First(m => m.Section == ModSection.Explicit));
        Assert.Equal(1, brickedFractured.FracturedCount);
        Assert.Same(gem, brickedFractured.Sockets[0].Gem);

        // 2. Flasks (Life/Utility/Unique): corrupt and shift quality -10..+10 (can exceed 20% up to 30%).
        var flask = ItemCatalog.Create(items.Find("Metadata/Items/Flasks/FlaskUtility6")!) with { Quality = 20 };
        Assert.Equal(30, Apply(flask, new OutcomeRandom(0, nextRange: 10)).Quality);
        Assert.Equal(10, Apply(flask, new OutcomeRandom(0, nextRange: -10)).Quality);
        var uniqueFlask = Game.UniquesFor(player).Create("Atziri's Promise", new Random(1))! with { Quality = 20 };
        var corruptedUniqueFlask = Apply(uniqueFlask, new OutcomeRandom(0, nextRange: 7));
        Assert.True(corruptedUniqueFlask.Corrupted);
        Assert.Equal(27, corruptedUniqueFlask.Quality);

        // 3. Refusals: Tinctures, Unidentified items, Corrupted, Mirrored, Vestigial; separate CurrencyMoreCorrupt keeps refusing gems.
        var tincture = ItemCatalog.Create(items.Find("Metadata/Items/Tinctures/Tincture1")!);
        Assert.Null(Game.CurrencyResult(player, tincture, vaalHash, new OutcomeRandom(0)));
        Assert.Null(Game.CurrencyResult(player, body with { Identified = false }, vaalHash, new OutcomeRandom(0)));
        Assert.Null(Game.CurrencyResult(player, unchanged, vaalHash, new OutcomeRandom(0)));
        Assert.Null(Game.CurrencyResult(player, body with { Mirrored = true }, vaalHash, new OutcomeRandom(0)));
        Assert.Null(Game.CurrencyResult(player, body with { Vestigial = true }, vaalHash, new OutcomeRandom(0)));

        var djinnVaal = items.Find("Metadata/Items/Currency/CurrencyMoreCorrupt");
        if (djinnVaal is not null)
            Assert.Null(Game.CurrencyResult(player, gem, djinnVaal.Hash, new OutcomeRandom(0)));
        var incursionCorrupt = items.Find("Metadata/Items/Currency/CurrencyIncursionCorrupt1");
        if (incursionCorrupt is not null)
            Assert.Null(Game.CurrencyResult(player, gem, incursionCorrupt.Hash, new OutcomeRandom(0)));

        // 4. Hinekora's Lock determinism: preview and commit produce byte-identical payloads for Vaal Orb.
        var lockedRare = rolls.ApplyLock(rolls.Roll(body, ItemRarity.Rare, new Random(5)), new Random(9))!;
        var preview = Apply(lockedRare, ItemRolls.ForeseeRandom(lockedRare, vaalHash));
        var commit = Apply(lockedRare, ItemRolls.ForeseeRandom(lockedRare, vaalHash));
        Assert.Equal(preview.Payload(), commit.Payload());
        Assert.True(commit.Corrupted);
    }

    [Fact]
    public void Vaal_orb_maps_and_jewels_follow_329_retail_outcomes()
    {
        using var fixture = new MinionUtilityTests.Fixture();
        var player = fixture.Player;
        var items = Game.ItemsFor(player);
        var rolls = Game.RollsFor(player);
        var vaalHash = items.Find(CurrencyEffects.VaalOrb)!.Hash;

        Item Apply(Item target, Random rng) =>
            Assert.IsType<Item>(Game.CurrencyResult(player, target, vaalHash, rng));

        // 1. Maps & MapKeys (Normal/Magic/Rare/Unique): 4-way retail outcomes.
        var t10Type = items.Find("Metadata/Items/Maps/MapKeyTier10")!;
        var t11Type = items.Find("Metadata/Items/Maps/MapKeyTier11")!;
        var t16Type = items.Find("Metadata/Items/Maps/MapKeyTier16")!;
        var vaalTempleType = items.Find("Metadata/Items/Maps/MapKeyVaalTemple")!;
        var rareT10 = rolls.Roll(ItemCatalog.Create(t10Type) with { ItemLevel = 77 }, ItemRarity.Rare, new Random(1)) with { Identified = true };

        // Outcome 0: no change.
        Assert.Equal(rareT10 with { Corrupted = true }, Apply(rareT10, new OutcomeRandom(0)));

        // Outcome 1: unidentified preserving exact mods & rarity (Normal stays identified).
        var unidMap = Apply(rareT10, new OutcomeRandom(1));
        Assert.True(unidMap.Corrupted);
        Assert.False(unidMap.Identified);
        Assert.Equal(rareT10.Mods, unidMap.Mods);
        Assert.True(Apply(ItemCatalog.Create(t10Type), new OutcomeRandom(1)).Identified);

        // Outcome 2: 8-mod Corrupted Rare (4 prefixes + 4 suffixes).
        var eightMod = Apply(rareT10, new OutcomeRandom(2));
        Assert.True(eightMod.Corrupted);
        Assert.True(eightMod.Identified);
        Assert.Equal(ItemRarity.Rare, eightMod.Rarity);
        var eightExplicits = eightMod.Mods.Where(m => m.Section == ModSection.Explicit)
            .Select(m => items.Mods.ByHash(m.Hash)!).ToList();
        Assert.Equal(8, eightExplicits.Count);
        Assert.Equal(4, eightExplicits.Count(m => m.GenerationType == ItemRolls.Prefix));
        Assert.Equal(4, eightExplicits.Count(m => m.GenerationType == ItemRolls.Suffix));

        // Outcome 3: +1 tier upgrade (T10 -> T11) and T16 -> Vaal Temple Map (with Vaal Vessels implicit).
        var upgradedT11 = Apply(rareT10, new OutcomeRandom(3));
        Assert.True(upgradedT11.Corrupted);
        Assert.Equal(ItemRarity.Rare, upgradedT11.Rarity);
        Assert.Equal(t11Type.Hash, upgradedT11.TypeHash);

        var rareT16 = rolls.Roll(ItemCatalog.Create(t16Type) with { ItemLevel = 83 }, ItemRarity.Rare, new Random(2)) with { Identified = true };
        var upgradedVaalTemple = Apply(rareT16, new OutcomeRandom(3));
        Assert.True(upgradedVaalTemple.Corrupted);
        Assert.Equal(ItemRarity.Rare, upgradedVaalTemple.Rarity);
        Assert.Equal(vaalTempleType.Hash, upgradedVaalTemple.TypeHash);
        Assert.Contains(upgradedVaalTemple.Mods, m => m.Section == ModSection.Implicit);

        // Unique map: unidentified keeps unique mods, brick restores base art & rolls Rare.
        var uniqueMap = Game.UniquesFor(player).NamedMap("Mao Kun")!;
        var unidUniqueMap = Apply(uniqueMap, new OutcomeRandom(1));
        Assert.True(unidUniqueMap.Corrupted);
        Assert.False(unidUniqueMap.Identified);
        Assert.Equal(ItemRarity.Unique, unidUniqueMap.Rarity);
        var brickedUniqueMap = Apply(uniqueMap, new OutcomeRandom(2));
        Assert.True(brickedUniqueMap.Corrupted);
        Assert.Equal(ItemRarity.Rare, brickedUniqueMap.Rarity);
        Assert.Equal(items.ByHash(uniqueMap.TypeHash)!.VisualIdentity, brickedUniqueMap.VisualIdentity);

        // 2. Jewels (Crimson/Viridian/Cobalt, Abyss, Cluster, Unique): 4-way retail outcomes.
        foreach (string jewelId in new[]
        {
            "Metadata/Items/Jewels/JewelStr",
            "Metadata/Items/Jewels/JewelDex",
            "Metadata/Items/Jewels/JewelInt",
        })
        {
            var jt = items.Find(jewelId)!;
            var jewel = ItemCatalog.Create(jt) with { ItemLevel = 80 };
            Assert.Equal(jewel with { Corrupted = true }, Apply(jewel, new OutcomeRandom(0)));

            var withImplicit = Apply(jewel, new OutcomeRandom(1));
            Assert.True(withImplicit.Corrupted);
            var imp = Assert.Single(withImplicit.Mods, m => m.Section == ModSection.Implicit);
            Assert.Equal(ItemRolls.CorruptedImplicit, items.Mods.ByHash(imp.Hash)!.GenerationType);
            Assert.Equal(10, items.Mods.ByHash(imp.Hash)!.Domain);

            var rareJewel = Apply(jewel, new OutcomeRandom(2));
            Assert.True(rareJewel.Corrupted);
            Assert.Equal(ItemRarity.Rare, rareJewel.Rarity);
            Assert.InRange(rareJewel.Mods.Count(m => m.Section == ModSection.Explicit), 3, 4);

            var uniqueJewel = Apply(jewel, new OutcomeRandom(3));
            Assert.True(uniqueJewel.Corrupted);
            Assert.Equal(ItemRarity.Unique, uniqueJewel.Rarity);
            Assert.Equal(jt.Hash, uniqueJewel.TypeHash);
            Assert.Equal(80, uniqueJewel.ItemLevel);
            Assert.NotEmpty(uniqueJewel.NameWords);

            // Pinnacle boss / Simulacrum / Heist / recipe corrupted uniques are never in the Vaal Orb pool.
            var eligibleNames = VaalCorruption.CorruptedUniqueJewelPool(Game.UniquesFor(player), jewelId)
                .Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.NotEmpty(eligibleNames);
            Assert.DoesNotContain("Forbidden Flame", eligibleNames);
            Assert.DoesNotContain("Forbidden Flesh", eligibleNames);
            Assert.DoesNotContain("Split Personality", eligibleNames);
            Assert.DoesNotContain("The Adorned", eligibleNames);
            Assert.DoesNotContain(eligibleNames, n => n.StartsWith("Replica ", StringComparison.Ordinal));
        }

        // Abyss Jewel: Domain 13 implicit, rare brick, and outcome 3 falls back to no-change.
        var abyssType = items.Find("Metadata/Items/Jewels/JewelAbyssMelee")!;
        var abyss = ItemCatalog.Create(abyssType) with { ItemLevel = 80 };
        var abyssImp = Apply(abyss, new OutcomeRandom(1));
        Assert.Equal(13, items.Mods.ByHash(Assert.Single(abyssImp.Mods, m => m.Section == ModSection.Implicit).Hash)!.Domain);
        Assert.Equal(ItemRarity.Rare, Apply(abyss, new OutcomeRandom(2)).Rarity);
        Assert.Equal(abyss with { Corrupted = true }, Apply(abyss, new OutcomeRandom(3)));

        // Cluster Jewel: Domain 10 implicit preserving Cluster enchants, rare brick preserving enchants, outcome 3 -> no-change.
        var clusterType = items.Find("Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge")!;
        var cluster = ItemCatalog.Create(clusterType, random: new Random(3)) with { ItemLevel = 84 };
        var enchants = cluster.Mods.Where(m => m.Section == ModSection.Enchant).ToList();
        Assert.NotEmpty(enchants);
        var clusterImp = Apply(cluster, new OutcomeRandom(1));
        Assert.Equal(10, items.Mods.ByHash(Assert.Single(clusterImp.Mods, m => m.Section == ModSection.Implicit).Hash)!.Domain);
        Assert.Equal(enchants, clusterImp.Mods.Where(m => m.Section == ModSection.Enchant).ToList());
        var clusterRare = Apply(cluster, new OutcomeRandom(2));
        Assert.Equal(ItemRarity.Rare, clusterRare.Rarity);
        Assert.Equal(enchants, clusterRare.Mods.Where(m => m.Section == ModSection.Enchant).ToList());
        Assert.Equal(cluster with { Corrupted = true }, Apply(cluster, new OutcomeRandom(3)));

        // Unique Cluster Jewel (One With Nothing) bricked to Rare restores base Cluster enchants (and already-corrupted Voices is refused).
        var voices = Game.UniquesFor(player).Create("Voices", new Random(1))!;
        Assert.True(voices.Corrupted);
        Assert.Null(Game.CurrencyResult(player, voices, vaalHash, new OutcomeRandom(2)));
        var oneWithNothing = Game.UniquesFor(player).Create("One With Nothing", new Random(1))!;
        var brickedUniqueCluster = Apply(oneWithNothing, new OutcomeRandom(2));
        Assert.True(brickedUniqueCluster.Corrupted);
        Assert.Equal(ItemRarity.Rare, brickedUniqueCluster.Rarity);
        Assert.Contains(brickedUniqueCluster.Mods, m => m.Section == ModSection.Enchant);
    }
}
