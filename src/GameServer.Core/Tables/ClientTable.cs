using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace GameServer.Core;

/// <summary>
/// One of the client's tables, held in memory (<see cref="ClientTables.Table"/>): its rows in order, so a row's position
/// is its index (<c>_row</c>), the key other tables refer to it by. Immutable, so catalogues share it.
/// The columns keep their declared SQLite types: <c>INTEGER</c>, <c>REAL</c>, <c>TEXT</c>, <c>BOOLEAN</c>, and the list
/// columns (<c>JSON</c>, flat lists of numbers or strings), read with <see cref="ClientRow.Ints"/> and its kin.
/// <para>Stored by column: integers as 32-bit values unless one needs 64, each distinct string once per load, and a list
/// column's items parsed once into one array for the whole column (a row's items are a slice of it), so a list read is a
/// copy, never a JSON parse.</para>
/// </summary>
public sealed class ClientTable : IReadOnlyList<ClientRow>
{
    internal enum Kind { Integer, Real, Text, Boolean, List }

    /// <summary>What a list column's items are: all integers, any number with a fraction (then all as reals), strings,
    /// or none in any row. <see cref="Json"/>: items of more than one kind (or not a flat list), kept as text and parsed
    /// on read, as every list was before.</summary>
    internal enum Items { None, Integers, Reals, Texts, Json }

    internal sealed class Column(string name, Kind kind, int rows)
    {
        public readonly string Name = name;
        public readonly Kind Kind = kind;
        public readonly BitArray Nulls = new(rows);
        /// <summary>Integer and Boolean values; <see cref="Longs"/> instead when one needs 64 bits.</summary>
        public int[]? Ints = kind is Kind.Integer or Kind.Boolean ? new int[rows] : null;
        public long[]? Longs;
        public readonly double[]? Reals = kind == Kind.Real ? new double[rows] : null;
        /// <summary>Text values; a <see cref="Items.Json"/> list column's raw text.</summary>
        public string?[]? Texts = kind == Kind.Text ? new string?[rows] : null;

        /// <summary>List columns: row i's items are [Starts[i], Starts[i + 1]) of the item array of <see cref="ItemKind"/>.</summary>
        public int[]? Starts;
        public Items ItemKind;
        public long[]? ItemLongs;
        public double[]? ItemReals;
        public string[]? ItemTexts;

        public long Integer(int row) => Ints is { } i ? i[row] : Longs![row];

        public void SetInteger(int row, long value)
        {
            if (Ints is { } ints)
            {
                if (value is >= int.MinValue and <= int.MaxValue) { ints[row] = (int)value; return; }
                Longs = new long[ints.Length];
                for (int i = 0; i < row; i++) Longs[i] = ints[i];
                Ints = null;
            }
            Longs![row] = value;
        }
    }

    readonly Column[] columns;
    readonly Dictionary<string, Column> byName;
    readonly ConcurrentDictionary<string, ILookup<long, ClientRow>> indexes = new(StringComparer.Ordinal);

    public string Name { get; }
    public int Count { get; }

    internal ClientTable(string name, int count, Column[] columns)
    {
        Name = name;
        Count = count;
        this.columns = columns;
        byName = new Dictionary<string, Column>(columns.Length, StringComparer.Ordinal);
        foreach (var c in columns) byName.TryAdd(c.Name, c);
    }

    /// <summary>The column names in table order (without <c>_row</c>).</summary>
    public IEnumerable<string> Columns => columns.Select(c => c.Name);

    public bool HasColumn(string column) => byName.ContainsKey(column);

    public ClientRow this[int index] =>
        (uint)index < (uint)Count ? new(this, index) : throw new ArgumentOutOfRangeException(nameof(index), $"{Name} has {Count} rows");

    /// <summary>The row at <paramref name="index"/>, or null past the end.</summary>
    public ClientRow? Row(int index) => (uint)index < (uint)Count ? new ClientRow(this, index) : null;

