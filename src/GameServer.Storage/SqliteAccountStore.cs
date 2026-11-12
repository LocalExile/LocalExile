using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Saves;

namespace GameServer.Storage;

/// <summary>
/// Accounts and their characters in the save database (<see cref="SaveDatabase"/>, the file the Gateway keeps character
/// saves in): one row each, an account as JSON (auth.db) and a character as the columns of account_characters. Nothing is
/// cached, so Auth and the dev host see each other's changes. Deleting a character also sets its save aside, in the same
/// transaction.
/// </summary>
public sealed class SqliteAccountStore(SaveDatabase db) : IAccountStore
{
    static readonly JsonSerializerOptions Opts = new();

    /// <summary>The character list's JSON rows (account_characters_json) as columns: the update that made the columns
    /// runs it, and so does the migration of a saves.db from before the player databases were split.</summary>
    public const string FromJson =
        "INSERT INTO account_characters (id, account_id, name, class, level, league, area_id, created_utc, last_played_utc, " +
        "last_town, portrait, portrait_frame, hardcore_death_league, hardcore_death_utc, hardcore_death_permanent) " +
        "SELECT id, account_id, name, json_extract(character, '$.Class'), json_extract(character, '$.Level'), " +
        "json_extract(character, '$.League'), json_extract(character, '$.AreaId'), json_extract(character, '$.CreatedUtc'), " +
        "json_extract(character, '$.LastPlayedUtc'), json_extract(character, '$.LastTown'), " +
        "coalesce(json_extract(character, '$.Portrait'), 0), coalesce(json_extract(character, '$.PortraitFrame'), 0), " +
        "json_extract(character, '$.HardcoreDeath.SourceLeague'), json_extract(character, '$.HardcoreDeath.AtUtc'), " +
        "json_extract(character, '$.HardcoreDeath.PermanentlyDead') FROM account_characters_json " +
        "WHERE id NOT IN (SELECT id FROM account_characters)";

    const string CharacterColumns = "id, account_id, name, class, level, league, area_id, created_utc, last_played_utc, " +
        "last_town, portrait, portrait_frame, hardcore_death_league, hardcore_death_utc, hardcore_death_permanent";

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
    public Character? FindCharacter(string name) => Characters($"SELECT {CharacterColumns} FROM account_characters WHERE name = $v", name).FirstOrDefault();
    public Character? GetCharacter(Guid id) => Characters($"SELECT {CharacterColumns} FROM account_characters WHERE id = $v", id.ToString()).FirstOrDefault();

    /// <summary>Every account, by name (the dev protocol's "accounts").</summary>
    public IReadOnlyList<Account> Accounts() => db.Read(() =>
    {
        using var c = db.Command("SELECT account FROM accounts ORDER BY name COLLATE NOCASE, id");
        using var r = c.ExecuteReader();
        var list = new List<Account>();
        while (r.Read()) list.Add(Parse<Account>(r.GetString(0), "of the account list"));
        return list;
    });

    public IReadOnlyList<Character> CharactersOf(Guid accountId) =>
        Characters($"SELECT {CharacterColumns} FROM account_characters WHERE account_id = $v ORDER BY created_utc, id", accountId.ToString());

    // A row that can't be a character (a value out of range, a date that does not parse) stops what needed it, as a save
    // does: treating it as missing would let a new character take its place.
    IReadOnlyList<Character> Characters(string sql, string value) => db.Read(() =>
    {
        using var c = db.Command(sql, ("$v", value));
        using var r = c.ExecuteReader();
        var list = new List<Character>();
        while (r.Read())
        {
            try
            {
                var death = r.IsDBNull(12) ? null
                    : new HardcoreDeathRecord(r.GetString(12), Date(r.GetString(13)), r.GetInt64(14) != 0);
                list.Add(new Character(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2),
                    (CharacterClass)r.GetInt64(3), checked((int)r.GetInt64(4)), r.GetString(5), r.GetString(6), Date(r.GetString(7)),
                    Date(r.GetString(8)), r.IsDBNull(9) ? null : r.GetString(9), checked((ushort)r.GetInt64(10)), death,
                    checked((ushort)r.GetInt64(11))));
            }
            catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException)
            {
                throw new UnreadableSaveException(db.Path, $"character {value} in {db.Path} can't be read ({e.Message})", e);
            }
        }
        return list;
    });

    static DateTimeOffset Date(string text) =>
        DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

    static string Text(DateTimeOffset date) => date.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

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
            ? "UPDATE account_characters SET account_id = $account, name = $name, class = $class, level = $level, league = $league, " +
              "area_id = $area, created_utc = $created, last_played_utc = $played, last_town = $town, portrait = $portrait, " +
              "portrait_frame = $frame, hardcore_death_league = $deathLeague, hardcore_death_utc = $deathAt, " +
              "hardcore_death_permanent = $deathPermanent WHERE id = $id"
            : $"INSERT INTO account_characters ({CharacterColumns}) VALUES ($id, $account, $name, $class, $level, $league, $area, " +
              "$created, $played, $town, $portrait, $frame, $deathLeague, $deathAt, $deathPermanent)",
            ("$id", ch.Id.ToString()), ("$account", ch.AccountId.ToString()), ("$name", ch.Name), ("$class", (long)ch.Class),
            ("$level", ch.Level), ("$league", ch.League), ("$area", ch.AreaId), ("$created", Text(ch.CreatedUtc)),
            ("$played", Text(ch.LastPlayedUtc)), ("$town", (object?)ch.LastTown ?? DBNull.Value), ("$portrait", (long)ch.Portrait),
            ("$frame", (long)ch.PortraitFrame), ("$deathLeague", (object?)ch.HardcoreDeath?.SourceLeague ?? DBNull.Value),
            ("$deathAt", ch.HardcoreDeath is { } d ? Text(d.AtUtc) : DBNull.Value),
            ("$deathPermanent", ch.HardcoreDeath is { } p ? (p.PermanentlyDead ? 1L : 0L) : DBNull.Value));
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
