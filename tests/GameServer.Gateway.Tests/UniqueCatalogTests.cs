using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Unique items whose client name/art come from Words/UniqueStashLayout and whose mods are curated (UniqueCatalog).
public class UniqueCatalogTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/Words.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("Words.json");
    }

    static readonly Lazy<(ItemCatalog Items, UniqueCatalog Uniques)> Setup = new(() =>
    {
        string data = Data();
        var items = new ItemCatalog(data);
        return (items, new UniqueCatalog(items, data));
    });

    [Fact]
    public void Mageblood_is_a_unique_heavy_belt_with_its_name_art_and_mods()
    {
        var (items, uniques) = Setup.Value;
        var item = uniques.Create("mageblood", new Random(1))!;

        Assert.Equal(ItemRarity.Unique, item.Rarity);
        Assert.True(item.Identified);
        Assert.Equal(items.Find("Metadata/Items/Belts/Belt4")!.Hash, item.TypeHash);
        Assert.Contains(3562871479u, item.NameWords);   // the Words HASH32 of "Mageblood"
        Assert.Equal(48209, item.VisualIdentity);       // UniqueBelt43 HASH16

        var mods = item.Mods.Select(m => (Mod: items.Mods.ByHash(m.Hash), Value: m)).Where(p => p.Mod is not null).ToList();
        Assert.Contains(mods, p => p.Mod!.Id == "StrengthImplicitBelt1" && p.Value.Section == ModSection.Implicit);
        foreach (string id in new[] { "DexterityUnique__3", "FireResistUnique__24", "ColdResistUnique__34",
                     "MagicUtilityFlasksCannotUseUnique__1____", "MagicUtilityFlasksCannotRemoveUnique__1" })
            Assert.Contains(mods, p => p.Mod!.Id == id && p.Value.Section == ModSection.Explicit);
        var always = item.Mods.Single(m => items.Mods.ByHash(m.Hash)?.Id == "MagicUtilityFlasksAlwaysApplyUnique__1");
        Assert.Equal(ModSection.Explicit, always.Section);
        Assert.InRange(always.Values[0], 2, 4);
    }

    [Fact]
    public void Every_definition_creates_an_item_that_survives_the_wire_and_a_save()
    {
        var (items, uniques) = Setup.Value;
        Assert.NotEmpty(uniques.All);
        // The server cannot write the Tincture component yet, so no tincture exists; nothing else may be left out.
        var writable = uniques.All.Where(d => !items.Find(d.BaseTypeId)!.Components.Contains("Tincture")).ToList();
        Assert.Empty(writable.Where(d => uniques.Create(d, new Random(1)) is null).Select(d => $"{d.Name} on {d.BaseTypeId}"));
        foreach (var definition in writable)
        {
            var item = uniques.Create(definition, new Random(1))!;
            Assert.Equal(items.Find(definition.BaseTypeId)!.Hash, item.TypeHash);
            Assert.Equal(ItemRarity.Unique, item.Rarity);
            Assert.NotEmpty(item.NameWords);
            Assert.Equal(definition.Corrupted, item.Corrupted);
            Assert.Equal(definition.Mirrored, item.Mirrored);
            Assert.InRange(item.ItemLevel, 1, 100);
            foreach (string id in definition.Mods)
                Assert.True(items.Mods.ById(id) is not null, $"{definition.Name}: {id}");

            Assert.NotEmpty(item.Payload());
            var back = CharacterSaves.FromSaved(CharacterSaves.ToSaved(item, items.Mods), items);
            Assert.Equal(item.NameWords, back.NameWords);
            Assert.Equal(item.VisualIdentity, back.VisualIdentity);
            Assert.Equal(item.Mods.Select(m => (m.Section, m.Hash)), back.Mods.Select(m => (m.Section, m.Hash)));
        }
    }

    [Fact]
    public void A_uniques_own_implicits_replace_the_base_types_and_go_in_the_implicit_list()
    {
        var (items, uniques) = Setup.Value;
        var definition = uniques.All.First(d => d.Implicits is { Length: > 0 } own
            && own.All(id => items.Find(d.BaseTypeId)!.Implicits?.All(mod => mod.Id != id) != false));
        var item = uniques.Create(definition, new Random(1))!;

        var implicits = item.Mods.Where(m => m.Section == ModSection.Implicit).Select(m => items.Mods.ByHash(m.Hash)!.Id);
        Assert.Equal(definition.Implicits!, implicits);
        Assert.DoesNotContain(item.Mods, m => m.Section == ModSection.Explicit && definition.Implicits!.Contains(items.Mods.ByHash(m.Hash)!.Id));
    }

    [Fact]
    public void Current_variants_take_their_current_base_and_a_real_item_level()
    {
        var (items, uniques) = Setup.Value;
        Assert.Equal("Opal Wand", items.Find(uniques.Find("Tulfall")!.BaseTypeId)!.Name);
        Assert.All(uniques.All, definition => Assert.InRange(definition.ItemLevel, 1, 100));
    }

    [Theory]
    [InlineData("Emperor's Vigilance")]   // a curly apostrophe in Words
    [InlineData("The Broken Elegy")]      // Broken Elegy in Words
    [InlineData("Gorgon's Gaze")]         // metadata before the base in the export
    public void Names_the_export_spells_differently_from_the_client_resolve(string name)
    {
        var (_, uniques) = Setup.Value;
        Assert.NotNull(uniques.Create(name, new Random(1)));
    }

    [Fact]
    public void A_roll_override_wins_over_the_mods_dat_range()
    {
        var (items, uniques) = Setup.Value;
        var mod = items.Mods.ById("IncreasedLifeUniqueBodyStr2")!;
        var definition = uniques.All.First(d => d.Name == "Wall of Brambles") with
        {
            Mods = ["IncreasedLifeUniqueBodyStr2"], Implicits = null, Ranges = [new("IncreasedLifeUniqueBodyStr2", 0, 12, 20)],
        };
        var rolled = uniques.Create(definition, new Random(1))!.Mods.Single(m => m.Hash == mod.Hash);
        Assert.InRange(rolled.Values[0], 12, 20);
    }

    [Fact]
    public void A_mod_in_both_lists_takes_each_lists_own_roll()
    {
        var (items, uniques) = Setup.Value;
        var mod = items.Mods.ById("IncreasedLifeUniqueBodyStr2")!;
        var definition = uniques.All.First(d => d.Name == "Wall of Brambles") with
        {
            Mods = [mod.Id], Implicits = [mod.Id], Ranges = [new(mod.Id, 0, 12, 12, Implicit: true), new(mod.Id, 0, 77, 77)],
        };
        var item = uniques.Create(definition, new Random(1))!;
        Assert.Equal(12, item.Mods.Single(m => m.Section == ModSection.Implicit).Values[0]);
        Assert.Equal(77, item.Mods.Single(m => m.Section == ModSection.Explicit).Values[0]);
    }

    [Fact]
    public void An_admins_range_wins_over_a_uniques_own_roll()
    {
        var (items, uniques) = Setup.Value;
        var mod = items.Mods.ById("IncreasedLifeUniqueBodyStr2")!;
        var definition = uniques.All.First(d => d.Name == "Wall of Brambles") with
        {
            Mods = [mod.Id], Implicits = null, Ranges = [new(mod.Id, 0, 12, 12)],
        };
        Assert.Equal(12, uniques.Create(definition, new Random(1))!.Mods.Single(m => m.Hash == mod.Hash).Values[0]);

        var before = ModRangeProvider.Default.All;
        try
        {
            ModRangeProvider.Default.Replace(new Dictionary<(string, int), (int, int)>(before) { [(mod.Id, 0)] = (5, 5) });
            Assert.Equal(5, uniques.Create(definition, new Random(1))!.Mods.Single(m => m.Hash == mod.Hash).Values[0]);
        }
        finally { ModRangeProvider.Default.Replace(before); }
    }

    static string[] Ids(ItemCatalog items, Item item, ModSection section) =>
        [.. item.Mods.Where(m => m.Section == section).Select(m => items.Mods.ByHash(m.Hash)!.Id)];

    [Fact]
    public void A_mod_id_starting_like_a_metadata_key_stays_on_the_unique()
    {
        var (items, uniques) = Setup.Value;
        Assert.Contains("CorruptedMagicJewelModEffectUnique__1", Ids(items, uniques.Create("The Adorned", new Random(1))!, ModSection.Explicit));
        Assert.Contains("CorruptedBloodImmunityUnique_1", Ids(items, uniques.Create("Ralakesh's Impatience", new Random(1))!, ModSection.Explicit));
    }

    [Fact]
    public void Alternate_variants_join_the_selected_one()
    {
        var (items, uniques) = Setup.Value;
        // Tombfist: Current, and PoB's choice in each alternate slot, not the last variant alone.
        var tombfist = Ids(items, uniques.Create("Tombfist", new Random(1))!, ModSection.Explicit);
        Assert.Contains("IncreasedAttackSpeedUniqueGlovesStrDex1", tombfist);
        Assert.Contains("AbyssJewelSocketUnique__11_", tombfist);
        Assert.Contains("IntimidateOnHitWithMeleeAbyssJewelUnique__1", tombfist);
        Assert.Contains("MaimOnHitWithRangedAbyssJewelUnique__1", tombfist);
        Assert.Equal(2, Ids(items, uniques.Create("Eyes of the Greatwolf", new Random(1))!, ModSection.Implicit).Length);
    }

    [Theory]
    [InlineData("Wings of Entropy", "Ezomyte Axe")]          // no base line for the current variant: the newest
    [InlineData("Essentia Sanguis", "Vaal Claw")]
    [InlineData("Dying Breath", "Iron Staff")]
    public void The_newest_base_line_stands_when_none_names_the_current_variant(string name, string baseName)
    {
        var (items, uniques) = Setup.Value;
        Assert.Equal(baseName, items.Find(uniques.Find(name)!.BaseTypeId)!.Name);
    }

    [Theory]
    [InlineData("Anathema", "IncreasedCastSpeedImplicitRing1")]       // the export names Moonstone Ring's old implicit
    [InlineData("Ungil's Gauche", "CriticalStrikeChanceImplicitDaggerNew1")]
    [InlineData("Arborix", "CriticalMultiplierImplicitBow1")]         // the export says it has none
    [InlineData("String of Servitude", "StrengthImplicitBelt1")]
    public void A_unique_keeps_its_bases_implicit_unless_it_has_its_own(string name, string implicitId)
    {
        var (items, uniques) = Setup.Value;
        Assert.Null(uniques.Find(name)!.Implicits);
        Assert.Contains(implicitId, Ids(items, uniques.Create(name, new Random(1))!, ModSection.Implicit));
    }

    [Theory]
    [InlineData("Berek's Grip", "ColdAndLightningResistImplicitRing1")]    // three Two-Stone Rings
    [InlineData("Berek's Pass", "FireAndColdResistImplicitRing1")]
    public void Same_name_bases_are_told_apart_by_their_implicits(string name, string implicitId)
    {
        var (items, uniques) = Setup.Value;
        Assert.Equal([implicitId], Ids(items, uniques.Create(name, new Random(1))!, ModSection.Implicit));
    }

    [Fact]
    public void Only_a_definition_with_a_real_difference_replaces_the_base_implicits()
    {
        var (items, uniques) = Setup.Value;
        Assert.DoesNotContain(uniques.All, d => d.Implicits is { Length: 0 });
        Assert.All(uniques.All.Where(d => d.Implicits is not null), d => Assert.True(
            d.Name == "The Eternal Struggle" || items.Find(d.BaseTypeId)!.Implicits is not { Count: > 0 }
            || d.Implicits!.Any(id => id.Contains("Unique")), d.Name));
    }

    [Theory]
    [InlineData("Hrimburn", "AttackerTakesColdDamageGlovesDex1", 100, 100)]   // PoB says 10
    [InlineData("Wildwrap", "FlaskManaRecoveryUniqueBodyDex7", 60, 100)]      // PoB says 20-25
    public void A_stale_export_roll_leaves_the_mods_dat_range(string name, string id, int min, int max)
    {
        var (items, uniques) = Setup.Value;
        var value = uniques.Create(name, new Random(1))!.Mods.Single(m => m.Hash == items.Mods.ById(id)!.Hash).Values[0];
        Assert.InRange(value, min, max);
    }

    [Theory]
    [InlineData("The Adorned")]
    [InlineData("Voices")]
    public void A_unique_that_always_drops_corrupted_is_corrupted(string name) =>
        Assert.True(Setup.Value.Uniques.Create(name, new Random(1))!.Corrupted);

    [Fact]
    public void A_uniques_fractured_mods_come_first_and_are_counted()
    {
        var (items, uniques) = Setup.Value;
        var fractured = uniques.All.Where(d => d.Fractured > 0).ToList();
        Assert.Equal(6, fractured.Count);
        foreach (var definition in fractured)
        {
            var item = uniques.Create(definition, new Random(1))!;
            Assert.Equal(definition.Fractured, item.FracturedCount);
            Assert.Equal(definition.Mods[0], items.Mods.ByHash(item.Mods[item.FracturedIndexes().Min()].Hash)!.Id);
        }
    }

    [Fact]
    public void Divine_and_Blessed_Orbs_keep_a_uniques_own_rolls()
    {
        var (items, uniques) = Setup.Value;
        var phys = items.Mods.ById("LocalIncreasedPhysicalDamagePercentUniqueTwoHandSword1")!;
        var varunastra = uniques.Create("Varunastra", new Random(1))!;
        for (int seed = 0; seed < 40; seed++)
            Assert.InRange(Program.Rolls.Reforge(varunastra, new Random(seed), uniques.RollsOf(varunastra))!
                .Mods.Single(m => m.Hash == phys.Hash).Values[0], 80, 100);

        var definition = uniques.All.First(d => d.Name == "Wall of Brambles") with
        {
            Mods = [], Implicits = [phys.Id], Ranges = [new(phys.Id, 0, 9, 9, Implicit: true)],
        };
        var implicitOnly = uniques.Create(definition, new Random(1))!;
        var blessed = Program.Rolls.Bless(implicitOnly, new Random(1), (mod, section, stat) => section == ModSection.Implicit ? (9, 9) : null)!;
        Assert.Equal(9, blessed.Mods.Single(m => m.Hash == phys.Hash).Values[0]);
    }

    [Fact]
    public void Only_a_unique_has_rolls_of_its_own()
    {
        var (items, uniques) = Setup.Value;
        Assert.Null(uniques.RollsOf(ItemCatalog.Create(items.Find("Driftwood Wand")!)));
        Assert.NotNull(uniques.RollsOf(uniques.Create("Varunastra", new Random(1))!));
    }

    [Theory]
    [InlineData("Emperor's Vigilance")]   // a curly apostrophe in Words
    [InlineData("The Broken Elegy")]      // Broken Elegy in Words
    public void A_unique_spelled_otherwise_in_Words_finds_its_drop_weight(string name)
    {
        var (_, uniques) = Setup.Value;
        var provider = new UniqueWeightProvider();
        provider.Replace(new Dictionary<string, (double, double)> { ["Emperor’s Vigilance"] = (.5, .5), ["Broken Elegy"] = (.5, .5) }, true);
        Assert.Equal(.5, new ItemDrops(Program.Items, uniqueWeights: provider).UniqueWeight(name));
        Assert.True(new ItemDrops(Program.Items, uniqueWeights: new UniqueWeightProvider()).UniqueWeight(name) > 0);
    }

    [Fact]
    public void A_unique_on_several_bases_is_reached_by_adding_the_base_name()
    {
        var (items, uniques) = Setup.Value;
        var spectrums = uniques.All.Where(d => d.Name == "Grand Spectrum").ToList();
        Assert.True(spectrums.Count > 1);
        foreach (var definition in spectrums)
            Assert.Equal(definition, uniques.Find($"Grand Spectrum {items.Find(definition.BaseTypeId)!.Name}"));
    }

}