    public IEnumerator<ClientRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return new ClientRow(this, i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal Column? Find(string column) => byName.GetValueOrDefault(column);

    internal Column Get(string column) =>
        byName.TryGetValue(column, out var c) ? c : throw new KeyNotFoundException($"the client table {Name} has no column {column}");

    /// <summary>The rows by the value of a number column (a generated table's link column: Agents/docs/dev/databases.md,
    /// "Generated tables"; or any key, such as GrantedEffects by <c>ActiveSkill</c>), in table order; rows where it is
    /// null are left out, and a table without the column has none. Built on the first call for the column and kept
    /// with the table, so a lookup by key is a hash probe, never a scan.</summary>
    public ILookup<long, ClientRow> By(string column) => indexes.GetOrAdd(column, static (c, t) =>
    {
        if (t.Find(c) is not { } col) return Array.Empty<ClientRow>().ToLookup(r => 0L);
        if (col.Kind is not (Kind.Integer or Kind.Boolean or Kind.Real))
            throw new InvalidCastException($"{t.Name}.{c} is not a number column");
        return t.Where(r => !r.IsNull(c)).ToLookup(r => r.Long(c));
    }, this);

    /// <summary>The first row whose text column holds <paramref name="value"/>, or null: an Id lookup. Uses an index built
    /// on the first call for the column.</summary>
    public ClientRow? First(string column, string value) =>
        textIndexes.GetOrAdd(column, static (c, t) =>
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var col = t.Get(c);
            if (col.Kind != Kind.Text) throw new InvalidCastException($"{t.Name}.{c} is not a text column");
            for (int i = 0; i < t.Count; i++)
                if (!col.Nulls[i] && col.Texts![i] is { } s) index.TryAdd(s, i);
            return index;
        }, this).TryGetValue(value, out int row) ? new ClientRow(this, row) : null;

    readonly ConcurrentDictionary<string, Dictionary<string, int>> textIndexes = new(StringComparer.Ordinal);

    internal static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>Reads the table's rows (<c>SELECT * ... ORDER BY _row</c>) on an open connection, straight from SQLite's
    /// statement API (no per-value boxing); strings go through <paramref name="strings"/>, so each distinct one is kept
    /// once.</summary>
    internal static ClientTable Read(SqliteConnection db, string name, StringPool strings)
    {
        int count;
        using (var c = db.CreateCommand())
        {
            c.CommandText = $"SELECT count(*) FROM {Quote(name)}";
            count = Convert.ToInt32(c.ExecuteScalar());
        }
        int rc = raw.sqlite3_prepare_v2(db.Handle, $"SELECT * FROM {Quote(name)} ORDER BY \"_row\"", out sqlite3_stmt stmt);
        if (rc != raw.SQLITE_OK) throw new SqliteException($"reading {name}: {raw.sqlite3_errmsg(db.Handle).utf8_to_string()}", rc);
        using (stmt)
        {
            var columns = new List<Column>();
            var ordinals = new List<int>();
            var lists = new List<ListBuilder?>();
            int fields = raw.sqlite3_column_count(stmt);
            for (int i = 0; i < fields; i++)
            {
                string column = raw.sqlite3_column_name(stmt, i).utf8_to_string();
                if (column == "_row") continue;
                Kind kind = (raw.sqlite3_column_decltype(stmt, i).utf8_to_string() ?? "").ToUpperInvariant() switch
                {
                    "INTEGER" => Kind.Integer,
                    "REAL" => Kind.Real,
                    "TEXT" => Kind.Text,
                    "BOOLEAN" => Kind.Boolean,
                    _ => Kind.List,
                };
                columns.Add(new Column(column, kind, count));
                ordinals.Add(i);
                lists.Add(kind == Kind.List ? new ListBuilder(count) : null);
            }
            int row = 0;
            while (row < count && (rc = raw.sqlite3_step(stmt)) == raw.SQLITE_ROW)
            {
                for (int k = 0; k < columns.Count; k++)
                {
                    var column = columns[k];
                    int i = ordinals[k];
                    if (raw.sqlite3_column_type(stmt, i) == raw.SQLITE_NULL)
                    {
                        column.Nulls[row] = true;
                        lists[k]?.Add(row, default, strings);
                        continue;
                    }
                    switch (column.Kind)
                    {
                        case Kind.Integer or Kind.Boolean: column.SetInteger(row, raw.sqlite3_column_int64(stmt, i)); break;
                        case Kind.Real: column.Reals![row] = raw.sqlite3_column_double(stmt, i); break;
                        case Kind.Text: column.Texts![row] = strings.Get(raw.sqlite3_column_blob(stmt, i)); break;
                        default: lists[k]!.Add(row, raw.sqlite3_column_blob(stmt, i), strings); break;
                    }
                }
                row++;
            }
            if (rc is not (raw.SQLITE_ROW or raw.SQLITE_DONE))
                throw new SqliteException($"reading {name}: {raw.sqlite3_errmsg(db.Handle).utf8_to_string()}", rc);
            for (int k = 0; k < columns.Count; k++) lists[k]?.Finish(columns[k]);
            return new ClientTable(name, row, [.. columns]);
        }
    }

