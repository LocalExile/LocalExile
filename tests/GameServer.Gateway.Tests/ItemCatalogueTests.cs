using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// A unique or an item state is created, sent and saved as defined.
public sealed class ItemCatalogueTests
{
    [Fact]
    public void Uniques_and_item_states_are_created_sent_and_saved_as_defined()
    {
        var items = new ItemCatalog("data");
        var uniques = new UniqueCatalog(items, "data");
        var rolls = new ItemRolls(items, "data");
        var random = new Random(1);
        string[] Ids(Item item, ModSection section) =>
            [.. item.Mods.Where(m => m.Section == section).Select(m => items.Mods.ByHash(m.Hash)!.Id)];
        int Value(Item item, string id) => item.Mods.Single(m => m.Hash == items.Mods.ById(id)!.Hash).Values[0];

        // Every definition but the tinctures, whose component the server cannot write, creates, is sent and reloads.
        foreach (var definition in uniques.All.Where(d => !items.Find(d.BaseTypeId)!.Components.Contains("Tincture")))
        {
            var item = uniques.Create(definition, random);
            Assert.True(item is not null, $"{definition.Name} on {definition.BaseTypeId}");
            Assert.Equal(items.Find(definition.BaseTypeId)!.Hash, item.TypeHash);
            Assert.Equal(definition.Corrupted, item.Corrupted);
            Assert.Equal(definition.Mirrored, item.Mirrored);
            Assert.Equal(definition.Influences, item.Influences);
            Assert.Equal(definition.Fractured, item.FracturedCount);
            Assert.NotEmpty(item.Payload());
            var back = CharacterSaves.FromSaved(CharacterSaves.ToSaved(item, items.Mods), items);
            Assert.Equal(item.Mods.Select(m => (m.Section, m.Hash)), back.Mods.Select(m => (m.Section, m.Hash)));
            Assert.Equal(item.Influences, back.Influences);
            Assert.NotEqual([], definition.Implicits);
        }

        // The hidden level overrides PoB leaves out: every one of these is lower than its base type's drop level.
        (string Name, int Level)[] hiddenLevels =
        [
            ("Araku Tiki", 1), ("Blackheart", 1), ("Cragfall", 4), ("Craghead", 4), ("Doedre's Damning", 1),
            ("Kalandra's Touch", 1), ("Kaom's Sign", 1), ("Karui Ward", 5), ("Malachai's Artifice", 5),
            ("Perandus Signet", 1), ("Replica Doedre's Damning", 1), ("Replica Karui Ward", 5),
            ("Replica Malachai's Artifice", 5), ("Seven-League Step", 1), ("Sidhebreath", 1),
            ("Stone of Lazhwar", 5), ("Sudden Dawn", 3), ("Tear of Purity", 5),
        ];
        foreach (var (name, level) in hiddenLevels)
        {
            var item = uniques.Create(name, random)!;
            Assert.Equal(level, ItemModStats.LevelRequirement(item, items.ByHash(item.TypeHash)!.DropLevel, items.Mods));
        }

        // What the export gets wrong: mod ids that look like metadata, alternate variants, the newest base, same-name
        // bases, an old base's implicits, stale rolls, and a mod in both lists.
        Assert.Equal(["CorruptedMagicJewelModEffectUnique__1"], uniques.Find("The Adorned")!.Mods);
        Assert.Contains("CorruptedBloodImmunityUnique_1", uniques.Find("Ralakesh's Impatience")!.Mods);
        Assert.Contains("AbyssJewelSocketUnique__11_", uniques.Find("Tombfist")!.Mods);
        Assert.Contains("IncreasedAttackSpeedUniqueGlovesStrDex1", uniques.Find("Tombfist")!.Mods);
        Assert.Equal("Ezomyte Axe", items.Find(uniques.Find("Wings of Entropy")!.BaseTypeId)!.Name);
        Assert.NotNull(uniques.Create("Rigwald's Charge", random));         // spelled otherwise in Words
        Assert.NotNull(uniques.Create("The Goddess Unleashed", random));
        Assert.Equal(["ColdAndLightningResistImplicitRing1"], Ids(uniques.Create("Berek's Grip", random)!, ModSection.Implicit));
        Assert.Equal(["IncreasedCastSpeedImplicitRing1"], Ids(uniques.Create("Anathema", random)!, ModSection.Implicit));
        Assert.Equal(["CriticalMultiplierImplicitBow1"], Ids(uniques.Create("Arborix", random)!, ModSection.Implicit));
        Assert.Equal(100, Value(uniques.Create("Hrimburn", random)!, "AttackerTakesColdDamageGlovesDex1"));
        Assert.InRange(Value(uniques.Create("Midnight Bargain", random)!, "MinionDamageUniqueWand2"), 50, 70);
        var life = items.Mods.ById("IncreasedLifeUniqueBodyStr2")!;
        var both = uniques.All.First(d => d.Name == "Wall of Brambles") with
        {
            Mods = [life.Id], Implicits = [life.Id], Ranges = [new(life.Id, 0, 12, 12, Implicit: true), new(life.Id, 0, 77, 77)],
        };
        var created = uniques.Create(both, random)!;
        Assert.Equal(12, created.Mods.Single(m => m.Section == ModSection.Implicit).Values[0]);
        Assert.Equal(77, created.Mods.Single(m => m.Section == ModSection.Explicit).Values[0]);

        // A Divine Orb rerolls in the unique's own range, and an admin's range wins over it.
        var varunastra = uniques.Create("Varunastra", random)!;
        for (int seed = 0; seed < 40; seed++)
            Assert.InRange(Value(rolls.Reforge(varunastra, new Random(seed), uniques.RollsOf(varunastra))!,
                "LocalIncreasedPhysicalDamagePercentUniqueTwoHandSword1"), 80, 100);
        Assert.Null(uniques.RollsOf(ItemCatalog.Create(items.Find("Driftwood Wand")!)));
        var before = ModRangeProvider.Default.All;
        try
        {
            ModRangeProvider.Default.Replace(new Dictionary<(string, int), (int, int)>(before) { [(life.Id, 0)] = (5, 5) });
            Assert.Equal(5, uniques.Create(both, random)!.Mods.Single(m => m.Section == ModSection.Explicit).Values[0]);
        }
        finally { ModRangeProvider.Default.Replace(before); }

        // A unique spelled otherwise in Words keeps its drop weight.
        var provider = new UniqueWeightProvider();
        provider.Replace(new Dictionary<string, (double, double)> { ["Emperor’s Vigilance"] = (.5, .5), ["Broken Elegy"] = (.5, .5) }, true);
        Assert.Equal(.5, provider.Weight("Emperor's Vigilance", false));
        Assert.Equal(.5, provider.Weight("The Broken Elegy", false));

        // The states change one byte each in place, come back from a save, open their mods and refuse fracturing.
        var boots = items.Find("Iron Greaves")!;
        var plain = ItemCatalog.Create(boots) with { ItemLevel = 86 };
        byte[] plainBytes = [.. plain.Payload()];
        byte[] stateBytes = [.. (plain with { Influences = ItemInfluence.Warlord, Split = true, Synthesised = true }).Payload()];
        Assert.Equal(plainBytes.Length, stateBytes.Length);
        Assert.Equal(new byte[] { 0x20, 0x24 }, stateBytes.Where((b, i) => b != plainBytes[i]).Order());

        var state = ItemStates.Toggle(ItemStates.Toggle(ItemStates.Toggle(plain, "shaper")!, "split")!, "synthesised")!;
        Assert.Null(ItemStates.Toggle(plain, "none"));
        Assert.Equal("Item states: split, synthesised, shaper", ItemStates.Describe(state));
        Assert.True(ItemStates.NeedsMods("split") && !ItemStates.NeedsMods("corrupted"));
        var saved = CharacterSaves.FromSaved(CharacterSaves.ToSaved(state, items.Mods), items);
        Assert.True(saved.Split && saved.Synthesised && saved.Influences == ItemInfluence.Shaper);
        Assert.Contains("boots_shaper", rolls.TagsWith(boots, ItemInfluence.Shaper));
        Assert.Equal(rolls.Pool(boots, 86, ItemInfluence.Shaper), rolls.Pool(boots, state));
        Assert.True(rolls.Pool(boots, state).Count > rolls.Pool(boots, 86).Count);

        var rare = plain with
        {
            Rarity = ItemRarity.Rare, NameWords = [1u, 2u],
            Mods = [.. plain.Mods, .. rolls.Pool(boots, plain).Take(4).Select(p => p.Mod.Roll(ModSection.Explicit, random))],
        };
        Assert.NotNull(rolls.Fracture(rare, random));
        Assert.Null(rolls.Fracture(rare with { Influences = ItemInfluence.Shaper }, random));
        Assert.Null(rolls.Fracture(rare with { Synthesised = true }, random));
    }
}
