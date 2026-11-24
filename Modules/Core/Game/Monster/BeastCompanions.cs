using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public const string SummonBeastialUrsaEffect = "SummonBeastialUrsa";
    public const string SummonBeastialRhoaEffect = "SummonBeastialRhoa";
    public const string SummonBeastialSnakeEffect = "SummonBeastialSnake";

    public sealed record BeastKind(string EffectId, string Variety, string MinionKind);

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

    public static BeastKind? BeastKindFor(string? effectId) =>
        effectId is not null && BeastKindsByEffect.TryGetValue(effectId, out var kind) ? kind : null;

    public static bool IsSummonBeastialCompanionEffect(string? effectId) => BeastKindFor(effectId) is not null;

    static bool IsBeastCompanionKind(string kind) =>
        BeastKindsByEffect.Values.Any(candidate => candidate.MinionKind == kind);

    static WornGrantedSkills.Grant BeastSourceIdentity(WornGrantedSkills.Grant grant) => grant;

    static bool BeastSourceEligible(Player player, WornGrantedSkills.Grant grant) =>
        CurrentGameWork
        && grant.Host != 0
        && IsSummonBeastialCompanionEffect(grant.EffectId)
        && LeagueFor(player).Rules.AllowsGemEffect(grant.EffectId)
        && !IsInactive(player, grant.Host);

    sealed class BeastCheck
    {
        public long Mutations = -1;
        public object? StatSources;
    }
    static readonly Store<Player, BeastCheck> BeastCheckedMutations = new();

    /// <summary>
    /// Reconciles the live worn grants without building CharacterPlayerStats or SupportedSkill. A missing eligible grant
    /// retires its generation irrevocably and drops pending/active ownership in the same observation.
    /// </summary>
    public static void ReconcileBeastCompanionsLocked(Player player)
    {
        var items = ItemsOf(player);
        long invMutations = items.Mutations;
        var statSources = player.StatSourcesCache;
        var check = BeastCheckedMutations.GetOrCreateValue(player);
        if (player.Beasts.Active is null && player.Beasts.Pending is null && player.Beasts.SourceGenerations.Count == 0
            && check.Mutations == invMutations && ReferenceEquals(check.StatSources, statSources))
            return;
        var live = AllWornGrantedSkillsOf(player).Where(g => BeastSourceEligible(player, g)).ToArray();
        var identities = live.Select(BeastSourceIdentity).ToHashSet();
        foreach (var stale in player.Beasts.SourceGenerations.Where(s => !identities.Contains(s.Key)).ToArray())
        {
            stale.Value.Retired = true;
            player.Beasts.SourceGenerations.Remove(stale.Key);
        }
        foreach (var grant in live)
        {
            var identity = BeastSourceIdentity(grant);
            if (!player.Beasts.SourceGenerations.TryGetValue(identity, out var owner) || owner.Retired)
                player.Beasts.SourceGenerations[identity] = new(grant);
        }

        if (player.Beasts.Active is { } active
            && (!BeastSourceGenerationCurrentLocked(player, active.Source)
                || !player.Minions.Owns(active.Id, active.Monster)
                || !player.Area.Monsters.TryGetValue(active.Id, out var held)
                || !ReferenceEquals(held, active.Monster)))
            RetireBeastCompanionLocked(player, active, AreaTime(player),
                output: ReferenceEquals(active.Area, player.Instance) && ReferenceEquals(active.Lifetime, CombatLifetime));
        if (player.Beasts.Pending is { } pending && !BeastSourceGenerationCurrentLocked(player, pending.Source))
            player.Beasts.Pending = null;
        if (player.Beasts.Active is null && player.Beasts.Pending is null && player.Beasts.SourceGenerations.Count == 0)
        {
            check.Mutations = invMutations;
            check.StatSources = statSources;
        }
    }

    static BeastCompanions.SourceGeneration? CurrentBeastSourceLocked(Player player, WornGrantedSkills.Grant grant)
    {
        ReconcileBeastCompanionsLocked(player);
        return player.Beasts.SourceGenerations.TryGetValue(BeastSourceIdentity(grant), out var owner)
            && !owner.Retired ? owner : null;
    }

    static bool BeastSourceGenerationCurrentLocked(Player player, BeastCompanions.Source source)
    {
        if (!CurrentGameWork || source.Owner.Retired) return false;
        return player.Beasts.SourceGenerations.TryGetValue(BeastSourceIdentity(source.Grant), out var owner)
            && ReferenceEquals(owner, source.Owner);
    }

    /// <summary>The actual worn source generation remains current, without rebuilding player stats.</summary>
    static bool BeastSourceCurrent(Player player, BeastCompanions.Source source)
    {
        ReconcileBeastCompanionsLocked(player);
        return BeastSourceGenerationCurrentLocked(player, source);
    }

    static bool BeastGrantCurrent(Player player, WornGrantedSkills.Grant grant)
    {
        return CurrentBeastSourceLocked(player, grant) is not null;
    }

    public static bool HasSourceValidBeastMinion(Player player)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0
            || player.Beasts.Active is not { } active) return false;
        ReconcileBeastCompanionsLocked(player);
        return ReferenceEquals(player.Beasts.Active, active)
            && ReferenceEquals(active.Lifetime, CombatLifetime)
            && ReferenceEquals(active.Area, player.Instance)
            && player.Minions.Owns(active.Id, active.Monster)
            && player.Area.Monsters.TryGetValue(active.Id, out var held)
            && ReferenceEquals(held, active.Monster)
            && !active.Monster.Dead && active.Monster.Life > 0
            && BeastSourceGenerationCurrentLocked(player, active.Source);
    }

    /// <summary>Rejects a stale worn-host activation before cooldown or resource payment.</summary>
    public static bool BeastCompanionAdmissionCurrent(Player player, SupportedGem gem, ushort skillId) =>
        gem.WornGrant is { } grant
        && gem.RequirementsMet
        && BeastGrantCurrent(player, grant)
        && GemSkillInstanceDisabled(player, gem.Effect, skillId) is null;

    /// <summary>Captures accepted work under the same Monsters lock that paid for it.</summary>
    public static void QueueBeastCompanionLocked(Player player, SupportedGem gem, ushort skillId, int? actionToken)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0 || InTown(player)
            || gem.WornGrant is not { } grant
            || BeastKindFor(grant.EffectId) is not { } kind
            || CurrentBeastSourceLocked(player, grant) is not { } owner) return;
        int Stat(uint key) => gem.Stats.GetValueOrDefault(key);
        int level = Stat(global::Stat.BaseDisplayMinionActorLevel);
        int limit = Stat(global::Stat.BaseNumberOfBeastMinionsAllowed);
        if (level <= 0 || limit <= 0) return;
        player.Beasts.Pending = new(new(owner), skillId, grant.EffectId, kind.Variety, kind.MinionKind,
            (byte)Math.Clamp(level, 1, 100),
            Stat(global::Stat.MinionMaximumLifePlusPct), limit, CombatLifetime, player.Instance, actionToken)
            { SkillStats = new Dictionary<uint, int>(gem.Stats) };
    }

    public static async Task TickBeastCompanions(Player player, double now, object? rules)
    {
        bool Current() => CurrentGameWork && ReferenceEquals(CombatLifetime, rules);
        if (!Current() || player.Closed || player.LeavingArea) return;
        ReconcileBeastCompanionsLocked(player);
        if (player.Beasts.Active is null && player.Beasts.Pending is null && player.Beasts.SourceGenerations.Count == 0)
            return;
        if (player.Beasts.Active is { } active)
        {
            bool present = player.Minions.Owns(active.Id, active.Monster)
                && ReferenceEquals(active.Area, player.Instance) && ReferenceEquals(active.Lifetime, rules)
                && player.Area.Monsters.TryGetValue(active.Id, out var held) && ReferenceEquals(held, active.Monster);
            bool keep = !InTown(player) && present && player.PlayerLife > 0 && !active.Monster.Dead && active.Monster.Life > 0
                && BeastSourceGenerationCurrentLocked(player, active.Source);
            if (!keep) RetireBeastCompanionLocked(player, active, now,
                output: ReferenceEquals(active.Area, player.Instance) && ReferenceEquals(active.Lifetime, rules));
        }

        if (InTown(player))
        {
            player.Beasts.Pending = null;
            return;
        }
        var pending = player.Beasts.Pending;
        if (pending is not null && !BeastPendingCurrentLocked(player, pending, rules))
        {
            player.Beasts.Pending = null;
            pending = null;
        }
        if (pending is not null)
        {
            var owned = player.Minions.All.Where(m => IsBeastCompanionKind(m.Kind)).ToArray();
            for (int i = pending.BeastLimit - 1; i < owned.Length; i++)
                RetireOwnedMinionLocked(player, owned[i], now);
        }

        if (player.Beasts.Pending is not { } summon || !Current()) return;
        if (Monsters.Find(summon.Variety) is not { Supported: true } variety)
        {
            if (ReferenceEquals(player.Beasts.Pending, summon)) player.Beasts.Pending = null;
            Console.WriteLine($"[gateway] {player.Connection.Remote} {summon.Skill}: {summon.Variety} cannot be created");
            return;
        }

        (int X, int Y) at;
        if (!Current() || !BeastPendingCurrentLocked(player, summon, rules)) return;
        at = NextToPlayer(player);
        var native = MonsterModRolls.Create(
            MonsterModRolls.Species(variety.SpeciesModsOrEmpty, AreaRng(player)), [], MonsterRarity.Normal, summon.MinionLevel);
        var prepared = BeastCombatInputs(summon.Source, summon.SkillId, summon.MinionKind, summon.Variety,
            summon.SkillStats, native.Effects);
        var mods = GolemModifiers(player, prepared, CharacterPlayerStats(player), AreaTime(player));
        mods = mods with { Mods = native.Mods };
        uint spawned = await SpawnMonster(player, variety, summon.MinionLevel,
            MonsterRarity.Normal, at.X, at.Y, friendly: true, mods: mods, extraStats: MinionStats,
            owned: (id, m) =>
            {
                if (!Current() || !BeastPendingCurrentLocked(player, summon, rules))
                {
                    if (ReferenceEquals(player.Beasts.Pending, summon)) player.Beasts.Pending = null;
                    return;
                }
                player.Beasts.Pending = null;
                m.Aggro = true;
                player.Minions.Add(new(id, m, summon.Skill, summon.MinionKind)
                    { Until = double.PositiveInfinity, Fights = true });
                player.Beasts.Active = new(id, m, summon.Source, summon.Skill, summon.Variety, summon.MinionKind,
                    summon.Lifetime, summon.Area)
                    { SourceSkill = summon.SkillId, SkillStats = summon.SkillStats, Intrinsic = native.Effects };
            }, ownedCurrent: () => Current() && BeastPendingCurrentLocked(player, summon, rules) && CanHaveOtherMinions(player));
        if (Current())
        {
            if (ReferenceEquals(player.Beasts.Pending, summon)
                && (spawned == 0 || !BeastPendingCurrentLocked(player, summon, rules)))
                player.Beasts.Pending = null;
        }
        if (spawned != 0 && Current())
            Console.WriteLine($"[gateway] {player.Connection.Remote} {summon.Skill} summoned {summon.MinionKind} " +
                $"0x{spawned:x} at level {summon.MinionLevel}");
    }

    static GolemState.Golem BeastCombatInputs(BeastCompanions.Source source, ushort skillId,
        string kind, string variety, IReadOnlyDictionary<uint, int> stats, MonsterModEffects intrinsic,
        uint id = 0, SpawnedMonster? monster = null) =>
        new(id, monster!, new(source.Effect, source.EffectId, kind, variety, 0, [], false, false, IsGolem: false), skillId)
        {
            Own = new(stats), Gear = new(intrinsic.Stats), GemLevel = source.Level,
        };

    static GolemState.Ability? BeastSupportedAttack(Player player, uint id, SpawnedMonster monster,
        MonsterAttack attack, double now)
    {
        if (player.Beasts.Active is not { } active || active.Id != id || !ReferenceEquals(active.Monster, monster)
            || monster.Variety is not { } variety) return null;
        var combat = BeastCombatInputs(active.Source, active.SourceSkill, active.MinionKind,
            active.Variety, active.SkillStats, active.Intrinsic, id, monster);
        var abilities = GolemAbilities(player, variety, monster.Level, combat, CharacterPlayerStats(player), now);
        return abilities.FirstOrDefault(a => a.Skill == attack.SkillId)
            ?? abilities.FirstOrDefault(a => a.Attack.Spell == attack.Spell);
    }

    static bool BeastPendingCurrentLocked(Player player, BeastCompanions.PendingSummon pending, object? rules)
    {
        // The paid create may resume after equipment inventory outputs. Observe the live worn source again under the
        // Monsters lock so the retained generation map cannot admit a source that is already no longer equipped.
        ReconcileBeastCompanionsLocked(player);
        return CurrentGameWork
            && !player.Closed
            && !player.LeavingArea
            && ReferenceEquals(player.Beasts.Pending, pending)
            && ReferenceEquals(pending.Lifetime, rules)
            && ReferenceEquals(pending.Area, player.Instance)
            && player.PlayerLife > 0
            && (pending.ActionToken is not int action || PlayerActionCurrent(player, action))
            && BeastSourceGenerationCurrentLocked(player, pending.Source);
    }

    static void RetireBeastCompanionLocked(Player player, BeastCompanions.ActiveBeast active, double now, bool output)
    {
        if (ReferenceEquals(player.Beasts.Active, active)) player.Beasts.Active = null;
        if (player.Minions.All.FirstOrDefault(m => m.Id == active.Id
                && ReferenceEquals(m.Monster, active.Monster)) is { } owned)
            player.Minions.Remove(owned.Id);
        RetireBeastMonsterLocked(player, active.Id, active.Monster, now, output);
    }

    static void RetireOwnedMinionLocked(Player player, PlayerMinions.Minion owned, double now, bool output = true)
    {
        var m = owned.Monster;
        bool current = player.Area.Monsters.TryGetValue(owned.Id, out var held) && ReferenceEquals(held, m);
        player.Minions.Remove(owned.Id);
        if (!current) return;
        RetireBeastMonsterLocked(player, owned.Id, m, now, output);
    }

    static void RetireBeastMonsterLocked(Player player, uint id, SpawnedMonster m, double now, bool output)
    {
        m.Aggro = false;
        m.Dead = true;
        m.Life = 0;
        player.Minions.Bodies.Add((id, m, now + 1));
        if (!output) return;
        SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
        player.Minions.OutputQueue.Add(new([], "beast companion life 0", id, m, PlayerMinions.OutputKind.Life));
    }
}
