using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

// The player's full stat snapshot is rebuilt by every hit, cast, leech, aura and flask check: within one skill use (C→S 0x24,
// 0x25) it was built several times over with the same inputs (live 2026-11-05: up to 1.3 s per message in the Upper
// Sceptre of God, the input queue 16 s behind, and the move echoes put the player back where they had been). Inside such a
// message's scope (PlayerStatsScope) a snapshot is reused while its inputs are the same: the pools, the buffs added, life,
// mana, the auras, moving and the /set_stat overrides, for at most Lifetime (a task the message started outlives it).
// Everything else (equipment, item and passive messages, ticks) builds its own.
public static partial class Game
{
    sealed class PlayerStatsEntry
    {
        public double At;
        public object? Pools;
        public long NextBuff, Life, Mana;
        public long Golems, Slayer, Offerings, Hierophant;
        public bool ArcaneSurge;
        public int Overrides, Auras, ResistancePenalty;
        public bool Moving;
        public Dictionary<uint, int>? Stats;
        public double Baseline;
    }

    const double PlayerStatsLifetime = .05;

    sealed class PlayerStatsCacheScope(ClientState conn) : IDisposable
    {
        public readonly ClientState Conn = conn;
        public PlayerStatsEntry? Entry;
        public void Dispose() { if (ReferenceEquals(CurrentPlayerStatsScope.Value, this)) CurrentPlayerStatsScope.Value = null; }
    }
    static readonly AsyncLocal<PlayerStatsCacheScope?> CurrentPlayerStatsScope = new();

    /// <summary>Reuse the player's stat snapshot within this message (see above).</summary>
    public static IDisposable PlayerStatsScope(ClientState conn) => CurrentPlayerStatsScope.Value = new(conn);

    static PlayerStatsEntry PlayerStatsKey(ClientState conn, double now)
    {
        int overrides;
        lock (conn.Monsters) overrides = conn.PlayerStats.Count;
        return new()
        {
            At = now, Pools = conn.Pools, NextBuff = conn.NextBuff, Life = (long)conn.PlayerLife, Mana = (long)conn.PlayerMana,
            Overrides = overrides, Auras = conn.Auras.Count, Moving = conn.Walk.Moving(now), Golems = conn.Golems.Version,
            Slayer = global::Slayer.Revision(conn), Offerings = conn.Summons.OfferingRevision, Hierophant = global::Hierophant.Revision(conn),
            ArcaneSurge = conn.SupportGems.ArcaneSurgeActiveAt(now),
            ResistancePenalty = Game.ResistancePenalty(conn),
        };
    }

    static bool SameInputs(PlayerStatsEntry a, PlayerStatsEntry b) =>
        b.At - a.At is >= 0 and < PlayerStatsLifetime && Equals(a.Pools, b.Pools) && a.NextBuff == b.NextBuff
        && a.Life == b.Life && a.Mana == b.Mana && a.Overrides == b.Overrides && a.Auras == b.Auras && a.Moving == b.Moving && a.Golems == b.Golems
        && a.Slayer == b.Slayer && a.Offerings == b.Offerings && a.Hierophant == b.Hierophant && a.ArcaneSurge == b.ArcaneSurge
        && a.ResistancePenalty == b.ResistancePenalty;

    public static Dictionary<uint, int> CachedPlayerStats(ClientState conn, out double baselineLifePerSecond)
    {
        if (CurrentPlayerStatsScope.Value is not { } scope || !ReferenceEquals(scope.Conn, conn))
            return ThrottledPlayerStats(conn, out baselineLifePerSecond);
        var key = PlayerStatsKey(conn, conn.Clock.Elapsed.TotalSeconds);
        lock (scope)
            if (scope.Entry is { Stats: { } hit } last && SameInputs(last, key))
            {
                baselineLifePerSecond = last.Baseline;
                return new(hit);
            }
        var stats = CharacterPlayerStatsSnapshot(conn, out baselineLifePerSecond, out _);
        key.Stats = new(stats);
        key.Baseline = baselineLifePerSecond;
        lock (scope) scope.Entry = key;
        return stats;
    }

