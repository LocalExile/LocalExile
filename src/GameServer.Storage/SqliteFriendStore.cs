namespace GameServer.Storage;

/// <summary>Account-level friend invitations and acknowledgements. No cached state can run ahead of the database.</summary>
public sealed class SqliteFriendStore
{
    readonly SaveDatabase db;

    public SqliteFriendStore(SaveDatabase database)
    {
        db = database;
        db.Atomically(() =>
        {
            using var c = db.Command("CREATE TABLE IF NOT EXISTS account_friends " +
                "(owner TEXT NOT NULL, friend TEXT NOT NULL, PRIMARY KEY(owner, friend), CHECK(owner <> friend)) STRICT");
            c.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<Guid> Friends(Guid owner) => db.Read(() =>
    {
        using var c = db.Command("SELECT friend FROM account_friends WHERE owner = $owner ORDER BY friend", ("$owner", owner.ToString()));
        using var r = c.ExecuteReader();
        var result = new List<Guid>();
        while (r.Read()) result.Add(Guid.Parse(r.GetString(0)));
        return result;
    });

    public IReadOnlyList<Guid> Incoming(Guid owner) => db.Read(() =>
    {
        using var c = db.Command("SELECT owner FROM account_friends WHERE friend = $owner ORDER BY owner", ("$owner", owner.ToString()));
        using var r = c.ExecuteReader();
        var result = new List<Guid>();
        while (r.Read()) result.Add(Guid.Parse(r.GetString(0)));
        return result;
    });

    public bool Add(Guid owner, Guid friend) => db.Atomically(() =>
    {
        if (owner == friend) return false;
        // Both identities must still exist at the transaction boundary.
        using var c = db.Command("INSERT OR IGNORE INTO account_friends (owner, friend) " +
            "SELECT $owner, $friend WHERE EXISTS (SELECT 1 FROM accounts WHERE id = $owner) " +
            "AND EXISTS (SELECT 1 FROM accounts WHERE id = $friend)",
            ("$owner", owner.ToString()), ("$friend", friend.ToString()));
        return c.ExecuteNonQuery() != 0;
    });

    public bool Remove(Guid owner, Guid friend) => db.Atomically(() =>
    {
        using var c = db.Command("DELETE FROM account_friends WHERE (owner = $owner AND friend = $friend) " +
            "OR (owner = $friend AND friend = $owner)",
            ("$owner", owner.ToString()), ("$friend", friend.ToString()));
        return c.ExecuteNonQuery() != 0;
    });
}
