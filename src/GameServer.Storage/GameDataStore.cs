using Microsoft.Data.Sqlite;

namespace GameServer.Storage;

/// <summary>
/// The game content in <c>data/world.db</c> (Agents/docs/dev/databases.md): settings, unique weights, the item and
/// jewel catalogues, drop tables, mod range and limit overrides. The database is built from <c>sql/</c>; a change to
/// the content is an update file there, and the Gateway reads the rows at startup and on <c>/reload</c>.
/// </summary>
public sealed class GameDataStore : IDisposable
{
    public sealed record ModStatOverride(string ModId, int StatIndex, int Minimum, int Maximum);
    public sealed record LimitOverride(string Key, int Value);
    public sealed record CatalogRow(string Kind, string Key, string Payload, bool Enabled);
    public sealed record SettingRow(string Key, double Value);
    public sealed record UniqueWeightRow(string Name, double Weight, double RuthlessWeight);
    public sealed record CurrencyWeightRow(string CurrencyId, double Weight, int DropLevel, bool Enabled,
        string Source, string Confidence, string? Notes);
    public sealed record CurrencySourceRow(string CurrencyId, string SourceKind, string SourceId, double Weight,
        int DropLevel, bool Enabled, string Confidence, string Source, string? Notes);
    public sealed record BossRow(string BossId, string BossName, string? AreaId, string Category, bool Enabled);
    public sealed record BossItemRow(string BossId, string ItemId, string ItemKind, double? Chance,
        string Confidence, string Source, string? Notes);
    public sealed record CardAreaRow(string AreaId, string AreaName, string CardId, int MinMapTier, string Source);
    public sealed record CardSourceRow(string CardId, string SourceKind, string SourceId, int? MinMapTier,
        int DropLevel, string Confidence, string Source, string? Notes);
    public sealed record ItemWeightRow(string Category, string ItemId, double Weight, bool Enabled, string Source,
        string Confidence, string Patch, string SampledAt, string? Notes);

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    public string Path { get; }

