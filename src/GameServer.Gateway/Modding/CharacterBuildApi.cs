using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Modding;
using GameServer.Storage;

/// <summary>SDK-only access to the server's validation and post-session save transaction.</summary>
public sealed class CharacterBuildApi(ModuleLifetime lifetime, TimeProvider? time = null) : ICharacterBuilds
{
    public string Capture(IPlayer player) => JsonSerializer.Serialize(Current(Connection(player)));

    public ICharacterBuildPlan Prepare(IPlayer player, string json)
    {
        var conn = Connection(player);
        var plan = CharacterBuildImports.Validate(json, Current(conn));
        return new Plan(conn, plan, lifetime, time);
    }

    ClientState Connection(IPlayer player)
    {
        if (!lifetime.IsActive) throw new InvalidOperationException("The importing mod is unloaded.");
        if (player is not IConnectionPlayer issued) throw new ArgumentException("The player was not issued by this server.");
        var conn = issued.Conn;
        if (conn.CharacterId is null || !Available(conn))
            throw new InvalidOperationException("Wait for a living character to enter an area.");
        return conn;
    }

    static bool Available(ClientState conn) => !conn.Closed && !conn.LeavingArea
        && conn.PendingTransfer is null && conn.PlayerLife != 0;

    static Character Current(ClientState conn)
    {
        var accounts = new SqliteAccountStore(Saves.Db);
        var record = accounts.GetCharacter(conn.CharacterId!.Value)
            ?? throw new InvalidDataException("The character no longer exists.");
        if (record.AccountId.ToString() != conn.Account && record.AccountId.ToString("N") != conn.Account
            && !string.Equals(accounts.GetAccount(record.AccountId)?.Name, conn.Account, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cannot confirm character ownership.");
        return record;
    }

    sealed class Plan : ICharacterBuildPlan
    {
        readonly ClientState conn;
        readonly ModuleLifetime lifetime;
        readonly CharacterBuildImports.Session session;
        readonly Lock gate = new();
        public string Summary { get; }
        public IReadOnlyList<string> Warnings { get; }

        public Plan(ClientState conn, CharacterBuildImports.Prepared build, ModuleLifetime lifetime, TimeProvider? time)
        {
            this.conn = conn; this.lifetime = lifetime;
            session = new(time); session.Stage(build);
            Summary = build.Summary; Warnings = Array.AsReadOnly(build.Warnings);
        }

        public bool Confirm()
        {
            lock (gate)
            {
                var build = session.Confirm();
                if (build is null) return false;
                lock (conn.Monsters)
                {
                    bool accepted = false;
                    lifetime.TryCommit(() =>
                    {
                        if (!Available(conn) || conn.CharacterId != build.CharacterId
                            || Program.InGame.Keys.Any(c => c != conn && c.CharacterId == build.CharacterId && !c.Closed)
                            || !CharacterBuildImports.Applying.TryAdd(build.CharacterId, 0)) return;
                        conn.BuildImport.Accept(build);
                        conn.CloseReason = "confirmed character build import";
                        conn.Closed = true;
                        conn.InvalidateIncomingHits();
                        accepted = true;
                    });
                    return accepted;
                }
            }
        }

        public void Dispose() { lock (gate) session.Clear(); }
    }
}