    // A live connection (its player update loops run) rebuilds its snapshot at most once a frame of 60 Hz while the inputs
    // above are the same: a fight asked for 157 full builds a second (live 2026-11-05, 56% of a core), most of them for the
    // same state. Anything slower than a frame (regeneration, Rage decay, Fortification expiry, "recently" windows) is at
    // most that late; a change of the pools, a buff added, life, mana, the auras, moving or an override rebuilds at once.
    // Off a live connection (tests, the build scopes) every read builds.
    const double PlayerStatsFrame = 1.0 / 60;
    sealed class ThrottledStats { public PlayerStatsEntry? Entry; }
    static readonly RuleTable<ClientState, ThrottledStats> ThrottledPlayerStatsEntries = new();

    static Dictionary<uint, int> ThrottledPlayerStats(ClientState conn, out double baselineLifePerSecond)
    {
        if (conn.PlayerUpdatesState is null || conn.Closed)
            return CharacterPlayerStatsSnapshot(conn, out baselineLifePerSecond, out _);
        var holder = ThrottledPlayerStatsEntries.GetOrCreateValue(conn);
        var key = PlayerStatsKey(conn, conn.Clock.Elapsed.TotalSeconds);
        lock (holder)
            if (holder.Entry is { Stats: { } hit } last && key.At - last.At is >= 0 and < PlayerStatsFrame && SameInputs(last, key))
            {
                baselineLifePerSecond = last.Baseline;
                return new(hit);
            }
        var stats = CharacterPlayerStatsSnapshot(conn, out baselineLifePerSecond, out _);
        key.Stats = new(stats);
        key.Baseline = baselineLifePerSecond;
        lock (holder) holder.Entry = key;
        return stats;
    }

    // ---- the skill list ----

    // The last full skill list (0x2bc, no removals) this client was sent: the Rage, charge, aura, Fortification, Onslaught,
    // Rampage and Antiquarian refreshes resend it whole, about 32 KB, at every change of theirs, also when not one skill
    // changed. An unchanged list is left out. The full refresh (SendCharacterStats) always sends and records it.
    sealed class SentSkillList { public byte[]? Bytes; public IReadOnlyList<GrantedSkill>? Skills; }
    static readonly RuleTable<ClientState, SentSkillList> SentSkillLists = new();

    public static byte[] PlayerSkillsIfChanged(ClientState conn, IReadOnlyList<GrantedSkill> skills)
    {
        var bytes = InstanceEntities.PlayerSkillsChange(PlayerId, skills, [], canDetonateMines: CanDetonateMines(conn));
        var sent = SentSkillLists.GetOrCreateValue(conn);
        lock (sent)
        {
            if (sent.Bytes is { } last && last.AsSpan().SequenceEqual(bytes)) return [];
            (sent.Bytes, sent.Skills) = (bytes, skills);
        }
        return bytes;
    }

    // What the full refresh sent: the base the next partial refresh compares with.
    public static void RecordPlayerSkills(ClientState conn, IReadOnlyList<GrantedSkill> skills)
    {
        var sent = SentSkillLists.GetOrCreateValue(conn);
        lock (sent) (sent.Bytes, sent.Skills) = (InstanceEntities.PlayerSkillsChange(PlayerId, skills, [], canDetonateMines: CanDetonateMines(conn)), skills);
    }

    /// <summary>A stat of the skill as the client last received it in the skill list (0 when the skill went without it),
    /// null when the skill was not sent. The client times some effects by the skill's stats it holds, not the
    /// character's current ones (Hammerfalls' waves).</summary>
    public static int? SentSkillStat(ClientState conn, ushort skillId, string stat)
    {
        if (Stats.Key(stat) is not uint key || !SentSkillLists.TryGetValue(conn, out var sent)) return null;
        lock (sent)
        {
            if (sent.Skills?.FirstOrDefault(s => s.Id == skillId) is not { } skill) return null;
            foreach (var (k, value) in skill.Stats ?? []) if (k == key) return value;
            return 0;
        }
    }
}
