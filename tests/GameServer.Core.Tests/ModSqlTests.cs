using GameServer.Storage;

namespace GameServer.Core.Tests;

/// <summary>A mod's world data (SqlUpdater.ModUpdates): its update files run with the repository's own.</summary>
public sealed class ModSqlTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"mod-sql-{Guid.NewGuid():N}");
    string Sql => Path.Combine(_root, "sql");
    string ModSql(string id) => Path.Combine(_root, "Mods", id, "sql");

    public ModSqlTests()
    {
        Write(Sql, "base/world/0001_things.sql", "CREATE TABLE things (name TEXT NOT NULL) STRICT;");
        Write(Sql, "updates/world/2026_01_01_000000_core.sql", "INSERT INTO things VALUES ('core');");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    static void Write(string root, string name, string text)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    int Update(params string[] mods)
    {
        using var db = SqlUpdater.Open(Path.Combine(_root, "world.db"));
        return SqlUpdater.Update(db, "world", Sql, _ => { }, mods.Select(ModSql));
    }

    List<string> Column(string sql)
    {
        using var db = SqlUpdater.Open(Path.Combine(_root, "world.db"));
        using var command = db.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows;
    }

    [Fact]
    public void A_mods_world_updates_run_once_after_the_repositorys()
    {
        // Dated before the repository's update, and still after it: a mod's data may count on the repository's.
        Write(ModSql("idols"), "updates/world/2025_01_01_000000_mod.sql", "INSERT INTO things SELECT 'mod after ' || name FROM things;");
        Assert.Equal(3, Update("idols"));
        Assert.Equal(["core", "mod after core"], Column("SELECT name FROM things ORDER BY rowid"));
        Assert.Equal(["UPDATE"], Column("SELECT state FROM updates WHERE name = 'mods/idols/updates/world/2025_01_01_000000_mod.sql'"));
        Assert.Equal(0, Update("idols"));
        // Off, its rows stay and nothing complains; on again, nothing runs twice.
        Assert.Equal(0, Update());
        Assert.Equal(0, Update("idols"));
        Assert.Equal(2, Column("SELECT name FROM things").Count);
        // Only the world database takes a mod's files, and only its updates/world folder.
        Assert.Empty(SqlUpdater.ModUpdates("characters", [ModSql("idols")]));
        Write(ModSql("idols"), "base/world/9999_ignored.sql", "DROP TABLE things;");
        Write(ModSql("idols"), "updates/pending_world/ignored.sql", "DROP TABLE things;");
        Write(ModSql("idols"), "updates/ruthless/2026_01_01_000000_ignored.sql", "DROP TABLE things;");
        Assert.Equal(0, Update("idols"));
        // A recorded file whose text changed stops the server, as the repository's own does, and says how to go on.
        Write(ModSql("idols"), "updates/world/2025_01_01_000000_mod.sql", "INSERT INTO things VALUES ('changed');");
        Assert.Contains("turn the mod idols off", Assert.Throws<SqlUpdateException>(() => Update("idols")).Message);
        Assert.Equal(0, Update());
    }

    [Fact]
    public void A_file_that_moves_between_the_repository_and_a_mod_does_not_run_again()
    {
        const string file = "updates/world/2026_02_01_000000_idols.sql", text = "INSERT INTO things VALUES ('idol');";
        Write(Sql, file, text);
        Assert.Equal(3, Update());
        // Into a mod: the record is renamed, the row is there once.
        File.Delete(Path.Combine(Sql, file));
        Write(ModSql("idols"), file, text);
        Assert.Equal(0, Update("idols"));
        Assert.Equal(["mods/idols/" + file], Column("SELECT name FROM updates WHERE name LIKE '%idols.sql'"));
        Assert.Single(Column("SELECT name FROM things WHERE name = 'idol'"));
        // A server that still has the file beside the mod's (an older build, or the move not yet released): the
        // repository's is the file again, and the mod's is not run beside it.
        Write(Sql, file, text);
        Assert.Equal(0, Update("idols"));
        Assert.Equal([file], Column("SELECT name FROM updates WHERE name LIKE '%idols.sql'"));
        Assert.Equal(0, Update("idols"));
        // On a new database too, and the same text in a second mod is not a second file either.
        Write(ModSql("second"), "updates/world/2026_03_01_000000_copy.sql", text);
        File.Delete(Path.Combine(_root, "world.db"));
        Assert.Equal(3, Update("idols", "second"));
        Assert.Single(Column("SELECT name FROM things WHERE name = 'idol'"));
        // The repository's file goes: the first mod's becomes the file.
        File.Delete(Path.Combine(Sql, file));
        Assert.Equal(0, Update("idols", "second"));
        Assert.Equal(["mods/idols/" + file], Column("SELECT name FROM updates WHERE name LIKE '%.sql' AND hash = (SELECT hash FROM updates WHERE name LIKE '%idols.sql')"));
        Assert.Single(Column("SELECT name FROM things WHERE name = 'idol'"));
        // That mod goes too: the second's becomes the file, still without a second run.
        Directory.Delete(Path.GetDirectoryName(ModSql("idols"))!, recursive: true);
        Assert.Equal(0, Update("second"));
        Assert.Single(Column("SELECT name FROM updates WHERE name = 'mods/second/updates/world/2026_03_01_000000_copy.sql'"));
        Assert.Single(Column("SELECT name FROM things WHERE name = 'idol'"));
        // A mod's file that fails stops the server and names the file and the way out.
        Write(ModSql("second"), "updates/world/2026_04_01_000000_broken.sql", "INSERT INTO no_such_table VALUES (1);");
        string message = Assert.Throws<SqlUpdateException>(() => Update("second")).Message;
        Assert.Contains(Path.Combine(ModSql("second"), "updates/world", "2026_04_01_000000_broken.sql"), message);
        Assert.Contains("\"second off\" in mod-state.txt", message);
    }
}
