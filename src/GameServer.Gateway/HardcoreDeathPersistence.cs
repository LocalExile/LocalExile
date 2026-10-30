using System.Collections.Concurrent;
using GameServer.Core.Accounts;
using GameServer.Storage;
using GameServer.Core.Diagnostics;

/// <summary>Commits the terminal HC outcome together with the character's carried progress, before any death packets.
/// Account stash rows are deliberately untouched: carried items migrate, the source league's bank does not.</summary>
static class HardcoreDeathPersistence
{
    sealed record PendingDeath(string Database, string Character, Guid Id, League Source, Action Flush, DateTimeOffset AtUtc);
    static readonly ConcurrentDictionary<(string Database, Guid Id), PendingDeath> Pending = new();
    static string DatabaseKey(SaveDatabase db) => Path.GetFullPath(db.Path).ToUpperInvariant();

    public static Character Commit(SaveDatabase db, string character, League source, Action flushProgress,
        DateTimeOffset? atUtc = null, Guid? expectedCharacterId = null)
    {
        string database = DatabaseKey(db);
        Guid id = expectedCharacterId ?? new SqliteAccountStore(db).FindCharacter(character)?.Id
            ?? throw new InvalidOperationException("The dying character no longer exists");
        var entry = new PendingDeath(database, character, id, source, flushProgress, atUtc ?? DateTimeOffset.UtcNow);
        // Queue before opening the transaction: even BEGIN can fail while storage is unavailable.
        var pending = Pending.AddOrUpdate((database, id), entry, (_, prior) => entry with { AtUtc = prior.AtUtc });
        Character dead;
        try
        {
            dead = db.Atomically(() =>
            {
                var store = new SqliteAccountStore(db);
                var current = store.FindCharacter(character) ?? throw new InvalidOperationException("The dying character no longer exists");
                if (current.Id != id)
                    throw new InvalidOperationException("The dying character was deleted and its name has been reused");
                var outcome = HardcoreDeathRules.Apply(current, source, pending.AtUtc);
                if (current.HardcoreDeath is not null) return current; // never flush an old callback over softcore progress
                flushProgress();
                store.UpdateCharacter(outcome);
                return outcome;
            });
        }
        catch (InvalidOperationException)
        {
            // A deleted/replaced character must not poison a future character with the same name.
            var current = new SqliteAccountStore(db).FindCharacter(character);
            if (current is null || current.Id != id)
                Pending.TryRemove(new KeyValuePair<(string, Guid), PendingDeath>((database, id), pending));
            throw;
        }
        Pending.TryRemove(new KeyValuePair<(string, Guid), PendingDeath>((database, id), pending));
        return dead;
    }

    /// <summary>Reconnect fails closed until a death whose first write failed can be committed.</summary>
    public static void RetryPendingCharacter(SaveDatabase db, Guid? characterId)
    {
        if (characterId is { } id && Pending.TryGetValue((DatabaseKey(db), id), out var pending))
            Commit(db, pending.Character, pending.Source, pending.Flush, pending.AtUtc, pending.Id);
    }

    /// <summary>The save timer retries transient storage failures even after the dying connection has closed.</summary>
    public static void RetryPending(SaveDatabase db)
    {
        string database = DatabaseKey(db);
        foreach (var pending in Pending.Values.Where(p => p.Database == database))
            try { RetryPendingCharacter(db, pending.Id); }
            catch (Exception e) { Console.WriteLine($"[saves] Hardcore death of {pending.Character} still pending: {ServerLog.Describe(e)}"); }
    }
}
