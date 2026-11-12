using System.Collections;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameServer.Core;

/// <summary>
/// One of the client's tables, read from client.db (<see cref="ClientTables.Table"/>): its rows in order, so a row's
/// position is its index (<c>_row</c>), the key other tables refer to it by. Immutable, so catalogues share it.
/// The columns keep their declared SQLite types: <c>INTEGER</c>, <c>REAL</c>, <c>TEXT</c>, <c>BOOLEAN</c>, and the list
/// columns (<c>JSON</c>, flat lists of numbers or strings), read with <see cref="ClientRow.Ints"/> and its kin.
/// </summary>
public sealed class ClientTable : IReadOnlyList<ClientRow>
{
    internal enum Kind { Integer, Real, Text, Boolean, List }

    internal sealed class Column(string name, Kind kind, int rows)
    {
        public readonly string Name = name;
        public readonly Kind Kind = kind;
        public readonly long[]? Integers = kind is Kind.Integer or Kind.Boolean ? new long[rows] : null;
        public readonly double[]? Reals = kind == Kind.Real ? new double[rows] : null;
        public readonly string?[]? Texts = kind is Kind.Text or Kind.List ? new string?[rows] : null;
        public readonly BitArray Nulls = new(rows);
    }

    readonly Column[] columns;
    readonly Dictionary<string, Column> byName;

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

    /// <summary>Reads the table's rows (<c>SELECT * ... ORDER BY _row</c>).</summary>
    internal static ClientTable Read(SqliteConnection db, string name)
    {
        int count;
        using (var c = db.CreateCommand())
        {
            c.CommandText = $"SELECT count(*) FROM {Quote(name)}";
            count = Convert.ToInt32(c.ExecuteScalar());
        }
        using var q = db.CreateCommand();
        q.CommandText = $"SELECT * FROM {Quote(name)} ORDER BY \"_row\"";
        using var r = q.ExecuteReader();
        var columns = new List<Column>();
        var ordinals = new List<int>();
        for (int i = 0; i < r.FieldCount; i++)
        {
            if (r.GetName(i) == "_row") continue;
            Kind kind = r.GetDataTypeName(i).ToUpperInvariant() switch
            {
                "INTEGER" => Kind.Integer,
                "REAL" => Kind.Real,
                "TEXT" => Kind.Text,
                "BOOLEAN" => Kind.Boolean,
                _ => Kind.List,
            };
            columns.Add(new Column(r.GetName(i), kind, count));
            ordinals.Add(i);
        }
        int row = 0;
        while (r.Read() && row < count)
        {
            for (int k = 0; k < columns.Count; k++)
            {
                var column = columns[k];
                int i = ordinals[k];
                if (r.IsDBNull(i)) { column.Nulls[row] = true; continue; }
                switch (column.Kind)
                {
                    case Kind.Integer or Kind.Boolean: column.Integers![row] = r.GetInt64(i); break;
                    case Kind.Real: column.Reals![row] = r.GetDouble(i); break;
                    default: column.Texts![row] = r.GetString(i); break;
                }
            }
            row++;
        }
        return new ClientTable(name, row, [.. columns]);
    }

