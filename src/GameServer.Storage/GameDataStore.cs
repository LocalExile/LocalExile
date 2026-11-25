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

    /// <summary>One row of <c>effect_rules</c>: the stat that turns the rule on and the rule's JSON, as the game module reads it.</summary>
    public sealed record EffectRuleRow(string Stat, int Ord, string Rule, string? Note);
    public sealed record EffectSkillRow(string Effect, string How, string? Art, double Scale, double Seconds, int? Radius,
        int Hits, double Every, int Spread, bool Checked);
    public sealed record SettingRow(string Key, double Value);
    public sealed record UniqueWeightRow(string Name, double Weight, double RuthlessWeight);
    public sealed record CurrencyWeightRow(string CurrencyId, double Weight, int DropLevel, bool Enabled,
        string Source, string Confidence, string? Notes);
    public sealed record CurrencySourceRow(string CurrencyId, string SourceKind, string SourceId, double Weight,
        int DropLevel, bool Enabled, string Confidence, string Source, string? Notes);
    public sealed record BossRow(string BossId, string BossName, string? AreaId, string Category, bool Enabled);
    public sealed record BossItemRow(string BossId, string ItemId, string ItemKind, double? Chance,
        string Confidence, string Source, string? Notes, double? OverrideChance = null);
    public sealed record CardAreaRow(string AreaId, string AreaName, string CardId, int MinMapTier, string Source);
    public sealed record CardSourceRow(string CardId, string SourceKind, string SourceId, int? MinMapTier,
        int DropLevel, string Confidence, string Source, string? Notes);
    public sealed record ItemWeightRow(string Category, string ItemId, double Weight, bool Enabled, string Source,
        string Confidence, string Patch, string SampledAt, string? Notes);
    public sealed record ServerLevelAreaRow(string AreaId, bool Generated, uint? SeedInPlaceOfOne);

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    public string Path { get; }

    /// <summary>data/world.db, or <c>Game_WORLD_DB</c>.</summary>
    public static string DefaultPath => Environment.GetEnvironmentVariable("Game_WORLD_DB") ?? System.IO.Path.Combine("data", "world.db");

    /// <summary>The areas the Gateway generates itself and the seeds played in place of seed 1 (server_level_areas).</summary>
    public IReadOnlyList<ServerLevelAreaRow> LoadServerLevelAreas()
    {
        lock (_gate)
        {
            using var command = Command("SELECT area_id, generated, seed_in_place_of_one FROM server_level_areas ORDER BY area_id");
            using var reader = command.ExecuteReader();
            var result = new List<ServerLevelAreaRow>();
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetInt64(1) != 0, reader.IsDBNull(2) ? null : checked((uint)reader.GetInt64(2))));
            return result;
        }
    }

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

    /// <summary>The enabled, reviewed rows of <c>effect_rules</c>, in stat and ord order.</summary>
    public IReadOnlyList<EffectRuleRow> LoadEffectRules()
    {
        lock (_gate)
        {
            using var command = Command("SELECT stat, ord, rule, note FROM effect_rules WHERE enabled = 1 AND reviewed = 1 ORDER BY stat, ord");
            using var reader = command.ExecuteReader();
            var result = new List<EffectRuleRow>();
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            return result;
        }
    }

    /// <summary>The enabled rows of <c>effect_skills</c>.</summary>
    /// <summary>One enabled row of <c>effect_buffs</c>: the buff a released skill gives its user.</summary>
    /// <summary>One fx_modifiers row that has conditions or a plain stat to count onto: what the game data's
    /// conditional stats are read from (StatConditions.LoadTable).</summary>
    public sealed record FxModifierRow(string Stat, int Ord, double Scale, string Conditions, string? CountsAs);

    /// <summary>The enabled fx_modifiers rows with conditions or a counts_as, in order. Empty when the table or its
    /// counts_as column is not there (an older world.db in a test).</summary>
    public IReadOnlyList<FxModifierRow> LoadFxModifiers()
    {
        lock (_gate)
        {
            using (var column = Command("SELECT 1 FROM pragma_table_info('fx_modifiers') WHERE name = 'counts_as'"))
                if (column.ExecuteScalar() is null) return [];
            using var command = Command("SELECT stat, ord, scale, conditions, counts_as FROM fx_modifiers " +
                "WHERE enabled = 1 AND (counts_as IS NOT NULL OR conditions <> '[]') ORDER BY stat, ord");
            using var reader = command.ExecuteReader();
            var result = new List<FxModifierRow>();
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetDouble(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            return result;
        }
    }

    /// <summary>One fx_modifiers row whose stat counts once per something the character has ("per Power Charge"): the
    /// stat it adds to (the row's quantity) and what it is per, as written (ChargeEffects.LoadTable).</summary>
    public sealed record FxPerRow(string Stat, int Ord, string Target, string Per);

    /// <summary>The enabled fx_modifiers rows with a "per", in order. Empty when the table or the column is not there
    /// (an older world.db in a test).</summary>
    public IReadOnlyList<FxPerRow> LoadFxPerRows()
    {
        lock (_gate)
        {
            using (var column = Command("SELECT 1 FROM pragma_table_info('fx_modifiers') WHERE name = 'per'"))
                if (column.ExecuteScalar() is null) return [];
            using var command = Command("SELECT stat, ord, quantity, per FROM fx_modifiers WHERE enabled = 1 AND per IS NOT NULL ORDER BY stat, ord");
            using var reader = command.ExecuteReader();
            var result = new List<FxPerRow>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
            return result;
        }
    }

    /// <summary>The enabled fx_modifiers rows whose target is the area's players ("players_area"), in order: the area's
    /// stat, the player's stat (the row's quantity) and how many of it one point gives.</summary>
    public IReadOnlyList<FxAreaMonsterRow> LoadFxAreaPlayerRows()
    {
        lock (_gate)
        {
            using (var exists = Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fx_modifiers'"))
                if (exists.ExecuteScalar() is null) return [];
            using var command = Command("SELECT stat, quantity, scale FROM fx_modifiers WHERE enabled = 1 AND target = 'players_area' ORDER BY stat, ord");
            using var reader = command.ExecuteReader();
            var result = new List<FxAreaMonsterRow>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetDouble(2)));
            return result;
        }
    }

    /// <summary>One fx_skill_stats row: a character's stat that is a general stat for one skill.</summary>
    public sealed record FxSkillStatRow(string Stat, string Skill, string Gives, double Scale);

    /// <summary>The enabled fx_skill_stats rows. Empty when the table is not there (an older world.db in a test).</summary>
    public IReadOnlyList<FxSkillStatRow> LoadFxSkillStats()
    {
        lock (_gate)
        {
            using (var exists = Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fx_skill_stats'"))
                if (exists.ExecuteScalar() is null) return [];
            using var command = Command("SELECT stat, skill, gives, scale FROM fx_skill_stats WHERE enabled = 1 ORDER BY stat, ord");
            using var reader = command.ExecuteReader();
            var result = new List<FxSkillStatRow>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3)));
            return result;
        }
    }

    /// <summary>One fx_per_attribute row: a stat that gives another stat once for every so many points of an attribute.</summary>
    public sealed record FxPerAttributeRow(string Stat, string CountsAs, string Attribute, int Every, double Scale);

    /// <summary>The enabled fx_per_attribute rows. Empty when the table is not there (an older world.db in a test).</summary>
    public IReadOnlyList<FxPerAttributeRow> LoadFxPerAttribute()
    {
        lock (_gate)
        {
            using (var exists = Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fx_per_attribute'"))
                if (exists.ExecuteScalar() is null) return [];
            using var command = Command("SELECT stat, counts_as, attribute, every, scale FROM fx_per_attribute WHERE enabled = 1 ORDER BY stat, ord");
            using var reader = command.ExecuteReader();
            var result = new List<FxPerAttributeRow>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetDouble(4)));
            return result;
        }
    }

    /// <summary>One fx_aliases row: a stat that is also another stat, with no condition.</summary>
    public sealed record FxAliasRow(string Stat, string CountsAs, double Scale);

    /// <summary>The enabled fx_aliases rows. Empty when the table is not there (an older world.db in a test).</summary>
    public IReadOnlyList<FxAliasRow> LoadFxAliases()
    {
        lock (_gate)
        {
            using (var exists = Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fx_aliases'"))
                if (exists.ExecuteScalar() is null) return [];
            using var command = Command("SELECT stat, counts_as, scale FROM fx_aliases WHERE enabled = 1 ORDER BY stat, ord");
            using var reader = command.ExecuteReader();
            var result = new List<FxAliasRow>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetDouble(2)));
            return result;
        }
    }

    /// <summary>The fx_aura_effects rows (an aura or buff skill, a stat that is its own increased effect). Empty when the
    /// table is not there (an older world.db in a test).</summary>
    public IReadOnlyList<(string Family, string Stat)> LoadFxAuraEffects()
    {
        lock (_gate)
        {
            using (var exists = Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fx_aura_effects'"))
                if (exists.ExecuteScalar() is null) return [];
            using var command = Command("SELECT family, stat FROM fx_aura_effects ORDER BY family, ord");
            using var reader = command.ExecuteReader();
            var result = new List<(string, string)>();
            while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1)));
            return result;
        }
    }

    /// <summary>One fx_reads_as row: a stat as a bundle writes it, and the stat an actor of this kind reads for it.</summary>
    public sealed record FxReadsAsRow(string Actor, string Stat, string Reads, double Scale);

    /// <summary>The fx_reads_as rows, in order. Empty when the table is not there (an older world.db in a test).</summary>
    public IReadOnlyList<FxReadsAsRow> LoadFxReadsAs()
    {
        lock (_gate)
        {
            using (var exists = Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fx_reads_as'"))
                if (exists.ExecuteScalar() is null) return [];
            using var command = Command("SELECT actor, stat, reads, scale FROM fx_reads_as ORDER BY actor, stat, ord");
            using var reader = command.ExecuteReader();
            var result = new List<FxReadsAsRow>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3)));
            return result;
        }
    }

    /// <summary>The "unscalable" flags of the fx tables: per stat (fx_stats.unscalable, every row) and per item line
    /// (fx_lines, a Mods Id). Empty when the column or the table is not there (an older world.db in a test).</summary>
    public (IReadOnlyDictionary<string, bool> Stats, IReadOnlyDictionary<string, bool> Lines) LoadFxUnscalable()
    {
        lock (_gate)
        {
            var stats = new Dictionary<string, bool>(StringComparer.Ordinal);
            var lines = new Dictionary<string, bool>(StringComparer.Ordinal);
            using (var column = Command("SELECT 1 FROM pragma_table_info('fx_stats') WHERE name = 'unscalable'"))
                if (column.ExecuteScalar() is not null)
                {
                    using var command = Command("SELECT stat, unscalable FROM fx_stats");
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) stats[reader.GetString(0)] = reader.GetInt32(1) != 0;
                }
            using (var table = Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fx_lines'"))
                if (table.ExecuteScalar() is not null)
                {
                    using var command = Command("SELECT mod, unscalable FROM fx_lines");
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) lines[reader.GetString(0)] = reader.GetInt32(1) != 0;
                }
            return (stats, lines);
        }
    }

    /// <summary>One fx_modifiers row of an area modifier that gives the area's monsters a stat they already act on: the
    /// area's stat, the monster's stat (the row's quantity) and how many of it one point gives.</summary>
    public sealed record FxAreaMonsterRow(string Stat, string MonsterStat, double Scale);

    /// <summary>The enabled fx_modifiers rows whose target is the area's monsters ("enemies_area"), in order.</summary>
    public IReadOnlyList<FxAreaMonsterRow> LoadFxAreaMonsterRows()
    {
        lock (_gate)
        {
            using (var exists = Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fx_modifiers'"))
                if (exists.ExecuteScalar() is null) return [];
            using var command = Command("SELECT stat, quantity, scale FROM fx_modifiers WHERE enabled = 1 AND target = 'enemies_area' ORDER BY stat, ord");
            using var reader = command.ExecuteReader();
            var result = new List<FxAreaMonsterRow>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetDouble(2)));
            return result;
        }
    }

    /// <summary>One fx rule with its rows (fx_rules, fx_rule_sources, fx_rule_when, fx_rule_if, fx_rule_do), as written:
    /// the game module turns it into a rule of its engine (Modules/Core/Game/Effects/FxRules.cs).</summary>
    public sealed record FxRuleRow(string Id, string Book, string Title, string? Replaces, string Status,
        IReadOnlyList<(string Kind, string Key)> Sources, IReadOnlyList<(string Trigger, string Args)> When,
        IReadOnlyList<(int Group, string Condition, bool Negate, string Args)> If,
        IReadOnlyList<(string Action, string Args, string Target, string TargetArgs, string Value, string Timers)> Do);

    /// <summary>The enabled fx rules, each with its rows in order. Empty when the tables are not there (an older
    /// world.db in a test).</summary>
    public IReadOnlyList<FxRuleRow> LoadFxRules()
    {
        lock (_gate)
        {
            using (var exists = Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fx_rules'"))
                if (exists.ExecuteScalar() is null) return [];
            var sources = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
            var when = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
            var conditions = new Dictionary<string, List<(int, string, bool, string)>>(StringComparer.Ordinal);
            var actions = new Dictionary<string, List<(string, string, string, string, string, string)>>(StringComparer.Ordinal);
            static List<T> Of<T>(Dictionary<string, List<T>> map, string rule) =>
                map.TryGetValue(rule, out var list) ? list : map[rule] = [];
            using (var command = Command("SELECT rule, kind, key FROM fx_rule_sources ORDER BY rule, ord"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) Of(sources, reader.GetString(0)).Add((reader.GetString(1), reader.GetString(2)));
            using (var command = Command("SELECT rule, trigger, args FROM fx_rule_when ORDER BY rule, ord"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) Of(when, reader.GetString(0)).Add((reader.GetString(1), reader.GetString(2)));
            using (var command = Command("SELECT rule, grp, condition, negate, args FROM fx_rule_if ORDER BY rule, grp, ord"))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    Of(conditions, reader.GetString(0)).Add((reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3) != 0, reader.GetString(4)));
            using (var command = Command("SELECT rule, action, args, target, target_args, value, timers FROM fx_rule_do ORDER BY rule, ord"))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    Of(actions, reader.GetString(0)).Add((reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetString(4), reader.GetString(5), reader.GetString(6)));
            var result = new List<FxRuleRow>();
            using (var command = Command("SELECT id, book, title, replaces, status FROM fx_rules WHERE enabled = 1 ORDER BY id"))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    string id = reader.GetString(0);
                    result.Add(new(id, reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetString(4), Of(sources, id), Of(when, id), Of(conditions, id), Of(actions, id)));
                }
            return result;
        }
    }

    public sealed record EffectBuffRow(string Effect, string? Buff, string? Routine, double? Seconds, string Stats, bool Checked);

    public IReadOnlyList<EffectBuffRow> LoadEffectBuffs()
    {
        lock (_gate)
        {
            using var command = Command("SELECT effect, buff, routine, seconds, stats, checked FROM effect_buffs WHERE enabled = 1 ORDER BY effect");
            using var reader = command.ExecuteReader();
            var result = new List<EffectBuffRow>();
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    reader.GetString(4), reader.GetInt32(5) != 0));
            return result;
        }
    }

    public IReadOnlyList<EffectSkillRow> LoadEffectSkills()
    {
        lock (_gate)
        {
            using var command = Command("SELECT effect, how, art, scale, seconds, radius, hits, every, spread, checked FROM effect_skills WHERE enabled = 1 ORDER BY effect");
            using var reader = command.ExecuteReader();
            var result = new List<EffectSkillRow>();
            while (reader.Read())
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetDouble(3), reader.GetDouble(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.GetInt32(6), reader.GetDouble(7), reader.GetInt32(8), reader.GetInt32(9) != 0));
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

    /// <summary>Every row of a catalogue table (the item and jewel catalogues: unique_items, radius_jewels, ...) in the
    /// order of <paramref name="orderBy"/>, its columns by name (INTEGER as long, TEXT as string, NULL as null).</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> LoadRows(string table, string orderBy)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(table, "^[a-z_]+$")
            || !System.Text.RegularExpressions.Regex.IsMatch(orderBy, "^[a-z_]+(, [a-z_]+)*$"))
            throw new ArgumentException($"not a table and its order: {table} by {orderBy}");
        lock (_gate)
        {
            using var command = Command($"SELECT * FROM {table} ORDER BY {orderBy}");
            using var reader = command.ExecuteReader();
            var result = new List<IReadOnlyDictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                for (int i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                result.Add(row);
            }
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
                SELECT d.boss_id, d.item_id, d.item_kind, d.chance, d.confidence, d.source, d.notes, o.chance
                FROM drop_boss_items AS d LEFT JOIN encounter_jewel_drop_overrides AS o
                  ON d.boss_id = o.boss_id AND d.item_id = o.item_id
                UNION ALL
                SELECT o.boss_id, o.item_id, 'unique', NULL, 'unknown', 'operator override', NULL, o.chance
                FROM encounter_jewel_drop_overrides AS o
                WHERE NOT EXISTS (SELECT 1 FROM drop_boss_items AS d WHERE d.boss_id = o.boss_id AND d.item_id = o.item_id)
                ORDER BY 1, 3, 2
                """);
            using var reader = command.ExecuteReader();
            var rows = new List<BossItemRow>();
            while (reader.Read())
                rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.GetString(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetDouble(7)));
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
