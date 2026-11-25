using System.Text.Json;
using GameServer.Core.Saves;
using GameServer.Storage;

/// <summary>
/// The Gateway's player databases (data/characters.db, <c>Game_CHARACTERS_DB</c>, with data/auth.db; <see cref="SaveDatabase"/>): characters
/// (<see cref="CharacterSaves"/>) and account stashes (<see cref="AccountStash"/>), as rows of the save tables (<see cref="SaveRows"/>).
/// <para>Every change is saved at once, in a transaction. A change that touches several saves or several parts of one
/// (an item from the stash to the cursor, a purchase, a currency item used on an item) runs in one
/// <see cref="Atomically{T}"/>: all of it is saved or none. When a save fails, the objects it changed in memory are
/// dropped (<see cref="SaveDatabase.OnRollback"/>) and loaded again from the database on their next use, and the
/// exception closes the connection: memory is never ahead of the disk.</para>
/// <para>Lock order: a connection's own locks (<c>conn.Monsters</c>, <c>conn.Npcs</c>) before the database's, and the
/// database's before an inventory's or a stash's. So the work inside a transaction never takes a connection's lock.</para>
/// </summary>
public static class Saves
{
    static SaveDatabase? _db;
    // Active area sessions keep their character save and stash in memory (ref-counted per key, guarded by Db's lock)
    // and flush dirty entries atomically to SQLite only when leaving the area, exiting, disconnecting, or shutting down.
    static readonly Dictionary<string, int> _pinnedCharacters = new(StringComparer.Ordinal);
    static readonly Dictionary<string, (CharacterSaves.Save? Save, bool Dirty)> _characters = new(StringComparer.Ordinal);
    static readonly Dictionary<string, int> _pinnedStashes = new(StringComparer.Ordinal);
    static readonly Dictionary<string, (AccountStash.Save? Header, bool Dirty)> _stashes = new(StringComparer.Ordinal);
    static readonly Dictionary<(string Account, int Tab), (List<CharacterSaves.SavedPlacement>? Items, bool Dirty)> _stashTabs = [];

    public static bool IsOpen => _db is not null;

    public static SaveDatabase Db => _db ?? throw new InvalidOperationException("the save database is not open (Saves.Open)");

    public static string DefaultPath => SaveDatabase.DefaultPath;

    public static void Open(string path)
    {
        var old = _db;
        _pinnedCharacters.Clear();
        _characters.Clear();
        _pinnedStashes.Clear();
        _stashes.Clear();
        _stashTabs.Clear();
        _db = new SaveDatabase(path);
        old?.Dispose();
    }

    static IEnumerable<string> StashKeys(string character, string? account, string? league)
    {
        string baseKey = string.IsNullOrEmpty(account) ? character : account;
        string leagueKey = AccountStash.Key(baseKey, league);
        yield return baseKey;
        if (leagueKey != baseKey) yield return leagueKey;
        yield return "guild:" + leagueKey;
        yield return "trade:" + leagueKey;
    }

    /// <summary>Keeps <paramref name="character"/> and its account stash in memory while in an area so gameplay makes no
    /// runtime SQLite reads or writes. Paired with <see cref="UnpinAndFlushSession"/> on exit/disconnect.</summary>
    public static void PinSession(string character, string? account, string? league)
    {
        if (string.IsNullOrEmpty(character) || _db is null) return;
        Db.Read(() =>
        {
            _pinnedCharacters[character] = _pinnedCharacters.GetValueOrDefault(character) + 1;
            foreach (string key in StashKeys(character, account, league))
                _pinnedStashes[key] = _pinnedStashes.GetValueOrDefault(key) + 1;
            return 0;
        });
    }

    /// <summary>Flushes dirty in-memory saves for <paramref name="character"/> and its account stash to SQLite in one
    /// atomic transaction (called when leaving an area, exiting to character select / log in, or disconnecting).</summary>
    public static void FlushSession(string character, string? account, string? league)
    {
        if (string.IsNullOrEmpty(character) || _db is null) return;
        var db = Db;
        db.Atomically(() =>
        {
            FlushCharacterLocked(db, character);
            foreach (string key in StashKeys(character, account, league))
                FlushStashLocked(db, key);
        });
    }

