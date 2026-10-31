using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Saves;

namespace GameServer.Storage;

/// <summary>
/// Accounts and their characters in the save database (<see cref="SaveDatabase"/>, the file the Gateway keeps character
/// saves in): one row each, the record as JSON. Nothing is cached, so Auth and the dev host see each other's changes.
/// Deleting a character also sets its save aside, in the same transaction.
/// </summary>
public sealed class SqliteAccountStore(SaveDatabase db) : IAccountStore
{
    static readonly JsonSerializerOptions Opts = new();

    /// <summary>The account store of Auth and the dev host: the save database (<see cref="SaveDatabase.DefaultPath"/>), with
    /// an account store from before it (data/accounts.json, <c>Game_DEV_DB</c>) imported once.</summary>
    public static SqliteAccountStore Open()
    {
        var db = new SaveDatabase(SaveDatabase.DefaultPath);
        ImportJson(db, Environment.GetEnvironmentVariable("Game_DEV_DB") ?? Path.Combine("data", "accounts.json"));
        return new SqliteAccountStore(db);
    }

    public Account? FindAccount(string name) => One<Account>("SELECT account FROM accounts WHERE name = $v", name);
    public Account? GetAccount(Guid id) => One<Account>("SELECT account FROM accounts WHERE id = $v", id.ToString());
    public Character? FindCharacter(string name) => One<Character>("SELECT character FROM account_characters WHERE name = $v", name);
    public Character? GetCharacter(Guid id) => One<Character>("SELECT character FROM account_characters WHERE id = $v", id.ToString());

    /// <summary>Every account, by name (the dev protocol's "accounts").</summary>
    public IReadOnlyList<Account> Accounts() => db.Read(() =>
    {
        using var c = db.Command("SELECT account FROM accounts ORDER BY name COLLATE NOCASE, id");
        using var r = c.ExecuteReader();
        var list = new List<Account>();
        while (r.Read()) list.Add(Parse<Account>(r.GetString(0), "of the account list"));
        return list;
    });

    public IReadOnlyList<Character> CharactersOf(Guid accountId) => db.Read(() =>
    {
        using var c = db.Command("SELECT character FROM account_characters WHERE account_id = $v ORDER BY created_utc, id", ("$v", accountId.ToString()));
        using var r = c.ExecuteReader();
        var list = new List<Character>();
        while (r.Read()) list.Add(Parse<Character>(r.GetString(0), "a character of an account"));
        return list;
    });

    public void AddAccount(Account a) => Upsert(a);
    public void UpdateAccount(Account a) => Upsert(a, onlyIfThere: true);
    public void AddCharacter(Character c) => Upsert(c);
    public void UpdateCharacter(Character c) => Upsert(c, onlyIfThere: true);

    /// <summary>Removes the character and sets its save aside (<see cref="SaveDatabase.SetAside"/>), together: a crash can't
    /// leave a deleted character's save for a new one of its name, nor a character without its save.</summary>
    public void DeleteCharacter(Guid id) => db.Atomically(() =>
    {
        if (GetCharacter(id) is not { } character) return;
        using var c = db.Command("DELETE FROM account_characters WHERE id = $v", ("$v", id.ToString()));
        c.ExecuteNonQuery();
        db.SetAside(character.Name);
    });

    void Upsert(Account a, bool onlyIfThere = false) => db.Atomically(() =>
    {
        using var c = db.Command(onlyIfThere
            ? "UPDATE accounts SET name = $name, account = $json WHERE id = $id"
            : "INSERT INTO accounts (id, name, account) VALUES ($id, $name, $json)",
            ("$id", a.Id.ToString()), ("$name", a.Name), ("$json", JsonSerializer.Serialize(a, Opts)));
        c.ExecuteNonQuery();
    });

    void Upsert(Character ch, bool onlyIfThere = false) => db.Atomically(() =>
    {
        if (onlyIfThere && GetCharacter(ch.Id) is { } current)
            ch = HardcoreDeathRules.PreserveCommittedDeath(ch, current);
        using var c = db.Command(onlyIfThere
            ? "UPDATE account_characters SET name = $name, account_id = $account, character = $json WHERE id = $id"
            : "INSERT INTO account_characters (id, account_id, name, created_utc, character) VALUES ($id, $account, $name, $created, $json)",
            ("$id", ch.Id.ToString()), ("$account", ch.AccountId.ToString()), ("$name", ch.Name),
            ("$created", ch.CreatedUtc.UtcDateTime.ToString("O")), ("$json", JsonSerializer.Serialize(ch, Opts)));
        c.ExecuteNonQuery();
    });

    T? One<T>(string sql, string value) where T : class => db.Read(() =>
    {
        using var c = db.Command(sql, ("$v", value));
        return c.ExecuteScalar() is string json ? Parse<T>(json, value) : null;
    });

    // A row that isn't a record stops what needed it, as a save does: treating it as missing would let a new account or
    // character take its place.
    T Parse<T>(string json, string what) where T : class
    {
        T? value;
        try { value = JsonSerializer.Deserialize<T>(json, Opts); }
        catch (JsonException e) { throw new UnreadableSaveException(db.Path, $"{typeof(T).Name} {what} in {db.Path} can't be read ({e.Message})", e); }
        return value ?? throw new UnreadableSaveException(db.Path, $"{typeof(T).Name} {what} in {db.Path} can't be read (null)", null);
    }

    /// <summary>
    /// The accounts and characters of an account store from before the database (data/accounts.json) go into it once, in
    /// one transaction, and the file is renamed to *.imported (kept, never read again). A file that can't be read stops
    /// the import (<see cref="UnreadableSaveException"/>): its accounts would otherwise be lost. Returns how many accounts
    /// and characters were imported.
    /// </summary>
    public static int ImportJson(SaveDatabase db, string path)
    {
        const string Done = "accounts_imported";
        if (db.GetMeta(Done) is not null) return 0;
        var store = new SqliteAccountStore(db);
        int imported = 0;
        bool exists = File.Exists(path);
        db.Atomically(() =>
        {
            if (exists)
            {
                var (accounts, characters) = new JsonAccountStore(path).All();
                foreach (var a in accounts.Where(a => store.GetAccount(a.Id) is null)) { store.AddAccount(a); imported++; }
                foreach (var c in characters.Where(c => store.GetCharacter(c.Id) is null)) { store.AddCharacter(c); imported++; }
            }
            db.SetMeta(Done, DateTime.UtcNow.ToString("O"));
        });
        if (exists)
        {
            try { File.Move(path, path + ".imported"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"[saves] {path} imported but not renamed: {e.Message}"); }
            Console.WriteLine($"[saves] {imported} account(s) and character(s) imported from {path} into {db.Path}; the file is kept as {path}.imported");
        }
        return imported;
    }
}