    /// <summary>A list column being read: the items of every row so far, in one array per kind. A column whose items
    /// turn out to be of more than one kind switches to keeping each row's JSON text (the rows already parsed are written
    /// back from their items, which were of one kind) and is parsed on read.</summary>
    sealed class ListBuilder(int rows)
    {
        readonly int[] starts = new int[rows + 1];
        readonly List<long> longs = [];
        List<double>? reals;
        readonly List<string> texts = [];
        readonly BitArray present = new(rows);
        string?[]? raws;
        bool numbers, strings;

        int Count => strings ? texts.Count : reals?.Count ?? longs.Count;

        public void Add(int row, ReadOnlySpan<byte> json, StringPool pool)
        {
            if (raws is not null) { raws[row] = json.IsEmpty ? null : pool.Get(json); return; }
            int mark = Count;
            bool wasNumbers = numbers, wasStrings = strings;
            if (!json.IsEmpty && !Parse(json, pool))
            {
                Truncate(mark, wasNumbers, wasStrings);
                raws = new string?[starts.Length - 1];
                for (int r = 0; r < row; r++) raws[r] = present[r] ? Write(r) : null;
                raws[row] = pool.Get(json);
                return;
            }
            present[row] = !json.IsEmpty;
            starts[row + 1] = Count;
        }

