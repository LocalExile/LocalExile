using System.Text.Json;
using GameServer.Storage;

/// <summary>
/// The app-facing reference tables: every Stats.dat key, every Mods.dat row and each mod's default stat ranges. They
/// are seeded once from the extracted tables, then queried by an editor instead of making it parse Mods.json/Stats.json
/// itself. The server's hot paths still use <see cref="ModCatalog"/> in memory; these tables are the editable/reference
/// view next to <c>mod_stat_overrides</c>.
/// </summary>
static class GameDataReference
{
    public static bool Seed(GameDataStore store, string folder = "data", bool force = false)
    {
        if (!force && store.ReferenceSeeded()) return false;
        var stats = new List<GameDataStore.StatRow>();
        string statsPath = Path.Combine(folder, "tables", "Stats.json");
        if (File.Exists(statsPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(statsPath));
            int row = 0;
            foreach (var stat in document.RootElement.EnumerateArray())
            {
                string id = stat.GetProperty("Id").GetString() ?? "";
                uint hash = stat.TryGetProperty("HASH32", out var h) && h.ValueKind == JsonValueKind.Number
                    ? h.GetUInt32() : 0;
                int category = stat.TryGetProperty("Category", out var c) && c.ValueKind == JsonValueKind.Number
                    ? c.GetInt32() : -1;
                stats.Add(new(id, row++, hash,
                    stat.TryGetProperty("IsLocal", out var local) && local.GetBoolean(),
                    stat.TryGetProperty("IsPublic", out var pub) && pub.GetBoolean(),
                    stat.TryGetProperty("IsWeaponLocal", out var weapon) && weapon.GetBoolean(),
                    category));
            }
        }

        var mods = new List<GameDataStore.ModRow>();
        var modStats = new List<GameDataStore.ModStatRow>();
        foreach (var mod in Program.Items.Mods.All)
        {
            mods.Add(new(mod.Id, mod.Hash, mod.Name, mod.Domain, mod.GenerationType, mod.Level, mod.ModType));
            for (int i = 0; i < mod.Stats.Count; i++)
            {
                var stat = mod.Stats[i];
                modStats.Add(new(mod.Id, i, stat.Id, stat.Min, stat.Max, stat.Local));
            }
        }

        if (stats.Count == 0 || mods.Count == 0)
        {
            Console.WriteLine("[gateway] game data: reference tables are empty; extract the game tables first");
            return false;
        }

        store.SeedReference(stats, mods, modStats);
        return true;
    }
}
