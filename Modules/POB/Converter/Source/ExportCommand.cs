using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Core.Imports;
using Microsoft.Data.Sqlite;


static class ExportCommand
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal sealed record Request(string Operation, string Database, string? DataFolder = null, string? Account = null,
        string? Name = null, string League = "Standard", string? Code = null, string? PreviewKey = null, string? Item = null);
    internal sealed record Destination(string Id, string Name);
    public static int Run(TextReader input, TextWriter output)
    {
        try
        {
            var text = new StringBuilder(); var buffer = new char[8192]; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (text.Length + count > 12 * 1024 * 1024) throw new ArgumentException("Export request exceeds 12 MiB.");
                text.Append(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(text.ToString(), new JsonDocumentOptions { MaxDepth = 12 });
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!keys.Add(property.Name)) throw new ArgumentException("Duplicate request fields are not accepted.");
            var request = document.RootElement.Deserialize<Request>(Json) ?? throw new ArgumentException("Empty export request.");
            var response = Execute(request);
            output.WriteLine(JsonSerializer.Serialize(response, Json));
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or JsonException
            or SqliteException or InvalidOperationException or FormatException or OverflowException)
        {
            string error = e is SqliteException sql ? sql.SqliteErrorCode is 5 or 6
                ? "The save database is busy. Wait for the server's write to finish, then try again."
                : "SQLite refused the export. No partial character was committed; check the selected database and its permissions."
                : e.Message;
            output.WriteLine(JsonSerializer.Serialize(new { ok = false, error = error[..Math.Min(error.Length, 2048)] }, Json));
            return 1;
        }
    }

    static object Execute(Request request)
    {
        if (request.Operation is not ("inspect" or "preview" or "export" or "item-preview" or "item-send"))
            throw new ArgumentException("Choose inspect, preview, export, item-preview or item-send.");
        string database = ExistingLocalFile(request.Database);
        using var connection = Open(database, write: false);
        ValidateSchema(connection);
        var accounts = Accounts(connection);
        string? data = ResolveData(database, request.DataFolder);
        var version = Compatibility.Identify(data);
        if (request.Operation == "inspect")
            return new { ok = true, database, dataFolder = data, accounts = accounts.Select(a => a.Name),
                characters = ItemSend.Characters(connection, accounts, request.Account),
                leagues = Leagues(connection), serverVersion = version, next = "Select an existing account, a new name and the game's league; then Preview." };
        if (data is null) throw new ArgumentException("Select the LocalExile data folder containing tables and item-types.json.");
        if (request.Operation.StartsWith("item-", StringComparison.Ordinal))
            return ItemSend.Execute(request, database, data, connection, accounts, version);
        if (request.Account is null || request.Name is null || !ValidName(request.Name))
            throw new ArgumentException("Select an existing account and a new name: 3-23 ASCII letters, digits, underscores or hyphens.");
        if (string.IsNullOrWhiteSpace(request.League) || request.League.Length > 128 || request.League.Any(char.IsControl))
            throw new ArgumentException("Enter the exact league name offered by your game server.");
        if (request.League.Contains("Ruthless", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ruthless requires a separate native format/rules adapter and is not supported by this exporter.");
        var account = accounts.SingleOrDefault(a => a.Name.Equals(request.Account, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("The selected account does not exist in this database.");
        CheckDestination(connection, account.Id, request.Name, database, data);
        var build = PathOfBuilding.Parse(request.Code ?? throw new ArgumentException("No build was supplied."));
        var progress = PathOfBuildingCharacters.Prepare(build, data, () => NativeGraph.Load(data));
        var catalog = new ItemCatalog(data);
        var equipment = PathOfBuildingItems.Materialize(build, catalog, data, (definition, type, item, own) =>
            ItemSend.Definition(connection, ItemSend.Title(definition.Text), type, item) is { } unique ? ItemSend.Completed(item, type, unique, catalog.Mods, own) : item);
        string save = JsonSerializer.Serialize(progress.ToSave(equipment));
        var compatibility = Compatibility.Check(build, progress, equipment, catalog, data, version);
        var allWarnings = progress.Warnings.Concat(equipment.Warnings).Concat(compatibility.Warnings).Distinct()
            .OrderBy(w => w.StartsWith("Skipped item ", StringComparison.Ordinal) || w.StartsWith("Skipped skill group", StringComparison.Ordinal)
                || w.StartsWith("Skipped gem ", StringComparison.Ordinal) ? 0 : 1).ToArray();
        int items = equipment.Inventories.Sum(i => i.Value.Count);
        int gems = equipment.Inventories.SelectMany(i => i.Value).Sum(i => i.Item.Sockets?.Count(s => s.Gem is not null) ?? 0);
        string previewKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            database, data, account.Id, request.Name, request.League, request.Code, save, version }))));
        object Reply(string? backup) => new { ok = true, preview = request.Operation == "preview", previewKey,
            character = request.Name, @class = build.Class.ToString(), level = build.Level, league = request.League,
            items, gems, passives = progress.Passives.Count, masteries = progress.MasteryEffects.Count, tattoos = progress.Tattoos?.Count ?? 0,
            skippedItems = Math.Max(0, build.Slots.Count(s => s.ItemId != 0) - items),
            skippedGems = Math.Max(0, build.Skills.Where(s => s.Enabled).Sum(s => s.Gems.Count(g => g.Enabled)) - gems),
            warnings = allWarnings.Take(45).Select(w => w[..Math.Min(w.Length, 240)]), warningCount = allWarnings.Length,
            warningsTruncated = allWarnings.Length > 45 || allWarnings.Any(w => w.Length > 240),
            compatibility = compatibility.Findings.Take(60), compatibilityCount = compatibility.Findings.Count,
            serverVersion = version, backup, compatibilityReport = backup is null ? null : backup + ".export.json", next = request.Operation == "preview"
                ? "Review warnings. Valid items, gems and passives are kept even when their effects are unimplemented or unknown."
                : "Return to the character screen to refresh the list. If needed, log out and log in again. The character starts in Lioneye's Watch." };
        if (request.Operation == "preview") return Reply(null);
        if (request.PreviewKey != previewKey) throw new ArgumentException("The build, destination or prepared save changed. Preview again before exporting.");
        // Serialize everything needed by the UI before any write, including warning bounds.
        if (JsonSerializer.SerializeToUtf8Bytes(Reply(null), Json).Length > 96 * 1024)
            throw new ArgumentException("The export summary is too large to display safely.");
        using var writer = Open(database, write: true);
        ValidateSchema(writer);
        string backup = Backup(writer, database);
        // Full details stay beside the backup; the UI receives a bounded summary. This is prepared before commit,
        // so a failed disk write cannot be mistaken for a failed export after a successful database transaction.
        using (var report = new FileStream(backup + ".export.json", FileMode.CreateNew, FileAccess.Write))
            JsonSerializer.Serialize(report, new { status = "prepared export; database transaction determines creation",
                character = request.Name, account = account.Name, league = request.League, serverVersion = version,
                items, gems, passives = progress.Passives.Count, masteries = progress.MasteryEffects.Count, tattoos = progress.Tattoos?.Count ?? 0,
                warnings = allWarnings, compatibility = compatibility.Findings }, Json);
        using var transaction = writer.BeginTransaction(deferred: false);
        CheckDestination(writer, account.Id, request.Name, database, data, transaction);
        if (Scalar(writer, "SELECT id FROM accounts WHERE id=$value AND name=$name COLLATE NOCASE", account.Id,
            transaction, request.Account) as string != account.Id) throw new ArgumentException("The account changed. Inspect the database and preview again.");
        string id = Guid.NewGuid().ToString(), stamp = DateTimeOffset.UtcNow.ToString("O");
        var character = new { Id = id, AccountId = account.Id, Name = request.Name, Class = (int)build.Class, Level = build.Level,
            League = request.League, AreaId = "1_1_town", CreatedUtc = stamp, LastPlayedUtc = stamp, LastTown = "1_1_town", Portrait = 0 };
        using (var command = writer.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO account_characters(id,account_id,name,created_utc,character) VALUES($id,$account,$name,$stamp,$json)";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$account", account.Id);
            command.Parameters.AddWithValue("$name", request.Name); command.Parameters.AddWithValue("$stamp", stamp);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(character)); command.ExecuteNonQuery();
        }
        using (var command = writer.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "INSERT INTO characters(name,save) VALUES($name,$save)";
            command.Parameters.AddWithValue("$name", request.Name); command.Parameters.AddWithValue("$save", save); command.ExecuteNonQuery();
        }
        transaction.Commit();
        return Reply(backup);
    }

    internal static string ExistingLocalFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("Choose an existing SQLite database on a local drive.");
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new ArgumentException("The selected database does not exist; the exporter never creates one.");
        if (new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network) throw new ArgumentException("Use a local database, not a network drive.");
        return full;
    }
    internal static SqliteConnection Open(string database, bool write)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database,
            Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 10 }.ToString());
        connection.Open();
        if (write) { using var c = connection.CreateCommand(); c.CommandText = "PRAGMA synchronous=FULL"; c.ExecuteNonQuery(); }
        return connection;
    }
    internal static void ValidateSchema(SqliteConnection connection)
    {
        foreach (var (table, required) in new[] { ("accounts", new[] { "id", "name", "account" }),
            ("account_characters", new[] { "id", "account_id", "name", "created_utc", "character" }), ("characters", new[] { "name", "save" }) })
        {
            using var c = connection.CreateCommand(); c.CommandText = $"PRAGMA table_info({table})";
            using var rows = c.ExecuteReader(); var names = new HashSet<string>(); while (rows.Read()) names.Add(rows.GetString(1));
            if (required.Any(n => !names.Contains(n))) throw new ArgumentException("This is not a compatible LocalExile save database. No schema changes were made.");
        }
    }
    static List<Destination> Accounts(SqliteConnection connection)
    {
        using var c = connection.CreateCommand(); c.CommandText = "SELECT id,name FROM accounts ORDER BY name";
        using var rows = c.ExecuteReader(); var result = new List<Destination>();
        while (rows.Read()) { if (result.Count == 1024) throw new ArgumentException("Too many accounts to inspect."); result.Add(new(rows.GetString(0), rows.GetString(1))); }
        return result;
    }
    static string[] Leagues(SqliteConnection connection)
    {
        using var c = connection.CreateCommand(); c.CommandText = "SELECT character FROM account_characters LIMIT 10000";
        using var rows = c.ExecuteReader(); var leagues = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Standard" };
        while (rows.Read()) { using var d = JsonDocument.Parse(rows.GetString(0)); if (d.RootElement.TryGetProperty("League", out var league) && league.GetString() is { } name) leagues.Add(name); }
        return leagues.Order().ToArray();
    }
    static string? ResolveData(string database, string? supplied)
    {
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            string folder = Path.GetFullPath(supplied);
            if (!Directory.Exists(Path.Combine(folder, "tables")) || !File.Exists(Path.Combine(folder, "item-types.json")))
                throw new ArgumentException("The data folder must contain tables and item-types.json from the normal LocalExile setup.");
            return folder;
        }
        string adjacent = Path.GetDirectoryName(database)!;
        foreach (string folder in new[] { adjacent, Path.Combine(adjacent, "data") })
            if (Directory.Exists(Path.Combine(folder, "tables")) && File.Exists(Path.Combine(folder, "item-types.json"))) return folder;
        return null;
    }
    static bool ValidName(string name) => name.Length is >= 3 and <= 23 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    static object? Scalar(SqliteConnection connection, string sql, string value, SqliteTransaction? transaction = null, string? name = null)
    {
        using var c = connection.CreateCommand(); c.Transaction = transaction; c.CommandText = sql;
        c.Parameters.AddWithValue("$value", value); if (name is not null) c.Parameters.AddWithValue("$name", name);
        return c.ExecuteScalar();
    }
    static void CheckDestination(SqliteConnection connection, string account, string name, string database, string data, SqliteTransaction? transaction = null)
    {
        if (Convert.ToInt32(Scalar(connection, "SELECT count(*) FROM account_characters WHERE account_id=$value", account, transaction)) >= 24)
            throw new ArgumentException("This account has reached the 24-character limit.");
        if (Scalar(connection, "SELECT 1 FROM account_characters WHERE name=$value COLLATE NOCASE UNION ALL SELECT 1 FROM characters WHERE name=$value COLLATE NOCASE LIMIT 1", name, transaction) is not null)
            throw new ArgumentException("That character name or save already exists. Existing characters are never replaced.");
        foreach (string folder in new[] { Path.Combine(data, "saves"), Path.Combine(Path.GetDirectoryName(database)!, "saves") }.Distinct())
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.json").Any(f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A legacy save already uses that name. Let LocalExile migrate it before exporting.");
    }
    static string Backup(SqliteConnection source, string database)
    {
        string folder = Path.Combine(Path.GetDirectoryName(database)!, "evryBuild-backups"); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(database)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        using (File.Open(path, FileMode.CreateNew, FileAccess.Write)) { }
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        destination.Open(); source.BackupDatabase(destination);
        return path;
    }
}