        bool Parse(ReadOnlySpan<byte> json, StringPool pool)
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.Number && !strings)
                {
                    numbers = true;
                    if (reals is null && reader.TryGetInt64(out long l)) longs.Add(l);
                    else
                    {
                        reals ??= longs.Select(v => (double)v).ToList();
                        reals.Add(reader.GetDouble());
                    }
                }
                else if (reader.TokenType == JsonTokenType.String && !numbers)
                {
                    strings = true;
                    texts.Add(reader.ValueIsEscaped ? pool.Intern(reader.GetString()!) : pool.Get(reader.ValueSpan));
                }
                else return false;
            }
            return true;
        }

        // Drops what a row that failed added (its items before the one of the other kind).
        void Truncate(int count, bool wasNumbers, bool wasStrings)
        {
            if (strings && wasStrings) texts.RemoveRange(count, texts.Count - count);
            else if (strings) { texts.Clear(); strings = false; }
            if (numbers && wasNumbers)
            {
                if (reals is not null) reals.RemoveRange(count, reals.Count - count);
                longs.RemoveRange(Math.Min(count, longs.Count), longs.Count - Math.Min(count, longs.Count));
            }
            else if (numbers) { longs.Clear(); reals = null; numbers = false; }
        }

        // A parsed row as JSON again (items of one kind: the value round-trips).
        string Write(int row)
        {
            int from = starts[row], to = starts[row + 1];
            object[] items = strings ? [.. texts.GetRange(from, to - from)]
                : reals is not null ? [.. reals.GetRange(from, to - from).Cast<object>()]
                : [.. longs.GetRange(from, to - from).Cast<object>()];
            return JsonSerializer.Serialize(items);
        }

        public void Finish(Column column)
        {
            if (raws is not null) { column.ItemKind = Items.Json; column.Texts = raws; return; }
            column.Starts = starts;
            if (strings) { column.ItemKind = Items.Texts; column.ItemTexts = [.. texts]; }
            else if (reals is not null) { column.ItemKind = Items.Reals; column.ItemReals = [.. reals]; }
            else if (numbers) { column.ItemKind = Items.Integers; column.ItemLongs = [.. longs]; }
            else column.ItemKind = Items.None;
        }
    }

    /// <summary>Reads one table from the client.db at <paramref name="dbPath"/>, read-only and uncached: for tools and
    /// tests that find the database themselves. The servers use <see cref="ClientTables.Table"/>.</summary>
    public static ClientTable Load(string dbPath, string name)
    {
        using var db = ClientTables.OpenReadOnly(dbPath);
        return Read(db, name, new StringPool());
    }

    /// <summary>A table made in memory, for a test: <paramref name="rows"/> hold each row's values by column name
    /// (long/int, double/float, string, bool, null, or an array of numbers or strings for a list column).</summary>
    public static ClientTable FromRows(string name, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var names = rows.SelectMany(r => r.Keys).Distinct().ToList();
        var columns = new Column[names.Count];
        var pool = new StringPool();
        for (int k = 0; k < names.Count; k++)
        {
            object? sample = rows.Select(r => r.GetValueOrDefault(names[k])).FirstOrDefault(v => v is not null);
            Kind kind = sample switch
            {
                bool => Kind.Boolean,
                double or float or decimal => Kind.Real,
                string => Kind.Text,
                IEnumerable => Kind.List,
                _ => Kind.Integer,
            };
            var column = columns[k] = new Column(names[k], kind, rows.Count);
            var list = kind == Kind.List ? new ListBuilder(rows.Count) : null;
            for (int i = 0; i < rows.Count; i++)
            {
                object? v = rows[i].GetValueOrDefault(names[k]);
                if (v is null) { column.Nulls[i] = true; list?.Add(i, default, pool); continue; }
                switch (kind)
                {
                    case Kind.Boolean: column.SetInteger(i, (bool)v ? 1 : 0); break;
                    case Kind.Integer: column.SetInteger(i, Convert.ToInt64(v)); break;
                    case Kind.Real: column.Reals![i] = Convert.ToDouble(v); break;
                    case Kind.Text: column.Texts![i] = (string)v; break;
                    default: list!.Add(i, JsonSerializer.SerializeToUtf8Bytes(v), pool); break;
                }
            }
            list?.Finish(column);
        }
        return new ClientTable(name, rows.Count, columns);
    }
}

/// <summary>Strings read from client.db, each distinct value kept once: 2.25 million text cells of the tables the
/// servers read hold 382 thousand distinct strings. Looked up by the UTF-8 bytes' characters, so a repeat allocates
/// nothing. Not thread-safe: one per loading thread.</summary>
internal sealed class StringPool
{
    readonly Dictionary<string, string> strings = new(StringComparer.Ordinal);
    readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> lookup;
    char[] buffer = new char[256];

    public StringPool() => lookup = strings.GetAlternateLookup<ReadOnlySpan<char>>();

    public int Count => strings.Count;

    public string Get(ReadOnlySpan<byte> utf8, string? decoded = null)
    {
        if (decoded is not null) return Intern(decoded);
        int max = Encoding.UTF8.GetMaxCharCount(utf8.Length);
        if (buffer.Length < max) buffer = new char[Math.Max(max, buffer.Length * 2)];
        var chars = buffer.AsSpan(0, Encoding.UTF8.GetChars(utf8, buffer));
        if (lookup.TryGetValue(chars, out var known)) return known;
        string s = new(chars);
        strings[s] = s;
        return s;
    }

    public string Intern(string s)
    {
        if (strings.TryGetValue(s, out var known)) return known;
        strings[s] = s;
        return s;
    }
}

