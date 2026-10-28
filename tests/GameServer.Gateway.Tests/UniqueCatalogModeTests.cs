using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class UniqueCatalogModeTests
{
    [Fact]
    public void Curated_unique_mods_use_the_selected_catalog_including_an_explicit_Ruthless_zero()
    {
        string folder = TestSaves.Catalog(new TestSaves.Type(42, "Metadata/Items/Belts/Belt4"));
        try
        {
            void Write(string name, object rows) => File.WriteAllText(Path.Combine(folder, "tables", name + ".json"),
                JsonSerializer.Serialize(rows));
            Write("Stats", new[] { new { Id = "additional_dexterity", IsLocal = false } });
            Write("Words", new[] { new { Wordlist = 6, Text = "Mageblood", HASH32 = 3562871479u } });
            Write("UniqueStashLayout", new[] { new { WordsKey = 0, ItemVisualIdentityKey = 0 } });
            Write("ItemVisualIdentity", new[] { new { HASH16 = 88 } });
            Write("Mods", new[] { new
            {
                Id = "DexterityUnique__3", HASH16 = 23, Name = "Dexterity", Domain = 1, GenerationType = 3, Level = 1,
                StatsKey1 = 0, StatsKey2 = (int?)null, Stat1Min = 17, Stat1Max = 17,
                HasHardmodeStatValues = true, Stat1MinHardmode = 0, Stat1MaxHardmode = 0,
                Families = new[] { 23 }, SpawnWeight_TagsKeys = Array.Empty<int>(), SpawnWeight_Values = Array.Empty<int>(),
                GenerationWeight_TagsKeys = Array.Empty<int>(), GenerationWeight_Values = Array.Empty<int>(),
                IsEssenceOnlyModifier = false,
            } });
            var ordinary = new UniqueCatalog(new ItemCatalog(folder), folder);
            var ruthless = new UniqueCatalog(new ItemCatalog(folder, ruthless: true), folder);

            var normalItem = ordinary.Create("Mageblood", new Random(1))!;
            var ruthlessItem = ruthless.Create("Mageblood", new Random(1))!;

            Assert.Equal(ItemRarity.Unique, ruthlessItem.Rarity);
            Assert.Equal(normalItem.TypeHash, ruthlessItem.TypeHash);
            Assert.Equal(normalItem.NameWords, ruthlessItem.NameWords);
            Assert.Equal(normalItem.VisualIdentity, ruthlessItem.VisualIdentity);
            Assert.Equal(17, Assert.Single(Assert.Single(normalItem.Mods).Values));
            Assert.Equal(0, Assert.Single(Assert.Single(ruthlessItem.Mods).Values));
            Assert.Equal(17, Assert.Single(Assert.Single(ordinary.Create("Mageblood", new Random(2))!.Mods).Values));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Runtime_unique_creation_keeps_each_modes_catalog_and_native_unique_identity()
    {
        ClientState Connection(bool ruthless) => new(new(0, "UniqueMode", 128, 128, 32, 32, null, []), 0, 1)
            { Ruthless = ruthless };
        var ordinary = Connection(false);
        var ruthless = Connection(true);
        var ordinaryCatalog = Program.UniquesFor(ordinary);
        var ruthlessCatalog = Program.UniquesFor(ruthless);
        Assert.NotSame(ordinaryCatalog, ruthlessCatalog);

        foreach (var conn in new[] { ordinary, ruthless, ordinary })
        {
            var item = Program.UniquesFor(conn).Create("Mageblood", new Random(1));
            Assert.NotNull(item);
            Assert.Equal(ItemRarity.Unique, item.Rarity);
            Assert.Equal(Program.ItemsFor(conn).Find("Metadata/Items/Belts/Belt4")!.Hash, item.TypeHash);
            Assert.Contains(3562871479u, item.NameWords);
            Assert.Equal(48209, item.VisualIdentity);
            var always = Assert.Single(item.Mods, m =>
                Program.ItemsFor(conn).Mods.ByHash(m.Hash)?.Id == "MagicUtilityFlasksAlwaysApplyUnique__1");
            Assert.InRange(Assert.Single(always.Values), 2, 4);
        }
    }
}