    /// <summary>Flushes dirty saves for the session and releases its in-memory pin when no other connection holds it.</summary>
    public static void UnpinAndFlushSession(string character, string? account, string? league)
    {
        if (string.IsNullOrEmpty(character) || _db is null) return;
        try
        {
            FlushSession(character, account, league);
        }
        finally
        {
            Db.Read(() =>
            {
                if (_pinnedCharacters.TryGetValue(character, out int c))
                {
                    if (c <= 1)
                    {
                        _pinnedCharacters.Remove(character);
                        if (_characters.TryGetValue(character, out var entry) && !entry.Dirty) _characters.Remove(character);
                    }
                    else _pinnedCharacters[character] = c - 1;
                }
                foreach (string key in StashKeys(character, account, league))
                {
                    if (!_pinnedStashes.TryGetValue(key, out int s)) continue;
                    if (s <= 1)
                    {
                        _pinnedStashes.Remove(key);
                        if (_stashes.TryGetValue(key, out var st) && !st.Dirty) _stashes.Remove(key);
                        foreach (var tabKey in _stashTabs.Keys.Where(k => k.Account == key).ToList())
                            if (!_stashTabs[tabKey].Dirty) _stashTabs.Remove(tabKey);
                    }
                    else _pinnedStashes[key] = s - 1;
                }
                return 0;
            });
        }
    }

    /// <summary>Flushes every dirty in-memory character and stash save to SQLite (Gateway shutdown).</summary>
    public static void FlushAll()
    {
        if (_db is null) return;
        var db = Db;
        db.Atomically(() =>
        {
            foreach (string character in _characters.Keys.ToList()) FlushCharacterLocked(db, character);
            var stashKeys = _stashes.Keys.Concat(_stashTabs.Keys.Select(k => k.Account)).Distinct().ToList();
            foreach (string key in stashKeys) FlushStashLocked(db, key);
        });
    }

    static void FlushCharacterLocked(SaveDatabase db, string character)
    {
        if (!_characters.TryGetValue(character, out var entry) || !entry.Dirty || entry.Save is null) return;
        SaveRows.WriteCharacter(db, character, entry.Save);
        db.OnRollback(() => _characters[character] = entry);
        _characters[character] = (entry.Save, false);
    }

    static void FlushStashLocked(SaveDatabase db, string account)
    {
        if (_stashes.TryGetValue(account, out var header) && header.Dirty && header.Header is not null)
        {
            SaveRows.WriteStash(db, account, header.Header);
            db.OnRollback(() => _stashes[account] = header);
            _stashes[account] = (header.Header, false);
        }
        foreach (var tabKey in _stashTabs.Keys.Where(k => k.Account == account).OrderBy(k => k.Tab).ToList())
        {
            var tab = _stashTabs[tabKey];
            if (!tab.Dirty || tab.Items is null) continue;
            SaveRows.WriteStashTab(db, tabKey.Account, tabKey.Tab, tab.Items);
            db.OnRollback(() => _stashTabs[tabKey] = tab);
            _stashTabs[tabKey] = (tab.Items, false);
        }
    }

    /// <summary>Returns the pinned in-memory character save without touching SQLite when present.</summary>
    public static bool TryGetCachedCharacter(string character, out CharacterSaves.Save? save)
    {
        if (FailWrite is not null || _db is null) { save = null; return false; }
        var (found, cached) = Db.Read(() =>
            _characters.TryGetValue(character, out var entry) ? (true, entry.Save) : (false, (CharacterSaves.Save?)null));
        save = cached;
        return found;
    }

    public static T Atomically<T>(Func<T> work) => Db.Atomically(work);

    public static void Atomically(Action work) => Db.Atomically(work);

    /// <summary>Tests: a write whose key this says yes to fails, as a full disk would.</summary>
    public static Func<string, bool>? FailWrite;

    /// <summary>A save as JSON (the record the tables hold), or null when there is none: tests compare saves with it.</summary>
    public static string? Get(SaveDatabase.Table table, string key) => table == SaveDatabase.Table.Characters
        ? LoadCharacter(key) is { } save ? JsonSerializer.Serialize(save, CharacterSaves.Opts) : null
        : LoadStash(key) is { } stash ? JsonSerializer.Serialize(stash, CharacterSaves.Opts) : null;

    /// <summary>Writes a save given as JSON (tests).</summary>
    public static void Put(SaveDatabase.Table table, string key, string json)
    {
        if (table == SaveDatabase.Table.Characters) PutCharacter(key, JsonSerializer.Deserialize<CharacterSaves.Save>(json, CharacterSaves.Opts)!);
        else PutStash(key, JsonSerializer.Deserialize<AccountStash.Save>(json, CharacterSaves.Opts)!);
    }

    /// <summary>A character's save, or null when it has none. A save that can't be read throws
    /// <see cref="UnreadableSaveException"/> and is left as it is: starting over would write over it.</summary>
    public static CharacterSaves.Save? LoadCharacter(string character) => Readable(SaveDatabase.Table.Characters, character, () =>
    {
        var db = Db;
        return db.Read(() =>
        {
            if (FailWrite is null && _pinnedCharacters.ContainsKey(character))
            {
                if (_characters.TryGetValue(character, out var cached)) return cached.Save;
                _characterReads++;
                var loaded = SaveRows.LoadCharacter(db, character);
                _characters[character] = (loaded, false);
                return loaded;
            }
            _characterReads++;
            return SaveRows.LoadCharacter(db, character);
        });
    });

