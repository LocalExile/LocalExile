using GameServer.Modules.Survivors;
namespace GameServer.Gateway.Tests;

// The survivors mode offers real uniques: every definition must resolve its Mods.dat rows, every equipment entry must
// create, and every one but a blank unique (Tabula Rasa) must leave at least one recognizable stat.
public sealed class UniqueCatalogueTests
{
    public void Every_unique_resolves_its_mods_and_every_equipment_unique_has_a_stat()
    {
        var items = new ItemCatalog("data");
        var uniques = new UniqueCatalog(items, "data");
        var stats = new StatCatalog();
        var missing = new List<string>();
        var noStats = new List<string>();
        int equipment = 0, created = 0;
        foreach (var definition in uniques.All)
        {
            foreach (string id in definition.Mods.Concat(definition.Implicits ?? []))
                if (items.Mods.ById(id) is null) missing.Add($"{definition.Name}:{id}");
            if (uniques.Create(definition, new Random(definition.Name.GetHashCode())) is not { } item) continue;
            created++;
            if (items.Find(definition.BaseTypeId) is not { } type) continue;
            if (!ItemModStats.EquipmentRows.Any(row => PlayerInventories.Accepts(row, type.Class))) continue;
            equipment++;
            var global = ItemModStats.Global([item], items.Mods, stats);
            var local = ItemModStats.Local(item, items.Mods);
            if (global.Count == 0 && local.Count == 0) noStats.Add(definition.Name);
        }

        Assert.Empty(missing);
        Assert.Equal(uniques.All.Count, created);
        Assert.All(uniques.All, definition => Assert.NotNull(uniques.NameWord(definition)));
        Assert.True(equipment > 1000, $"only {equipment} equipment uniques");
        // Tabula Rasa is the one unique whose identity is its white sockets, not a stat.
        Assert.Equal(["Tabula Rasa"], noStats.Order());
    }
}
