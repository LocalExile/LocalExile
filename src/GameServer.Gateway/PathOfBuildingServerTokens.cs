using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Storage;
using Microsoft.Data.Sqlite;

/// <summary>Account-bound tokens. Only SHA-256 digests are stored; authentication opens SQLite read-only.</summary>
static class PathOfBuildingServerTokens
{
    const string Prefix = "pob_import_token/";

    public static string Issue(string database, string accountName, bool revoke = false)
    {
        RequireDatabase(database);
        using var db = new SaveDatabase(database);
        return db.Atomically(() =>
        {
            var account = new SqliteAccountStore(db).FindAccount(accountName)
                ?? throw new ArgumentException("The local account does not exist. Create the account before issuing its PoB token.");
            string token = revoke ? "" : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            db.SetMeta(Prefix + account.Id.ToString("N"), revoke ? "" : Hash(token));
            return token;
        });
    }

    public static Account? Authenticate(string database, string accountName, string? authorization)
    {
        if (accountName.Length is < 3 or > 32 || authorization is null
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        string token = authorization[7..];
        if (token.Length != 64 || !token.All(char.IsAsciiHexDigit)) return null;
        using var connection = OpenReadOnly(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.account, m.value FROM accounts a " +
            "JOIN meta m ON m.key = $prefix || replace(a.id, '-', '') WHERE a.name = $name";
        command.Parameters.AddWithValue("$prefix", Prefix);
        command.Parameters.AddWithValue("$name", accountName);
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        string digest = row.GetString(1);
        if (digest.Length != 64 || !digest.All(char.IsAsciiHexDigit)
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest), Convert.FromHexString(Hash(token)))) return null;
        return JsonSerializer.Deserialize<Account>(row.GetString(0))
            ?? throw new InvalidDataException("The authenticated account record is unreadable.");
    }

    public static SqliteConnection OpenReadOnly(string database)
    {
        RequireDatabase(database);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    static void RequireDatabase(string database)
    {
        if (!File.Exists(database)) throw new FileNotFoundException("The configured save database does not exist. Start the normal local server first.");
    }
}