    /// <summary>Whether the character has a save and its saved experience, without reading the rest of the save (not
    /// counted in <see cref="CharacterReads"/>).</summary>
    public static (bool Saved, uint? Experience) CharacterExperience(string character)
    {
        try
        {
            var db = Db;
            return db.Read(() =>
            {
                if (FailWrite is null && _characters.TryGetValue(character, out var cached))
                    return (cached.Save is not null, cached.Save?.Experience);
                return SaveRows.CharacterExperience(db, character);
            });
        }
        catch (Exception e) when (e is InvalidDataException or InvalidCastException or OverflowException)
        {
            throw Unreadable(SaveDatabase.Table.Characters, character, e);
        }
    }

    /// <summary>How many times a character's save was read from its tables since the Gateway started (counted under the
    /// database's lock). A whole save is costly to read (its items and their mods), so what runs many times a second must
    /// not add to this: the lag probe prints its rate (ConnectionPerformance.RuntimeSummary), and CharacterSaves.Look
    /// keeps the parts a fight asks for.</summary>
    public static long CharacterReads => Interlocked.Read(ref _characterReads);
    static long _characterReads;

    public static void PutCharacter(string character, CharacterSaves.Save save)
    {
        if (FailWrite?.Invoke(character) == true) throw new IOException($"save of {character} failed (test)");
        var db = Db;
        bool cached = db.Read(() =>
        {
            if (FailWrite is not null || !_pinnedCharacters.ContainsKey(character)) return false;
            bool had = _characters.TryGetValue(character, out var prev);
            if (db.InAtomic)
                db.OnRollback(() => { if (had) _characters[character] = prev; else _characters.Remove(character); });
            _characters[character] = (save, true);
            return true;
        });
        if (!cached) SaveRows.WriteCharacter(db, character, save);
    }

    public static bool PutCharacterInventories(string character, Dictionary<byte, List<CharacterSaves.SavedPlacement>> inventories,
        CharacterSaves.SavedItem? startWeapon, uint? unsavedExperience)
    {
        if (FailWrite?.Invoke(character) == true) throw new IOException($"save of {character} failed (test)");
        var db = Db;
        bool? cached = db.Read<bool?>(() =>
        {
            if (FailWrite is not null || !_pinnedCharacters.ContainsKey(character)) return null;
            bool had = _characters.TryGetValue(character, out var prev);
            var current = had ? prev.Save : LoadPinnedCharacterLocked(db, character);
            if (current is null)
            {
                if (!had) _characters[character] = (null, false);
                return false;
            }
            if (db.InAtomic)
                db.OnRollback(() => { if (had) _characters[character] = prev; else _characters.Remove(character); });
            _characters[character] = (current with
            {
                Inventories = inventories,
                StartWeapon = startWeapon,
                Experience = unsavedExperience ?? current.Experience,
            }, true);
            return true;
        });
        return cached ?? SaveRows.WriteCharacterInventories(db, character, inventories, startWeapon, unsavedExperience);
    }

    public static bool PutCharacterExperience(string character, uint experience) => Db.Atomically(() =>
    {
        var db = Db;
        if (FailWrite is null && _pinnedCharacters.ContainsKey(character))
        {
            bool had = _characters.TryGetValue(character, out var prev);
            var current = had ? prev.Save : LoadPinnedCharacterLocked(db, character);
            if (current is null)
            {
                if (!had) _characters[character] = (null, false);
                return false;
            }
            if (db.InAtomic)
                db.OnRollback(() => { if (had) _characters[character] = prev; else _characters.Remove(character); });
            _characters[character] = (current with { Experience = experience }, true);
            return true;
        }
        if (!SaveRows.CharacterExperience(db, character).Saved) return false;
        if (FailWrite?.Invoke(character) == true) throw new IOException($"save of {character} failed (test)");
        return SaveRows.WriteCharacterExperience(db, character, experience);
    });

    static CharacterSaves.Save? LoadPinnedCharacterLocked(SaveDatabase db, string character)
    {
        _characterReads++;
        return SaveRows.LoadCharacter(db, character);
    }

    /// <summary>A stash's header (its tabs without their items), or null when it has none.</summary>
    public static AccountStash.Save? LoadStash(string account) => Readable(SaveDatabase.Table.Stashes, account, () =>
    {
        var db = Db;
        return db.Read(() =>
        {
            if (FailWrite is null && _pinnedStashes.ContainsKey(account))
            {
                if (_stashes.TryGetValue(account, out var cached)) return cached.Header;
                var loaded = SaveRows.LoadStash(db, account);
                _stashes[account] = (loaded, false);
                return loaded;
            }
            return SaveRows.LoadStash(db, account);
        });
    });

