using Microsoft.Data.Sqlite;

namespace GameServer.Storage;

/// <summary>
/// Moves the one database from before the split (data/saves.db) into the player databases: accounts into auth.db, the
/// characters, saves, stashes and friends into characters.db (Agents/docs/dev/databases.md). It runs only when asked
/// (<c>GameServer.Auth migrate-saves</c>, <c>LocalExile migrate-saves</c>), never at a server start. A copy of saves.db
/// is made first; the game-data tables of saves.db (settings, catalogue, weights) are left behind, since world.db and
/// client.db are built from sql/. saves.db is renamed to saves.db.migrated at the end.
/// </summary>
public static class SavesMigration
{
    // The player tables of saves.db and their columns; the rest of saves.db was game data.
    static readonly (string Table, string Columns, string Database)[] Tables =
    [
        ("accounts", "id, name, account", "auth"),
        ("characters", "name, save", "main"),
        ("stashes", "account, save", "main"),
        ("stash_tabs", "account, tab, items", "main"),
        ("deleted_characters", "name, deleted_utc, save", "main"),
        ("meta", "key, value", "main"),
        ("account_characters", "id, account_id, name, created_utc, character", "main"),
        ("account_friends", "owner, friend", "main"),
    ];

    /// <summary>Stops a server that would open the default player databases while data/saves.db is not migrated: it
    /// would start with no accounts and no characters.</summary>
    public static void RefuseUnmigrated(string charactersPath)
    {
        if (!File.Exists(SaveDatabase.LegacyPath)) return;
        if (!string.Equals(Path.GetFullPath(charactersPath), Path.GetFullPath(Path.Combine("data", "characters.db")), StringComparison.OrdinalIgnoreCase)) return;
        throw new SqlUpdateException($"{SaveDatabase.LegacyPath} is from before the player databases were split. Stop the servers and run " +
            "`dotnet run --project src/GameServer.Auth -- migrate-saves` (a release: `LocalExile migrate-saves`); it makes a copy first", null);
    }

    public sealed record Result(string Backup, IReadOnlyDictionary<string, int> Rows);

    public static Result Migrate(string saves, string charactersPath, string authPath, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        if (!File.Exists(saves)) throw new FileNotFoundException($"{saves} is not there: nothing to migrate", saves);
        foreach (string target in new[] { charactersPath, authPath })
            if (File.Exists(target)) throw new IOException($"{target} is already there: the migration only fills new databases. Move it away first");

        string backup = $"{saves}.backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        using (var old = SqlUpdater.Open(saves))
        using (var copy = old.CreateCommand())
        {
            copy.CommandText = "VACUUM INTO $path";
            copy.Parameters.AddWithValue("$path", backup);
            copy.ExecuteNonQuery();
        }
        log($"[migrate] copy of {saves}: {backup}");

        SqlUpdater.Update(authPath, "auth", log: log);
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var db = SqlUpdater.Open(charactersPath))
        {
            SqlUpdater.Update(db, "characters", log: log);
            Attach(db, saves, "old");
            Attach(db, authPath, "auth");
            using var tx = db.BeginTransaction();
            foreach (var (table, columns, database) in Tables)
            {
                if (!Exists(db, tx, table)) { rows[table] = 0; continue; }
                using var c = db.CreateCommand();
                c.Transaction = tx;
                c.CommandText = $"INSERT INTO {database}.{table} ({columns}) SELECT {columns} FROM old.{table}";
                rows[table] = c.ExecuteNonQuery();
            }
            tx.Commit();
            using var detach = db.CreateCommand();
            detach.CommandText = "DETACH DATABASE old";
            detach.ExecuteNonQuery();
        }
        foreach (var (table, count) in rows) log($"[migrate] {table}: {count} row(s)");

        SqliteConnection.ClearAllPools();
        using (var old = SqlUpdater.Open(saves)) SqlUpdater.Execute(old, "PRAGMA wal_checkpoint(TRUNCATE)");
        File.Move(saves, saves + ".migrated");
        foreach (string suffix in new[] { "-wal", "-shm" })
            if (File.Exists(saves + suffix)) File.Delete(saves + suffix);
        log($"[migrate] {saves} is now {saves}.migrated; accounts are in {authPath}, characters in {charactersPath}");
        return new Result(backup, rows);
    }

    static void Attach(SqliteConnection db, string path, string name)
    {
        using var c = db.CreateCommand();
        c.CommandText = $"ATTACH DATABASE $path AS {name}";
        c.Parameters.AddWithValue("$path", path);
        c.ExecuteNonQuery();
    }

    static bool Exists(SqliteConnection db, SqliteTransaction tx, string table)
    {
        using var c = db.CreateCommand();
        c.Transaction = tx;
        c.CommandText = "SELECT 1 FROM old.sqlite_master WHERE type = 'table' AND name = $t";
        c.Parameters.AddWithValue("$t", table);
        return c.ExecuteScalar() is not null;
    }
}
