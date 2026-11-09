using Microsoft.Data.Sqlite;

namespace GameServer.Storage;

/// <summary>
/// Moves the one database from before the split (data/saves.db) into the player databases: accounts into auth.db, the
/// characters, saves, stashes and friends into characters.db (Agents/docs/dev/databases.md). The first server to open the
/// default player databases runs it (<see cref="MigrateIfNeeded"/>), as do <c>GameServer.Auth migrate-saves</c> and
/// <c>LocalExile migrate-saves</c>, so an install updated by hand or by a launcher keeps its saves. A copy of saves.db
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

    /// <summary>Migrates data/saves.db before a server opens the default player databases, so it does not start with no
    /// accounts and no characters. Several servers start at once: the first takes data/saves.db.migrating and migrates,
    /// the others wait for it and then find saves.db gone. A saves.db beside existing player databases stops the server
    /// instead: merging them is the owner's call.</summary>
    public static void MigrateIfNeeded(string charactersPath, Action<string>? log = null)
    {
        if (!File.Exists(SaveDatabase.LegacyPath)) return;
        if (!string.Equals(Path.GetFullPath(charactersPath), Path.GetFullPath(Characters), StringComparison.OrdinalIgnoreCase)) return;
        log ??= Console.WriteLine;
        using var claim = Claim(SaveDatabase.LegacyPath + ".migrating");
        if (!File.Exists(SaveDatabase.LegacyPath)) return;
        if (File.Exists(Characters) || File.Exists(Auth))
            throw new SqlUpdateException($"{SaveDatabase.LegacyPath} is from before the player databases were split, and " +
                $"{Characters} or {Auth} is already there. Move the new ones away to migrate " +
                "saves.db at the next start, or move saves.db away to keep them", null);
        log($"[migrate] {SaveDatabase.LegacyPath} is from before the player databases were split: migrating it");
        Migrate(SaveDatabase.LegacyPath, Characters, Auth, log);
    }

    static readonly string Characters = Path.Combine("data", "characters.db"), Auth = Path.Combine("data", "auth.db");

    // An exclusive handle on the claim file, waited for while another server migrates (a large saves.db takes seconds).
    static FileStream Claim(string path)
    {
        for (var until = DateTime.UtcNow.AddMinutes(5); ; Thread.Sleep(200))
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < until) { }
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