    public static void PutStash(string account, AccountStash.Save header)
    {
        if (FailWrite?.Invoke(account) == true) throw new IOException($"save of {account}'s stash failed (test)");
        var db = Db;
        bool cached = db.Read(() =>
        {
            if (FailWrite is not null || !_pinnedStashes.ContainsKey(account)) return false;
            bool had = _stashes.TryGetValue(account, out var prev);
            if (db.InAtomic)
                db.OnRollback(() => { if (had) _stashes[account] = prev; else _stashes.Remove(account); });
            _stashes[account] = (header, true);
            return true;
        });
        if (!cached) SaveRows.WriteStash(db, account, header);
    }

    /// <summary>One stash tab's items, or null when the stash has none saved.</summary>
    public static List<CharacterSaves.SavedPlacement>? LoadStashTab(string account, int tab) =>
        Readable(SaveDatabase.Table.Stashes, $"{account} tab {tab}", () =>
        {
            var db = Db;
            return db.Read(() =>
            {
                var key = (account, tab);
                if (FailWrite is null && _pinnedStashes.ContainsKey(account))
                {
                    if (_stashTabs.TryGetValue(key, out var cached)) return cached.Items;
                    var loaded = SaveRows.LoadStashTab(db, account, tab);
                    _stashTabs[key] = (loaded, false);
                    return loaded;
                }
                return SaveRows.LoadStashTab(db, account, tab);
            });
        });

    public static void PutStashTab(string account, int tab, List<CharacterSaves.SavedPlacement> items)
    {
        if (FailWrite?.Invoke(account) == true) throw new IOException($"save of {account}'s stash tab {tab} failed (test)");
        var db = Db;
        bool cached = db.Read(() =>
        {
            if (FailWrite is not null || !_pinnedStashes.ContainsKey(account)) return false;
            var key = (account, tab);
            bool had = _stashTabs.TryGetValue(key, out var prev);
            if (db.InAtomic)
                db.OnRollback(() => { if (had) _stashTabs[key] = prev; else _stashTabs.Remove(key); });
            _stashTabs[key] = (items, true);
            return true;
        });
        if (!cached) SaveRows.WriteStashTab(db, account, tab, items);
    }

    static T? Readable<T>(SaveDatabase.Table table, string key, Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidCastException or FormatException or OverflowException)
        {
            throw Unreadable(table, key, e);
        }
    }

    static UnreadableSaveException Unreadable(SaveDatabase.Table table, string key, Exception? e)
    {
        string message = $"the save of {key} ({table} in {Db.Path}) can't be read{(e is null ? "" : $" ({e.Message})")}; it is left as it is: " +
                         "fix it. Don't delete it: that starts it over";
        Console.WriteLine($"[saves] {message}");
        return new UnreadableSaveException(Db.Path, message, e);
    }

    /// <summary>
    /// Saves from before the database (data/saves/*.json, data/stashes/*.json) go into its JSON tables once (the move into
    /// the save tables, <see cref="SaveRows.Migrate"/>, takes them from there), in one transaction, and
    /// the files are renamed to *.json.imported (kept, never read again). A file that can't be read stops the import, and
    /// the Gateway: the character would otherwise start over. Returns how many were imported.
    /// </summary>
    public static int ImportJson(SaveDatabase db, string folder, JsonSerializerOptions options)
    {
        const string Done = "json_imported";
        if (db.GetMeta(Done) is not null) return 0;
        var files = Files(Path.Combine(folder, "saves"), SaveDatabase.Table.Characters)
            .Concat(Files(Path.Combine(folder, "stashes"), SaveDatabase.Table.Stashes)).ToList();
        int imported = db.Atomically(() =>
        {
            int count = 0;
            foreach (var (path, table, key) in files)
            {
                // Read as the save it is, so a broken file stops here (UnreadableSaveException) instead of being imported.
                _ = table == SaveDatabase.Table.Characters
                    ? SaveFiles.Load<CharacterSaves.Save>(path, options) as object
                    : SaveFiles.Load<AccountStash.Save>(path, options);
                if (SaveFiles.Read(path) is not { } json || db.Get(table, key) is not null) continue;
                db.Put(table, key, json);
                count++;
            }
            db.SetMeta(Done, DateTime.UtcNow.ToString("O"));
            return count;
        });
        foreach (var (path, _, _) in files)
            try { File.Move(path, path + ".imported"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"[saves] {path} imported but not renamed: {e.Message}"); }
        if (files.Count > 0) Console.WriteLine($"[saves] {imported} JSON save(s) imported into {db.Path}; the files are kept as *.json.imported");
        return imported;
    }

    static IEnumerable<(string Path, SaveDatabase.Table Table, string Key)> Files(string folder, SaveDatabase.Table table) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json").Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Order()
                .Select(f => (f, table, Path.GetFileNameWithoutExtension(f)))
            : [];
}
