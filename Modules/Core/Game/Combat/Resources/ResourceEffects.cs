using System.Collections.Concurrent;
using GameServer.Modding;

/// <summary>Rule-owned resources, separate from the animated-actor runtime and its worker.</summary>
sealed class ResourceEffectsState(IModuleLifetime? rules, IModuleLifetime? source, ClientState? connection = null) : ITransferCombatState
{
    internal IModuleLifetime? Rules { get; } = rules;
    internal IModuleLifetime? Source { get; } = source;
    internal long Generation;
    internal readonly ManaRecoupState Mana = new();
    internal readonly MomentumState Momentum = new();
    internal readonly TimedBlind PlayerBlind = new();
    internal readonly Dictionary<uint, (SpawnedMonster Target, TimedBlind Blind)> Targets = [];
    readonly HashSet<(uint Actor, ushort Buff)> retired = [];
    internal sealed record Snapshot(ManaRecoupState.Carried Mana, (double Seconds, int Less)[] Blind);
    internal Snapshot? TransferCheckpoint;
    internal bool Loading;
    public IReadOnlyCollection<(uint Actor, ushort Buff)> RetiredBuffs => retired;

    internal TimedBlind TargetBlind(uint id, SpawnedMonster target)
    {
        if (Targets.TryGetValue(id, out var old) && ReferenceEquals(old.Target, target)) return old.Blind;
        if (old.Blind is { Buff: > 0 } blind) retired.Add((id, blind.Buff));
        var value = new TimedBlind(); Targets[id] = (target, value); return value;
    }
    internal Snapshot Capture(double at) => new(Mana.Capture(at), PlayerBlind.Capture(at));
    internal void CollectRetiredTargets(ClientState conn)
    {
        foreach (var (id, value) in Targets.ToArray())
            if (!ReferenceEquals(conn.Monsters.GetValueOrDefault(id), value.Target))
            {
                if (value.Blind.Buff != 0) retired.Add((id, value.Blind.Buff));
                Targets.Remove(id);
            }
        conn.RetiredResourceBuffs.UnionWith(retired); retired.Clear();
    }
    public void Freeze(double at) => TransferCheckpoint = Capture(at);
    public void Resume(double at)
    {
        // A refused transfer resumes the live draft, discarding loading earnings rather than paying catch-up.
        Mana.Take(at); Mana.DiscardRecoveryFraction(); PlayerBlind.Read(at); TransferCheckpoint = null;
    }
    internal void Restore(Snapshot value, double at)
    { Mana.Restore(value.Mana, at); PlayerBlind.Restore(value.Blind, at); }
    public void Clear()
    {
        if (connection is not null)
        {
            if (Momentum.Buff != 0) connection.RetiredSkillBuffs.Add(Momentum.Buff);
            if (Momentum.SwiftBuff != 0) connection.RetiredSkillBuffs.Add(Momentum.SwiftBuff);
        }
        Momentum.Clear();
        if (PlayerBlind.Buff != 0) retired.Add((Program.PlayerId, PlayerBlind.Buff));
        foreach (var (id, value) in Targets)
            if (value.Blind.Buff != 0) retired.Add((id, value.Blind.Buff));
        // Accepted flask records contain only Gateway data. Game retirement hands them to the next writer;
        // explicit/source/death/connection retirement keeps the existing copied-effect cleanup policy.
        if (connection is not null && (Rules is not { IsActive: false } || Source is { IsActive: false }
            || connection.Closed || connection.RageRetired))
            foreach (var effect in connection.FlaskEffects.End(e => e.CopyManaToLife))
                if (effect.Buff != 0) retired.Add((Program.PlayerId, effect.Buff));
        Mana.Clear(); PlayerBlind.Clear(); Targets.Clear(); TransferCheckpoint = null;
    }
}

