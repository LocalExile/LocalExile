using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal const string SummonBeastialUrsaEffect = "SummonBeastialUrsa";
    internal const string SummonBeastialRhoaEffect = "SummonBeastialRhoa";
    internal const string SummonBeastialSnakeEffect = "SummonBeastialSnake";

    internal sealed record BeastKind(string EffectId, string Variety, string MinionKind);

    static readonly IReadOnlyDictionary<string, BeastKind> BeastKindsByEffect =
        new Dictionary<string, BeastKind>(StringComparer.Ordinal)
        {
            [SummonBeastialUrsaEffect] = new(SummonBeastialUrsaEffect,
                "Metadata/Monsters/DropBear/DropBearUniqueSummoned", "Summoned Ursa"),
            [SummonBeastialRhoaEffect] = new(SummonBeastialRhoaEffect,
                "Metadata/Monsters/Rhoas/RhoaUniqueSummoned", "Summoned Rhoa"),
            [SummonBeastialSnakeEffect] = new(SummonBeastialSnakeEffect,
                "Metadata/Monsters/Snake/SnakeSpitUniqueSummoned", "Summoned Cobra"),
        };

    internal static BeastKind? BeastKindFor(string? effectId) =>
        effectId is not null && BeastKindsByEffect.TryGetValue(effectId, out var kind) ? kind : null;

    internal static bool IsSummonBeastialCompanionEffect(string? effectId) => BeastKindFor(effectId) is not null;

    static bool IsBeastCompanionKind(string kind) =>
        BeastKindsByEffect.Values.Any(candidate => candidate.MinionKind == kind);

    static WornGrantedSkills.Grant BeastSourceIdentity(WornGrantedSkills.Grant grant) => grant;

    static bool BeastSourceEligible(ClientState conn, WornGrantedSkills.Grant grant) =>
        CurrentGameWork
        && grant.Host != 0
        && IsSummonBeastialCompanionEffect(grant.EffectId)
        && LeagueFor(conn).Rules.AllowsGemEffect(grant.EffectId)
        && !IsInactive(conn, grant.Host);

    /// <summary>
    /// Reconciles the live worn grants without building CharacterPlayerStats or SupportedSkill. A missing eligible grant
    /// retires its generation irrevocably and drops pending/active ownership in the same observation.
    /// </summary>
    internal static void ReconcileBeastCompanionsLocked(ClientState conn)
    {
        var live = AllWornGrantedSkillsOf(conn).Where(g => BeastSourceEligible(conn, g)).ToArray();
        var identities = live.Select(BeastSourceIdentity).ToHashSet();
        foreach (var stale in conn.Beasts.SourceGenerations.Where(s => !identities.Contains(s.Key)).ToArray())
        {
            stale.Value.Retired = true;
            conn.Beasts.SourceGenerations.Remove(stale.Key);
        }
        foreach (var grant in live)
        {
            var identity = BeastSourceIdentity(grant);
            if (!conn.Beasts.SourceGenerations.TryGetValue(identity, out var owner) || owner.Retired)
                conn.Beasts.SourceGenerations[identity] = new(grant);
        }

        if (conn.Beasts.Active is { } active
            && (!BeastSourceGenerationCurrentLocked(conn, active.Source)
                || !conn.Minions.Owns(active.Id, active.Monster)
                || !conn.Monsters.TryGetValue(active.Id, out var held)
                || !ReferenceEquals(held, active.Monster)))
            RetireBeastCompanionLocked(conn, active, conn.Clock.Elapsed.TotalSeconds,
                output: ReferenceEquals(active.Area, conn.Instance) && ReferenceEquals(active.Lifetime, CombatLifetime));
        if (conn.Beasts.Pending is { } pending && !BeastSourceGenerationCurrentLocked(conn, pending.Source))
            conn.Beasts.Pending = null;
    }

    static BeastCompanions.SourceGeneration? CurrentBeastSourceLocked(ClientState conn, WornGrantedSkills.Grant grant)
    {
        ReconcileBeastCompanionsLocked(conn);
        return conn.Beasts.SourceGenerations.TryGetValue(BeastSourceIdentity(grant), out var owner)
            && !owner.Retired ? owner : null;
    }

    static bool BeastSourceGenerationCurrentLocked(ClientState conn, BeastCompanions.Source source)
    {
        if (!CurrentGameWork || source.Owner.Retired) return false;
        return conn.Beasts.SourceGenerations.TryGetValue(BeastSourceIdentity(source.Grant), out var owner)
            && ReferenceEquals(owner, source.Owner);
    }

    /// <summary>The actual worn source generation remains current, without rebuilding player stats.</summary>
    static bool BeastSourceCurrent(ClientState conn, BeastCompanions.Source source)
    {
        lock (conn.Monsters)
        {
            ReconcileBeastCompanionsLocked(conn);
            return BeastSourceGenerationCurrentLocked(conn, source);
        }
    }

    static bool BeastGrantCurrent(ClientState conn, WornGrantedSkills.Grant grant)
    {
        lock (conn.Monsters) return CurrentBeastSourceLocked(conn, grant) is not null;
    }

    internal static bool HasSourceValidBeastMinion(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0
                || conn.Beasts.Active is not { } active) return false;
            ReconcileBeastCompanionsLocked(conn);
            return ReferenceEquals(conn.Beasts.Active, active)
                && ReferenceEquals(active.Lifetime, CombatLifetime)
                && ReferenceEquals(active.Area, conn.Instance)
                && conn.Minions.Owns(active.Id, active.Monster)
                && conn.Monsters.TryGetValue(active.Id, out var held)
                && ReferenceEquals(held, active.Monster)
                && !active.Monster.Dead && active.Monster.Life > 0
                && BeastSourceGenerationCurrentLocked(conn, active.Source);
        }
    }

    /// <summary>Rejects a stale worn-host activation before cooldown or resource payment.</summary>
    internal static bool BeastCompanionAdmissionCurrent(ClientState conn, SupportedGem gem, ushort skillId) =>
        gem.WornGrant is { } grant
        && gem.RequirementsMet
        && BeastGrantCurrent(conn, grant)
        && GemSkillInstanceDisabled(conn, gem.Effect, skillId) is null;

    /// <summary>Captures accepted work under the same Monsters lock that paid for it.</summary>
    internal static void QueueBeastCompanionLocked(ClientState conn, SupportedGem gem, ushort skillId, int? actionToken)
    {
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0
            || gem.WornGrant is not { } grant
            || BeastKindFor(grant.EffectId) is not { } kind
            || CurrentBeastSourceLocked(conn, grant) is not { } owner) return;
        int Stat(string name) => Stats.Key(name) is uint key ? gem.Stats.GetValueOrDefault(key) : 0;
        int level = Stat("base_display_minion_actor_level");
        int limit = Stat("base_number_of_beast_minions_allowed");
        if (level <= 0 || limit <= 0) return;
        conn.Beasts.Pending = new(new(owner), skillId, grant.EffectId, kind.Variety, kind.MinionKind,
            (byte)Math.Clamp(level, 1, 100),
            Stat("minion_maximum_life_+%"), limit, CombatLifetime, conn.Instance, actionToken);
    }

    internal static async Task TickBeastCompanions(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, double now, object? rules)
    {
        bool Current() => CurrentGameWork && ReferenceEquals(CombatLifetime, rules);
        if (!Current() || conn.Closed || conn.LeavingArea) return;

        lock (conn.Monsters)
        {
            if (!Current() || conn.Closed || conn.LeavingArea) return;
            ReconcileBeastCompanionsLocked(conn);
            if (conn.Beasts.Active is { } active)
            {
                bool present = conn.Minions.Owns(active.Id, active.Monster)
                    && ReferenceEquals(active.Area, conn.Instance) && ReferenceEquals(active.Lifetime, rules)
                    && conn.Monsters.TryGetValue(active.Id, out var held) && ReferenceEquals(held, active.Monster);
                bool keep = present && conn.PlayerLife > 0 && !active.Monster.Dead && active.Monster.Life > 0
                    && BeastSourceGenerationCurrentLocked(conn, active.Source);
                if (!keep) RetireBeastCompanionLocked(conn, active, now,
                    output: ReferenceEquals(active.Area, conn.Instance) && ReferenceEquals(active.Lifetime, rules));
            }

            var pending = conn.Beasts.Pending;
            if (pending is not null && !BeastPendingCurrentLocked(conn, pending, rules))
            {
                conn.Beasts.Pending = null;
                pending = null;
            }
            if (pending is not null)
            {
                var owned = conn.Minions.All.Where(m => IsBeastCompanionKind(m.Kind)).ToArray();
                for (int i = pending.BeastLimit - 1; i < owned.Length; i++)
                    RetireOwnedMinionLocked(conn, owned[i], now);
            }
        }

        if (conn.Beasts.Pending is not { } summon || !Current()) return;
        if (Monsters.Find(summon.Variety) is not { Supported: true } variety)
        {
            lock (conn.Monsters)
                if (ReferenceEquals(conn.Beasts.Pending, summon)) conn.Beasts.Pending = null;
            Console.WriteLine($"[gateway] {remote} {summon.Skill}: {summon.Variety} cannot be created");
            return;
        }

        (int X, int Y) at;
        lock (conn.Monsters)
        {
            if (!Current() || !BeastPendingCurrentLocked(conn, summon, rules)) return;
            at = NextToPlayer(conn);
        }
        var mods = summon.MaximumLifeIncreasePct == 0
            ? MonsterModSet.None
            : MonsterModSet.None with { Effects = new(new Dictionary<string, int>
                { ["maximum_life_+%"] = summon.MaximumLifeIncreasePct }) };
        uint spawned = await SpawnMonster(remote, stream, ciphers, conn, variety, summon.MinionLevel,
            MonsterRarity.Normal, at.X, at.Y, friendly: true, mods: mods, extraStats: MinionStats,
            owned: (id, m) =>
            {
                if (!Current() || !BeastPendingCurrentLocked(conn, summon, rules))
                {
                    if (ReferenceEquals(conn.Beasts.Pending, summon)) conn.Beasts.Pending = null;
                    return;
                }
                conn.Beasts.Pending = null;
                m.Aggro = true;
                conn.Minions.Add(new(id, m, summon.Skill, summon.MinionKind)
                    { Until = double.PositiveInfinity, Fights = true });
                conn.Beasts.Active = new(id, m, summon.Source, summon.Skill, summon.Variety, summon.MinionKind,
                    summon.Lifetime, summon.Area);
            }, ownedCurrent: () => Current() && BeastPendingCurrentLocked(conn, summon, rules));
        if (Current())
        {
            lock (conn.Monsters)
                if (ReferenceEquals(conn.Beasts.Pending, summon)
                    && (spawned == 0 || !BeastPendingCurrentLocked(conn, summon, rules)))
                    conn.Beasts.Pending = null;
        }
        if (spawned != 0 && Current())
            Console.WriteLine($"[gateway] {remote} {summon.Skill} summoned {summon.MinionKind} " +
                $"0x{spawned:x} at level {summon.MinionLevel}");
    }

    static bool BeastPendingCurrentLocked(ClientState conn, BeastCompanions.PendingSummon pending, object? rules)
    {
        // The paid create may resume after equipment inventory outputs. Observe the live worn source again under the
        // Monsters lock so the retained generation map cannot admit a source that is already no longer equipped.
        ReconcileBeastCompanionsLocked(conn);
        return CurrentGameWork
            && !conn.Closed
            && !conn.LeavingArea
            && ReferenceEquals(conn.Beasts.Pending, pending)
            && ReferenceEquals(pending.Lifetime, rules)
            && ReferenceEquals(pending.Area, conn.Instance)
            && conn.PlayerLife > 0
            && (pending.ActionToken is not int action || PlayerActionCurrent(conn, action))
            && BeastSourceGenerationCurrentLocked(conn, pending.Source);
    }

    static void RetireBeastCompanionLocked(ClientState conn, BeastCompanions.ActiveBeast active, double now, bool output)
    {
        if (ReferenceEquals(conn.Beasts.Active, active)) conn.Beasts.Active = null;
        if (conn.Minions.All.FirstOrDefault(m => m.Id == active.Id
                && ReferenceEquals(m.Monster, active.Monster)) is { } owned)
            conn.Minions.Remove(owned.Id);
        RetireBeastMonsterLocked(conn, active.Id, active.Monster, now, output);
    }

    static void RetireOwnedMinionLocked(ClientState conn, PlayerMinions.Minion owned, double now, bool output = true)
    {
        var m = owned.Monster;
        bool current = conn.Monsters.TryGetValue(owned.Id, out var held) && ReferenceEquals(held, m);
        conn.Minions.Remove(owned.Id);
        if (!current) return;
        RetireBeastMonsterLocked(conn, owned.Id, m, now, output);
    }

    static void RetireBeastMonsterLocked(ClientState conn, uint id, SpawnedMonster m, double now, bool output)
    {
        m.Aggro = false;
        m.Dead = true;
        m.Life = 0;
        conn.Minions.Bodies.Add((id, m, now + 1));
        if (!output) return;
        conn.Minions.OutputQueue.Add(new(InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]),
            "beast companion ends", id, m));
        conn.Minions.OutputQueue.Add(new([], "beast companion life 0", id, m, PlayerMinions.OutputKind.Life));
    }
}