    /// <summary>Reads one table from the client.db at <paramref name="dbPath"/>, read-only and uncached: for tools and
    /// tests that find the database themselves. The servers use <see cref="ClientTables.Table"/>.</summary>
    public static ClientTable Load(string dbPath, string name)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        db.Open();
        return Read(db, name);
    }

    /// <summary>A child table's rows by the parent row they name in <paramref name="column"/> (a generated table's link
    /// column: Agents/docs/dev/databases.md, "Generated tables"), in table order, which is the parent's list order.</summary>
    public ILookup<long, ClientRow> By(string column) => this.Where(r => !r.IsNull(column)).ToLookup(r => r.Long(column));

    internal static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>A table made in memory, for a test: <paramref name="rows"/> hold each row's values by column name
    /// (long/int, double/float, string, bool, null, or an array of numbers or strings for a list column).</summary>
    public static ClientTable FromRows(string name, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var names = rows.SelectMany(r => r.Keys).Distinct().ToList();
        var columns = new Column[names.Count];
        for (int k = 0; k < names.Count; k++)
        {
            object? sample = rows.Select(r => r.GetValueOrDefault(names[k])).FirstOrDefault(v => v is not null);
            Kind kind = sample switch
            {
                bool => Kind.Boolean,
                double or float or decimal => Kind.Real,
                string => Kind.Text,
                System.Collections.IEnumerable => Kind.List,
                _ => Kind.Integer,
            };
            var column = columns[k] = new Column(names[k], kind, rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                object? v = rows[i].GetValueOrDefault(names[k]);
                if (v is null) { column.Nulls[i] = true; continue; }
                switch (kind)
                {
                    case Kind.Boolean: column.Integers![i] = (bool)v ? 1 : 0; break;
                    case Kind.Integer: column.Integers![i] = Convert.ToInt64(v); break;
                    case Kind.Real: column.Reals![i] = Convert.ToDouble(v); break;
                    case Kind.Text: column.Texts![i] = (string)v; break;
                    default: column.Texts![i] = JsonSerializer.Serialize(v); break;
                }
            }
        }
        return new ClientTable(name, rows.Count, columns);
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
            ClientTable.Kind.Integer or ClientTable.Kind.Boolean => c.Integers![Index],
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
            ClientTable.Kind.Integer or ClientTable.Kind.Boolean => c.Integers![Index],
            _ => throw Mismatch(column, "a number"),
        };
    }

    public double? RealOrNull(string column) => IsNull(column) ? null : Real(column);

    public bool Bool(string column)
    {
        var c = Value(column);
        return c.Kind is ClientTable.Kind.Boolean or ClientTable.Kind.Integer ? c.Integers![Index] != 0 : throw Mismatch(column, "a boolean");
    }

    public bool? BoolOrNull(string column) => IsNull(column) ? null : Bool(column);

    /// <summary>A list column's numbers (empty when null or missing).</summary>
    public int[] Ints(string column) => List(column, "a list of numbers", static (ref Utf8JsonReader r) =>
        r.TokenType != JsonTokenType.Number ? throw new FormatException() : r.TryGetInt32(out int i) ? i : checked((int)r.GetDouble()));

    public long[] Longs(string column) => List(column, "a list of numbers", static (ref Utf8JsonReader r) =>
        r.TokenType != JsonTokenType.Number ? throw new FormatException() : r.TryGetInt64(out long i) ? i : (long)r.GetDouble());

    public double[] Reals(string column) => List(column, "a list of numbers", static (ref Utf8JsonReader r) =>
        r.TokenType != JsonTokenType.Number ? throw new FormatException() : r.GetDouble());

    /// <summary>A list column's strings (empty when null or missing).</summary>
    public string[] Texts(string column) => List(column, "a list of strings", static (ref Utf8JsonReader r) =>
        r.TokenType != JsonTokenType.String ? throw new FormatException() : r.GetString()!);

    delegate T Item<T>(ref Utf8JsonReader reader);

    T[] List<T>(string column, string wanted, Item<T> item)
    {
        if (Table.Find(column) is not { } c || c.Nulls[Index]) return [];
        if (c.Kind != ClientTable.Kind.List) throw Mismatch(column, "a list");
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(c.Texts![Index]!));
        var values = new List<T>();
        try
        {
            reader.Read();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) values.Add(item(ref reader));
        }
        catch (FormatException) { throw Mismatch(column, wanted); }
        return [.. values];
    }

    /// <summary>The value as stored: long, double, string, bool, an array for a list column, or null.</summary>
    public object? Get(string column)
    {
        if (IsNull(column)) return null;
        var c = Table.Get(column);
        return c.Kind switch
        {
            ClientTable.Kind.Integer => c.Integers![Index],
            ClientTable.Kind.Boolean => c.Integers![Index] != 0,
            ClientTable.Kind.Real => c.Reals![Index],
            ClientTable.Kind.Text => c.Texts![Index],
            _ => List<object>(column, "a list of numbers or strings", static (ref Utf8JsonReader r) => r.TokenType switch
            {
                JsonTokenType.String => r.GetString()!,
                JsonTokenType.Number => r.TryGetInt64(out long i) ? i : r.GetDouble(),
                _ => throw new FormatException(),
            }),
        };
    }

    InvalidCastException Mismatch(string column, string wanted) => new($"{Table.Name}.{column} is not {wanted}");

    public override string ToString() => $"{Table.Name}[{Index}]";
}
