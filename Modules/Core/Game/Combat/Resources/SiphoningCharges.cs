using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    const uint SiphoningGain = Stat.GainSiphoningChargeOnSkillUsePctChance;
    const uint SiphoningMaximum = Stat.MaximumSiphoningChargesPerElderOrShaperItemEquipped;
    const uint SiphoningDegen = Stat.PhysicalDamageTakenPerMinutePerSiphoningChargeIfHaveUsedASkillRecently;
    const uint SiphoningHitReduction = Stat.AdditionalPhysicalDamageReductionPctAgainstHits;
    public sealed record CarriedSiphoning(int Held, uint[] Sources, double Seconds, double UsedSeconds,
        double Pending, double TakenCarry, double BypassCarry);

    public static CarriedSiphoning CaptureSiphoning(Player conn, double at)
    {
        var state = SiphoningAt(conn, at);
        return new(state.Held, [.. state.Sources], Math.Max(0, state.Until - at),
            Math.Max(0, state.UsedUntil - at), state.Pending, state.TakenCarry, state.BypassCarry);
    }

    public static void RestoreSiphoning(Player conn, CarriedSiphoning value, double at)
    {
        var state = conn.Siphoning;
        state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch;
        state.Held = value.Held; state.Sources = [.. value.Sources];
        state.Until = at + value.Seconds; state.UsedUntil = at + value.UsedSeconds; state.At = at;
        state.Pending = value.Pending; state.TakenCarry = value.TakenCarry; state.BypassCarry = value.BypassCarry;
        state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Siphoning);
    }

    static void SettleSiphoning(Player conn, SiphoningState state, double now)
    {
        double end = Math.Min(now, Math.Min(state.Until, state.UsedUntil));
        if (end > state.At) state.Pending += (end - state.At) * state.Held * state.PerMinute / 60d;
        state.At = Math.Max(state.At, now);
        if (now >= state.Until && state.Held != 0) { state.Held = 0; state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Siphoning); }
        bool active = state.Held > 0 && now < state.Until && now < state.UsedUntil && state.PerMinute > 0;
        if (state.Degenerating != active) { state.Degenerating = active; state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Siphoning); }
    }

    static SiphoningState SiphoningAt(Player conn, double now, IReadOnlyDictionary<uint, int>? raw = null)
    {
        var state = conn.Siphoning;
        if (conn.ResourceEffectsState is ResourceEffectsState { Loading: true }) now = 0;
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch)
        {
            state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch; state.At = now;
            state.Held = state.Maximum = state.Gain = 0; state.Until = state.UsedUntil = 0;
            state.Pending = state.TakenCarry = state.BypassCarry = 0; state.Sources.Clear();
            state.Degenerating = false; state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Siphoning);
        }
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired
            || (conn.PlayerLife == 0 && conn.PlayerObjectCreated))
        {
            if (state.Held != 0 || state.Degenerating) { state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Siphoning); }
            state.Held = state.Maximum = state.Gain = 0; state.Sources.Clear();
            state.Pending = state.TakenCarry = state.BypassCarry = 0; state.At = now; state.Degenerating = false;
            return state;
        }
        var sources = conn.StatSourcesCache;
        var active = (sources?.Worn ?? []).Where(w => sources?.Inactive.Contains(w.Id) != true).ToArray();
        var hosts = active.Where(w => w.Item.Mods.Any(m => ItemsFor(conn).Mods.ByHash(m.Hash)?.Stats
            .Any(s => s.Id == SiphoningGain || s.Id == SiphoningMaximum) == true)).Select(w => w.Id).ToHashSet();
        // Only the Elder and Shaper items' mods grant Siphoning Charges: with none worn there is nothing to read, and the
        // stats cache's key (PlayerStatsKey → SiphoningRevision) no longer gathers every stat source on each lookup to
        // find that out (CPU sample 2026-11-18: half of all CharacterStatSources work in a Fireball fight).
        if (raw is null && hosts.Count == 0 && state.Sources.Count == 0)
        { state.At = Math.Max(state.At, now); return state; }
        raw ??= CharacterStatSources(conn).Stats;
        int Get(uint key) => raw.GetValueOrDefault(key);
        if (state.Sources.Count == 0 && Get(SiphoningGain) == 0 && Get(SiphoningMaximum) == 0)
        { state.At = Math.Max(state.At, now); return state; }
        if (!state.Sources.SetEquals(hosts))
        {
            state.Sources = hosts; state.Held = 0; state.Until = state.UsedUntil = 0;
            state.Pending = state.TakenCarry = state.BypassCarry = 0; state.At = now; state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Siphoning);
        }
        SettleSiphoning(conn, state, now);
        int count = active.Count(w => (w.Item.Influences & (ItemInfluence.Elder | ItemInfluence.Shaper)) != 0);
        state.Maximum = (int)Math.Clamp((long)Math.Max(0, Get(SiphoningMaximum)) * count, 0, ushort.MaxValue);
        state.Gain = Math.Max(0, Get(SiphoningGain));
        // This owner integrates the exact four-second use window, including its expiry inside a recovery step.
        // Read its coefficient before generic recent filtering, from the same qualified physical equipment.
        state.PerMinute = Math.Max(0, ItemModStats.Sum(active.Select(w => w.Item), ItemsFor(conn).Mods)
            .GetValueOrDefault(SiphoningDegen));
        if (state.Held > state.Maximum) { state.Held = state.Maximum; state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Siphoning); }
        SettleSiphoning(conn, state, now);
        return state;
    }

    public static void SiphoningSkillUsedLocked(Player conn, double at, Random? random = null)
    {
        var raw = CharacterStatSources(conn).Stats;
        var state = SiphoningAt(conn, at, raw);
        if (conn.Paused || InTown(conn) || state.Maximum <= 0 || state.Sources.Count == 0) return;
        // Install the existing transfer hook before any accepted charge can leave this area.
        ResourceEffectsOf(conn);
        state.UsedUntil = Math.Max(state.At, at) + RecentEvents.WindowSeconds;
        SettleSiphoning(conn, state, state.At);
        if (!RandomRolls.Chance(state.Gain, 100, random ?? Random.Shared)) return;
        int Get(uint key) => raw.GetValueOrDefault(key);
        double duration = Charges.BaseDurationMs / 1000d
            * Math.Max(0, 1 + ((long)Get(Stat.ChargeDurationPlusPct) + Get(Stat.BaseChargeDurationPlusPct)) / 100d);
        if (duration <= 0) return;
        state.Held = Math.Min(state.Maximum, state.Held + 1);
        state.Until = state.At + duration;
        state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Siphoning);
        SettleSiphoning(conn, state, state.At);
        Console.WriteLine($"[gateway] Siphoning charge {state.Held}/{state.Maximum} until={state.Until:0.000} t={at:0.000}");
    }

    public static long SiphoningRevision(Player conn)
    {
        lock (conn.Monsters) return SiphoningAt(conn, AreaTime(conn)).Revision;
    }

    public static bool SiphoningDegenerationActive(Player conn)
    {
        lock (conn.Monsters) return SiphoningAt(conn, AreaTime(conn)).Degenerating;
    }

    public static void ClearSiphoningOnDeath(Player conn)
    {
        var state = conn.Siphoning;
        state.Held = 0; state.Until = state.UsedUntil = 0;
        state.Pending = state.TakenCarry = state.BypassCarry = 0; state.Degenerating = false; state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Siphoning);
    }

    public static IReadOnlyList<(uint Key, int Value)> SiphoningRawStatsLocked(Player conn,
        IReadOnlyDictionary<uint, int> raw)
    {
        var state = SiphoningAt(conn, AreaTime(conn), raw);
        if (state.Sources.Count == 0 && state.Held == 0 && state.Maximum == 0) return [];
        int Get(uint key) => raw.GetValueOrDefault(key);
        var rows = new List<(uint, int)>();
        void Add(uint n, long v) { if (n is var key) rows.Add((key, ActorScale.Saturate(v))); }
        Add(Stat.CurrentSiphoningCharges, state.Held);
        Add(Stat.VirtualMaximumSiphoningCharges, state.Maximum);
        foreach (string end in new[] { "minimum", "maximum" })
        {
            long value = (long)state.Held * Get(Stat.Of($"attack_and_spell_{end}_added_physical_damage_per_siphoning_charge"));
            Add(Stat.Of($"attack_{end}_added_physical_damage"), value);
            Add(Stat.Of($"spell_{end}_added_physical_damage"), value);
        }
        Add(Stat.LifeLeechFromAnyDamagePermyriad, (long)state.Held * Get(Stat.LifeLeechFromAnyDamagePermyriadPerSiphoningCharge));
        Add(Stat.NonChaosDamageToAddAsChaosDamagePct, (long)state.Held * Get(Stat.NonChaosDamagePctToAddAsChaosDamagePerSiphoningCharge));
        Add(SiphoningHitReduction, (long)state.Held * Get(Stat.AdditionalPhysicalDamageReductionAgainstHitsPctPerSiphoningCharge));
        return rows;
    }

    public static int AdvanceSiphoningDamageLocked(Player conn, double now, IReadOnlyDictionary<uint, int> stats)
    {
        var state = SiphoningAt(conn, now);
        double raw = state.Pending; state.Pending = 0;
        if (raw <= 0 || InGracePeriod(conn) || InTown(conn) || conn.Immune || HardcoreProgressBlocked(conn)) return 0;
        int Get(uint key) => stats.GetValueOrDefault(key);
        if (DamageImmunities.From(Get).Contains(DamageType.Physical)) return 0;
        int damage = IncomingResourceDefences.TakenOverTime(raw, Get, ref state.TakenCarry,
            IncomingResourceDefences.LessTakenPct(conn.Pools, Get),
            typedIncreasedPct: Get(Stat.PhysicalDamageTakenPlusPct), reductionPct: ChargeEffects.Reduction(Get).Physical,
            finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        int life = IncomingResourceDefences.DamageOverTime(conn.EnergyShield, damage,
            Get(IncomingResourceDefences.BypassStat), now, ref state.BypassCarry);
        conn.PlayerLife = (uint)Math.Max(0, (long)conn.PlayerLife - life);
        if (conn.PlayerLife == 0) { conn.InvalidateIncomingHits(); RecordHardcoreDeath(conn); }
        return life;
    }

    static async Task PresentSiphoningCharges(Player conn)
    {
        lock (conn.Monsters)
            if (conn.Siphoning.Sources.Count == 0 && conn.Siphoning.Buff == 0) return;
        bool changed = false;
        await SendWorldRecord(conn, PlayerId, [], "Siphoning charge state", atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea) return null;
                var state = SiphoningAt(conn, AreaTime(conn));
                bool sameBuff = state.Presented == state.Held && state.PresentedUntil == state.Until;
                if (sameBuff && state.PresentedDegenerating == state.Degenerating) return [];
                var packets = new List<byte[]>();
                if (!sameBuff)
                {
                    if (state.Buff != 0) packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.Buff));
                    state.Buff = state.Held > 0 ? conn.NextBuff++ : (ushort)0;
                    if (state.Buff != 0) packets.Add(InstanceEntities.AddBuff(PlayerId, state.Buff, 32, PlayerId,
                        stacks: (ushort)state.Held,
                        u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, state.Until - AreaTime(conn)))));
                }
                state.Presented = state.Held; state.PresentedUntil = state.Until;
                state.PresentedDegenerating = state.Degenerating;
                changed = true;
                return [.. packets.SelectMany(p => p)];
            }
        });
        if (changed) await SendChargeStats(conn, "Siphoning charges");
    }
}