    /// <summary>Opens the database and brings it up to date from <c>sql/base/world</c> and its updates.</summary>
    public GameDataStore(string path)
    {
        Path = path;
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);
        _db = SqlUpdater.Open(path);
        try { SqlUpdater.Update(_db, "world"); }
        catch
        {
            _db.Dispose();
            throw;
        }
    }

    public IReadOnlyList<ModStatOverride> LoadModRanges()
    {
        lock (_gate)
        {
            using var command = Command("SELECT mod_id, stat_index, minimum, maximum FROM mod_stat_overrides ORDER BY mod_id, stat_index");
            using var reader = command.ExecuteReader();
            var result = new List<ModStatOverride>();
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
            return result;
        }
    }

    public IReadOnlyList<LimitOverride> LoadLimits()
    {
        lock (_gate)
        {
            using var command = Command("SELECT key, value FROM limit_overrides ORDER BY key");
            using var reader = command.ExecuteReader();
            var result = new List<LimitOverride>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt32(1)));
            return result;
        }
    }

    public IReadOnlyList<UniqueWeightRow> LoadUniqueWeights()
    {
        lock (_gate)
        {
            using var command = Command("SELECT name, weight, ruthless_weight FROM unique_weights ORDER BY name");
            using var reader = command.ExecuteReader();
            var result = new List<UniqueWeightRow>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2)));
            return result;
        }
    }

    public IReadOnlyList<UniqueWeightRow> LoadUniqueWeightOverrides()
    {
        lock (_gate)
        {
            using var command = Command("SELECT name, weight, ruthless_weight FROM unique_weight_overrides ORDER BY name");
            using var reader = command.ExecuteReader();
            var result = new List<UniqueWeightRow>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2)));
            return result;
        }
    }

    public (int Weights, int Overrides) UniqueWeightCounts()
    {
        lock (_gate)
        {
            int Count(string table)
            {
                using var command = Command($"SELECT COUNT(*) FROM {table}");
                return Convert.ToInt32(command.ExecuteScalar());
            }
            return (Count("unique_weights"), Count("unique_weight_overrides"));
        }
    }

    public IReadOnlyList<SettingRow> LoadSettings()
    {
        lock (_gate)
        {
            using var command = Command("SELECT key, value FROM system_settings ORDER BY key");
            using var reader = command.ExecuteReader();
            var result = new List<SettingRow>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetDouble(1)));
            return result;
        }
    }

    public IReadOnlyList<CatalogRow> LoadCatalog(string kind)
    {
        lock (_gate)
        {
            using var command = Command("SELECT kind, key, payload, enabled FROM catalog_entries WHERE kind = $kind ORDER BY key",
                ("$kind", kind));
            using var reader = command.ExecuteReader();
            var result = new List<CatalogRow>();
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0));
            return result;
        }
    }

    public (int CurrencyWeights, int CurrencySources, int Bosses, int BossItems, int CardAreas, int CardSources,
        int ItemWeights) DropCounts()
    {
        lock (_gate)
        {
            int Count(string table)
            {
                using var command = Command($"SELECT COUNT(*) FROM {table}");
                return Convert.ToInt32(command.ExecuteScalar());
            }
            return (Count("drop_currency_weights"), Count("drop_currency_sources"), Count("drop_bosses"),
                Count("drop_boss_items"), Count("drop_card_areas"), Count("drop_card_sources"),
                Count("drop_item_weights"));
        }
    }

    public IReadOnlyList<CurrencyWeightRow> LoadCurrencyWeights()
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT currency_id, weight, drop_level, enabled, source, confidence, notes
                FROM drop_currency_weights ORDER BY currency_id
                """);
            using var reader = command.ExecuteReader();
            var rows = new List<CurrencyWeightRow>();
            while (reader.Read())
                rows.Add(new(reader.GetString(0), reader.GetDouble(1), reader.GetInt32(2), reader.GetInt32(3) != 0,
                    reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
            return rows;
        }
    }

    public IReadOnlyList<CurrencySourceRow> LoadCurrencySources()
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT currency_id, source_kind, source_id, weight, drop_level, enabled, confidence, source, notes
                FROM drop_currency_sources ORDER BY currency_id, source_kind, source_id
                """);
            using var reader = command.ExecuteReader();
            var rows = new List<CurrencySourceRow>();
            while (reader.Read())
                rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3),
                    reader.GetInt32(4), reader.GetInt32(5) != 0, reader.GetString(6), reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            return rows;
        }
    }

    public IReadOnlyList<BossRow> LoadBosses()
    {
        lock (_gate)
        {
            using var command = Command("SELECT boss_id, boss_name, area_id, category, enabled FROM drop_bosses ORDER BY boss_id");
            using var reader = command.ExecuteReader();
            var rows = new List<BossRow>();
            while (reader.Read())
                rows.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3), reader.GetInt32(4) != 0));
            return rows;
        }
    }

    public IReadOnlyList<BossItemRow> LoadBossItems()
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT boss_id, item_id, item_kind, chance, confidence, source, notes
                FROM drop_boss_items ORDER BY boss_id, item_kind, item_id
                """);
            using var reader = command.ExecuteReader();
            var rows = new List<BossItemRow>();
            while (reader.Read())
                rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.GetString(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            return rows;
        }
    }

    public IReadOnlyList<CardAreaRow> LoadCardAreas()
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT area_id, area_name, card_id, min_map_tier, source
                FROM drop_card_areas ORDER BY area_id, card_id
                """);
            using var reader = command.ExecuteReader();
            var rows = new List<CardAreaRow>();
            while (reader.Read())
                rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                    reader.GetString(4)));
            return rows;
        }
    }

    public IReadOnlyList<CardSourceRow> LoadCardSources()
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT card_id, source_kind, source_id, min_map_tier, drop_level, confidence, source, notes
                FROM drop_card_sources ORDER BY card_id, source_kind, source_id
                """);
            using var reader = command.ExecuteReader();
            var rows = new List<CardSourceRow>();
            while (reader.Read())
                rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5),
                    reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
            return rows;
        }
    }

    public IReadOnlyList<ItemWeightRow> LoadItemWeights()
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT category, item_id, weight, enabled, source, confidence, patch, sampled_at, notes
                FROM drop_item_weights ORDER BY category, item_id
                """);
            using var reader = command.ExecuteReader();
            var rows = new List<ItemWeightRow>();
            while (reader.Read())
                rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetDouble(2), reader.GetInt32(3) != 0,
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            return rows;
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }
}