static partial class Game
{
    static readonly ResourceEffectsState RetiredResourceEffects = new(null, null);
    internal static ResourceEffectsState ResourceEffectsOf(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameRules || conn.Closed || conn.RageRetired || conn.PlayerLife == 0) return RetiredResourceEffects;
            var owner = CombatLifetime!;
            var source = AscendancyPolicies.SourceOwner(conn);
            if (conn.ResourceEffectsState is ResourceEffectsState state && ReferenceEquals(state.Rules, owner)
                && ReferenceEquals(state.Source, source) && conn.ResourceEffectsLease.Current(owner, source, state, state.Generation))
                return state;
            conn.RetireResourceEffects();
            state = new(owner, source, conn); conn.ResourceEffectsState = state;
            if (conn.ResourceEffectsLease.Bind(conn, owner, source, state, out long generation))
            { state.Generation = generation; return state; }
            conn.RetireResourceEffects(); return RetiredResourceEffects;
        }
    }
    internal static bool ResourceEffectsCurrent(ClientState conn, ResourceEffectsState state) => CurrentGameRules
        && !conn.Closed && !conn.RageRetired && ReferenceEquals(conn.ResourceEffectsState, state)
        && ReferenceEquals(state.Source, AscendancyPolicies.SourceOwner(conn)) && state.Rules is { IsActive: true } owner
        && conn.ResourceEffectsLease.Current(owner, state.Source, state, state.Generation);

    internal static double ResourceEffectsTime(ClientState conn)
    {
        if (conn.ResourceEffectsState is ResourceEffectsState { Loading: true }) return 0;
        return conn.LeavingArea && conn.RampageTransferAt is double at ? at
            : conn.ProjectileRuntimeState is { Active: true } runtime ? runtime.NowTicks / 120000d : conn.Clock.Elapsed.TotalSeconds;
    }
    internal static void ManaSpentLocked(ClientState conn, uint amount, IReadOnlyDictionary<uint, int> player)
    {
        if (amount == 0 || conn.LeavingArea || conn.PlayerLife == 0 || !CurrentGameRules) return;
        var state = ResourceEffectsOf(conn);
        if (!ResourceEffectsCurrent(conn, state)) return;
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        long gain = state.Mana.Spent(amount, Get(ManaRecoveryRules.RagePer200));
        if (gain > 0) conn.Rage.Gain(gain, ResourceEffectsTime(conn), RageStats.Inputs(Get), Get("has_soul_gain_prevention") != 0);
    }
    internal static void SettleFullManaLocked(ClientState conn, double at)
    {
        if (conn.ResourceEffectsState is ResourceEffectsState state && ResourceEffectsCurrent(conn, state)
            && conn.PlayerMana >= UnreservedMana(conn))
        { state.Mana.Take(at); state.Mana.DiscardRecoveryFraction(); }
    }
    internal static void DiscardResourceLoadingRecovery(ClientState conn, double now)
    {
        if (conn.ResourceEffectsState is ResourceEffectsState state && ResourceEffectsCurrent(conn, state))
        { state.Mana.Take(now); state.Mana.DiscardRecoveryFraction(); }
    }
    internal static bool CompleteResourceRecoveryLocked(ClientState conn, double from, double now,
        IReadOnlyDictionary<uint, int> stats, double earnedMana, uint manaBefore, uint manaCap, ref double manaCarry)
    {
        AdvancePlayerAilmentsLocked(conn, from, now, stats);
        if (conn.PlayerLife == 0)
        {
            manaCarry = 0; LeechOf(conn).Clear(now); FortificationOf(conn).Clear();
            conn.EnergyShield.Empty(); ClearAntiquarianOnDeath(conn); return false;
        }
        var state = ResourceEffectsOf(conn);
        double recoup = ResourceEffectsCurrent(conn, state) ? state.Mana.Take(now) : 0;
        conn.PlayerMana = RecoveryPool.Advance(manaBefore, manaCap, earnedMana + recoup, ref manaCarry);
        if (conn.PlayerMana >= manaCap && ResourceEffectsCurrent(conn, state)) state.Mana.DiscardRecoveryFraction();
        return true;
    }

    sealed record ResourceTransfer(string Character, DateTime At, IModuleLifetime Rules, IModuleLifetime? Source,
        ResourceEffectsState.Snapshot State);
    static readonly ConcurrentDictionary<uint, ResourceTransfer> CarriedResourceEffects = new();
    internal static void CarryResourceEffects(ClientState conn, uint ticket)
    {
        lock (conn.Monsters)
        {
            if (conn.PlayerLife == 0 || conn.Closed || conn.RageRetired
                || conn.ResourceEffectsState is not ResourceEffectsState state || !ResourceEffectsCurrent(conn, state)) return;
            foreach (var (key, entry) in CarriedResourceEffects)
                if (!entry.Rules.IsActive || entry.Source is { IsActive: false }
                    || DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedResourceEffects.TryRemove(key, out _);
            var checkpoint = state.TransferCheckpoint ?? state.Capture(ResourceEffectsTime(conn));
            CarriedResourceEffects.TryAdd(ticket, new(CharacterName(conn), DateTime.UtcNow, state.Rules!, state.Source, checkpoint));
            state.Clear();
        }
    }
    internal static void RestoreResourceEffects(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (!CarriedResourceEffects.TryGetValue(conn.Ticket, out var entry) || entry.Character != CharacterName(conn)) return;
            if (!CarriedResourceEffects.TryRemove(new KeyValuePair<uint, ResourceTransfer>(conn.Ticket, entry))) return;
            if (conn.PlayerLife == 0 || !ReferenceEquals(entry.Rules, CombatLifetime) || !entry.Rules.IsActive
                || !ReferenceEquals(entry.Source, AscendancyPolicies.SourceOwner(conn)) || entry.Source is { IsActive: false }
                || DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) return;
            var state = ResourceEffectsOf(conn);
            if (!ResourceEffectsCurrent(conn, state)) return;
            // Initial player publication reads restored state at zero; loading cannot consume its remaining duration.
            state.Restore(entry.State, 0); state.Loading = true;
        }
    }
    internal static void ActivateResourceEffects(ClientState conn, double at)
    {
        lock (conn.Monsters)
            if (conn.ResourceEffectsState is ResourceEffectsState { Loading: true } state && ResourceEffectsCurrent(conn, state))
            { state.Restore(state.Capture(0), at); state.Loading = false; }
    }
}