/// <summary>A row of a <see cref="ClientTable"/>. A column it does not have throws, as does a typed read of a null
/// value; the <c>...OrNull</c> reads return null for both.</summary>
public readonly struct ClientRow(ClientTable table, int index)
{
    public ClientTable Table { get; } = table;

    /// <summary>The row's index in its table (<c>_row</c>): the key other tables use for it.</summary>
    public int Index { get; } = index;

    /// <summary>Whether the table has the column.</summary>
    public bool Has(string column) => Table.HasColumn(column);

    /// <summary>Whether the column is missing or null in this row.</summary>
    public bool IsNull(string column) => Table.Find(column) is not { } c || c.Nulls[Index];

    ClientTable.Column Value(string column)
    {
        var c = Table.Get(column);
        if (c.Nulls[Index]) throw new InvalidOperationException($"{Table.Name}[{Index}].{column} is null");
        return c;
    }

    public string? Text(string column) => Table.Get(column) is var c && c.Nulls[Index] ? null
        : c.Kind == ClientTable.Kind.Text ? c.Texts![Index] : throw Mismatch(column, "text");

    /// <summary>The text, or null when the column is missing or null.</summary>
    public string? TextOrNull(string column) => Table.HasColumn(column) ? Text(column) : null;

    public long Long(string column)
    {
        var c = Value(column);
        return c.Kind switch
        {
            ClientTable.Kind.Integer or ClientTable.Kind.Boolean => c.Integer(Index),
            ClientTable.Kind.Real => (long)c.Reals![Index],
            _ => throw Mismatch(column, "a number"),
        };
    }

    public int Int(string column) => checked((int)Long(column));
    public uint UInt(string column) => unchecked((uint)Long(column));
    public long? LongOrNull(string column) => IsNull(column) ? null : Long(column);
    public int? IntOrNull(string column) => IsNull(column) ? null : Int(column);
    public uint? UIntOrNull(string column) => IsNull(column) ? null : UInt(column);

    public double Real(string column)
    {
        var c = Value(column);
        return c.Kind switch
        {
            ClientTable.Kind.Real => c.Reals![Index],
            ClientTable.Kind.Integer or ClientTable.Kind.Boolean => c.Integer(Index),
            _ => throw Mismatch(column, "a number"),
        };
    }

    public double? RealOrNull(string column) => IsNull(column) ? null : Real(column);

    public bool Bool(string column)
    {
        var c = Value(column);
        return c.Kind is ClientTable.Kind.Boolean or ClientTable.Kind.Integer ? c.Integer(Index) != 0 : throw Mismatch(column, "a boolean");
    }

    public bool? BoolOrNull(string column) => IsNull(column) ? null : Bool(column);

    /// <summary>A list column's numbers (empty when null or missing). A fresh array the caller may keep or change.</summary>
    public int[] Ints(string column)
    {
        if (List(column) is not { } c) return [];
        if (c.ItemKind == ClientTable.Items.Json) return Parse(c, "a list of numbers", static (ref Utf8JsonReader r) =>
            r.TokenType != JsonTokenType.Number ? throw new FormatException() : r.TryGetInt32(out int i) ? i : checked((int)r.GetDouble()));
        var (from, to) = Slice(c);
        var result = new int[to - from];
        switch (c.ItemKind)
        {
            case ClientTable.Items.Integers: for (int i = from; i < to; i++) result[i - from] = checked((int)c.ItemLongs![i]); break;
            case ClientTable.Items.Reals: for (int i = from; i < to; i++) result[i - from] = checked((int)c.ItemReals![i]); break;
            case ClientTable.Items.Texts when to > from: throw Mismatch(column, "a list of numbers");
        }
        return result;
    }

    public long[] Longs(string column)
    {
        if (List(column) is not { } c) return [];
        if (c.ItemKind == ClientTable.Items.Json) return Parse(c, "a list of numbers", static (ref Utf8JsonReader r) =>
            r.TokenType != JsonTokenType.Number ? throw new FormatException() : r.TryGetInt64(out long i) ? i : (long)r.GetDouble());
        var (from, to) = Slice(c);
        var result = new long[to - from];
        switch (c.ItemKind)
        {
            case ClientTable.Items.Integers: c.ItemLongs.AsSpan(from, to - from).CopyTo(result); break;
            case ClientTable.Items.Reals: for (int i = from; i < to; i++) result[i - from] = (long)c.ItemReals![i]; break;
            case ClientTable.Items.Texts when to > from: throw Mismatch(column, "a list of numbers");
        }
        return result;
    }

    public double[] Reals(string column)
    {
        if (List(column) is not { } c) return [];
        if (c.ItemKind == ClientTable.Items.Json) return Parse(c, "a list of numbers", static (ref Utf8JsonReader r) =>
            r.TokenType != JsonTokenType.Number ? throw new FormatException() : r.GetDouble());
        var (from, to) = Slice(c);
        var result = new double[to - from];
        switch (c.ItemKind)
        {
            case ClientTable.Items.Reals: c.ItemReals.AsSpan(from, to - from).CopyTo(result); break;
            case ClientTable.Items.Integers: for (int i = from; i < to; i++) result[i - from] = c.ItemLongs![i]; break;
            case ClientTable.Items.Texts when to > from: throw Mismatch(column, "a list of numbers");
        }
        return result;
    }

    /// <summary>A list column's strings (empty when null or missing).</summary>
    public string[] Texts(string column)
    {
        if (List(column) is not { } c) return [];
        if (c.ItemKind == ClientTable.Items.Json) return Parse(c, "a list of strings", static (ref Utf8JsonReader r) =>
            r.TokenType != JsonTokenType.String ? throw new FormatException() : r.GetString()!);
        var (from, to) = Slice(c);
        return c.ItemKind switch
        {
            ClientTable.Items.Texts => c.ItemTexts.AsSpan(from, to - from).ToArray(),
            ClientTable.Items.Integers or ClientTable.Items.Reals when to > from => throw Mismatch(column, "a list of strings"),
            _ => [],
        };
    }

    (int From, int To) Slice(ClientTable.Column c) => c.Starts is { } s ? (s[Index], s[Index + 1]) : (0, 0);

    /// <summary>The list column, or null when it is missing or null in this row.</summary>
    ClientTable.Column? List(string column)
    {
        if (Table.Find(column) is not { } c || c.Nulls[Index]) return null;
        return c.Kind == ClientTable.Kind.List ? c : throw Mismatch(column, "a list");
    }

    delegate T Item<T>(ref Utf8JsonReader reader);

    // A list column whose items are of more than one kind keeps its text: parsed on every read.
    T[] Parse<T>(ClientTable.Column c, string wanted, Item<T> item)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(c.Texts![Index]!));
        var values = new List<T>();
        try
        {
            reader.Read();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) values.Add(item(ref reader));
        }
        catch (FormatException) { throw Mismatch(c.Name, wanted); }
        return [.. values];
    }

    /// <summary>The value as stored: long, double, string, bool, an array for a list column, or null.</summary>
    public object? Get(string column)
    {
        if (IsNull(column)) return null;
        var c = Table.Get(column);
        return c.Kind switch
        {
            ClientTable.Kind.Integer => c.Integer(Index),
            ClientTable.Kind.Boolean => c.Integer(Index) != 0,
            ClientTable.Kind.Real => c.Reals![Index],
            ClientTable.Kind.Text => c.Texts![Index],
            _ => c.ItemKind switch
            {
                ClientTable.Items.Texts => Texts(column).Cast<object>().ToArray(),
                ClientTable.Items.Integers => Longs(column).Cast<object>().ToArray(),
                ClientTable.Items.Reals => Reals(column).Select(d => d == Math.Floor(d) && Math.Abs(d) < 9e15 ? (object)(long)d : d).ToArray(),
                ClientTable.Items.None => Array.Empty<object>(),
                _ => Parse<object>(c, "a list of numbers or strings", static (ref Utf8JsonReader r) => r.TokenType switch
                {
                    JsonTokenType.String => r.GetString()!,
                    JsonTokenType.Number => r.TryGetInt64(out long i) ? i : r.GetDouble(),
                    _ => throw new FormatException(),
                }),
            },
        };
    }

    InvalidCastException Mismatch(string column, string wanted) => new($"{Table.Name}.{column} is not {wanted}");

    public override string ToString() => $"{Table.Name}[{Index}]";
}
