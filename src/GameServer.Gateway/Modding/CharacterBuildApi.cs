using System.Text.Json;
using GameServer.Core.Accounts;
using AccountCharacter = GameServer.Core.Accounts.Character;
using GameServer.Modding;
using GameServer.Storage;

/// <summary>SDK-only access to the server's validation and post-session save transaction.</summary>
public sealed class CharacterBuildApi(ModuleLifetime lifetime, TimeProvider? time = null) : ICharacterBuilds
{
    /// <summary>Character build imports for this mod's load: its plans end when it unloads.</summary>
    public static ICharacterBuilds For(IModContext mod) =>
        new CharacterBuildApi(mod.Lifetime as ModuleLifetime ?? throw new ArgumentException("The mod was not loaded by this server."));

    public string Capture(IPlayer player) => JsonSerializer.Serialize(Current(PlayerOf(player)));

    public ICharacterBuildPlan Prepare(IPlayer modPlayer, string json)
    {
        var player = PlayerOf(modPlayer);
        var plan = CharacterBuildImports.Validate(json, Current(player));
        return new Plan(player, plan, lifetime, time);
    }

    Player PlayerOf(IPlayer modPlayer)
    {
        if (!lifetime.IsActive) throw new InvalidOperationException("The importing mod is unloaded.");
        if (modPlayer is not IGamePlayer issued) throw new ArgumentException("The player was not issued by this server.");
        var player = issued.Player;
        if (player.Character.Id is null || !Available(player))
            throw new InvalidOperationException("Wait for a living character to enter an area.");
        return player;
    }

    static bool Available(Player player) => !player.Closed && !player.LeavingArea
        && player.PendingTransfer is null && player.PlayerLife != 0;

    static AccountCharacter Current(Player player)
    {
        var accounts = new SqliteAccountStore(Saves.Db);
        var record = accounts.GetCharacter(player.Character.Id!.Value)
            ?? throw new InvalidDataException("The character no longer exists.");
        if (record.AccountId.ToString() != player.Character.Account && record.AccountId.ToString("N") != player.Character.Account
            && !string.Equals(accounts.GetAccount(record.AccountId)?.Name, player.Character.Account, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cannot confirm character ownership.");
        return record;
    }

    sealed class Plan : ICharacterBuildPlan
    {
        readonly Player player;
        readonly ModuleLifetime lifetime;
        readonly CharacterBuildImports.Session session;
        readonly Lock gate = new();
        public string Summary { get; }
        public IReadOnlyList<string> Warnings { get; }

        public Plan(Player player, CharacterBuildImports.Prepared build, ModuleLifetime lifetime, TimeProvider? time)
        {
            this.player = player; this.lifetime = lifetime;
            session = new(time); session.Stage(build);
            Summary = build.Summary; Warnings = Array.AsReadOnly(build.Warnings);
        }

        public bool Confirm()
        {
            lock (gate)
            {
                var build = session.Confirm();
                if (build is null) return false;
                bool accepted = false;
                lifetime.TryCommit(() =>
                {
                    if (!Available(player) || player.Character.Id != build.CharacterId
                        || Program.InGame.Keys.Any(c => c != player && c.Character.Id == build.CharacterId && !c.Closed)
                        || !CharacterBuildImports.Applying.TryAdd(build.CharacterId, 0)) return;
                    player.BuildImport.Accept(build);
                    player.Connection.CloseReason = "confirmed character build import";
                    player.Closed = true;
                    player.InvalidateIncomingHits();
                    accepted = true;
                });
                return accepted;
            }
        }

        public void Dispose() { lock (gate) session.Clear(); }
    }
}
