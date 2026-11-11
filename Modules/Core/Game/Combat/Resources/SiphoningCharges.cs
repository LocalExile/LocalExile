using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    const string SiphoningGain = "gain_siphoning_charge_on_skill_use_%_chance";
    const string SiphoningMaximum = "maximum_siphoning_charges_per_elder_or_shaper_item_equipped";
    const string SiphoningDegen = "physical_damage_taken_per_minute_per_siphoning_charge_if_have_used_a_skill_recently";
    const string SiphoningHitReduction = "additional_physical_damage_reduction_%_against_hits";
    public sealed record CarriedSiphoning(int Held, uint[] Sources, double Seconds, double UsedSeconds,
        double Pending, double TakenCarry, double BypassCarry);

    public static CarriedSiphoning CaptureSiphoning(ClientState conn, double at)
    {
        var state = SiphoningAt(conn, at);
        return new(state.Held, [.. state.Sources], Math.Max(0, state.Until - at),
            Math.Max(0, state.UsedUntil - at), state.Pending, state.TakenCarry, state.BypassCarry);
    }

    public static void RestoreSiphoning(ClientState conn, CarriedSiphoning value, double at)
    {
        var state = conn.Siphoning;
        state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch;
        state.Held = value.Held; state.Sources = [.. value.Sources];
        state.Until = at + value.Seconds; state.UsedUntil = at + value.UsedSeconds; state.At = at;
        state.Pending = value.Pending; state.TakenCarry = value.TakenCarry; state.BypassCarry = value.BypassCarry;
        state.Revision++;
    }

    static void SettleSiphoning(SiphoningState state, double now)
    {
        double end = Math.Min(now, Math.Min(state.Until, state.UsedUntil));
        if (end > state.At) state.Pending += (end - state.At) * state.Held * state.PerMinute / 60d;
        state.At = Math.Max(state.At, now);
        if (now >= state.Until && state.Held != 0) { state.Held = 0; state.Revision++; }
        bool active = state.Held > 0 && now < state.Until && now < state.UsedUntil && state.PerMinute > 0;
        if (state.Degenerating != active) { state.Degenerating = active; state.Revision++; }
    }

    static SiphoningState SiphoningAt(ClientState conn, double now, IReadOnlyDictionary<uint, int>? raw = null)
    {
        var state = conn.Siphoning;
        if (conn.ResourceEffectsState is ResourceEffectsState { Loading: true }) now = 0;
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch)
        {
            state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch; state.At = now;
            state.Held = state.Maximum = state.Gain = 0; state.Until = state.UsedUntil = 0;
            state.Pending = state.TakenCarry = state.BypassCarry = 0; state.Sources.Clear();
            state.Degenerating = false; state.Revision++;
        }
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.RageRetired
            || (conn.PlayerLife == 0 && conn.PlayerObjectCreated))
        {
            if (state.Held != 0 || state.Degenerating) state.Revision++;
            state.Held = state.Maximum = state.Gain = 0; state.Sources.Clear();
            state.Pending = state.TakenCarry = state.BypassCarry = 0; state.At = now; state.Degenerating = false;
            return state;
        }
        raw ??= CharacterStatSources(conn).Stats;
        int Get(string n) => Stats.Key(n) is uint key ? raw.GetValueOrDefault(key) : 0;
        if (state.Sources.Count == 0 && Get(SiphoningGain) == 0 && Get(SiphoningMaximum) == 0)
        { state.At = Math.Max(state.At, now); return state; }
        var sources = conn.StatSourcesCache;
        var active = (sources?.Worn ?? []).Where(w => sources?.Inactive.Contains(w.Id) != true).ToArray();
        var hosts = active.Where(w => w.Item.Mods.Any(m => ItemsFor(conn).Mods.ByHash(m.Hash)?.Stats
            .Any(s => s.Id == SiphoningGain || s.Id == SiphoningMaximum) == true)).Select(w => w.Id).ToHashSet();
        if (!state.Sources.SetEquals(hosts))
        {
            state.Sources = hosts; state.Held = 0; state.Until = state.UsedUntil = 0;
            state.Pending = state.TakenCarry = state.BypassCarry = 0; state.At = now; state.Revision++;
        }
        SettleSiphoning(state, now);
        int count = active.Count(w => (w.Item.Influences & (ItemInfluence.Elder | ItemInfluence.Shaper)) != 0);
        state.Maximum = (int)Math.Clamp((long)Math.Max(0, Get(SiphoningMaximum)) * count, 0, ushort.MaxValue);
        state.Gain = Math.Max(0, Get(SiphoningGain));
        // This owner integrates the exact four-second use window, including its expiry inside a recovery step.
        // Read its coefficient before generic recent filtering, from the same qualified physical equipment.
        state.PerMinute = Math.Max(0, ItemModStats.Sum(active.Select(w => w.Item), ItemsFor(conn).Mods)
            .GetValueOrDefault(SiphoningDegen));
        if (state.Held > state.Maximum) { state.Held = state.Maximum; state.Revision++; }
        SettleSiphoning(state, now);
        return state;
    }

    public static void SiphoningSkillUsedLocked(ClientState conn, double at, Random? random = null)
    {
        var raw = CharacterStatSources(conn).Stats;
        var state = SiphoningAt(conn, at, raw);
        if (conn.Paused || InTown(conn) || state.Maximum <= 0 || state.Sources.Count == 0) return;
        // Install the existing transfer hook before any accepted charge can leave this area.
        ResourceEffectsOf(conn);
        state.UsedUntil = Math.Max(state.At, at) + RecentEvents.WindowSeconds;
        SettleSiphoning(state, state.At);
        if (!RandomRolls.Chance(state.Gain, 100, random ?? Random.Shared)) return;
        int Get(string n) => Stats.Key(n) is uint key ? raw.GetValueOrDefault(key) : 0;
        double duration = Charges.BaseDurationMs / 1000d
            * Math.Max(0, 1 + ((long)Get("charge_duration_+%") + Get("base_charge_duration_+%")) / 100d);
        if (duration <= 0) return;
        state.Held = Math.Min(state.Maximum, state.Held + 1);
        state.Until = state.At + duration;
        state.Revision++;
        SettleSiphoning(state, state.At);
        Console.WriteLine($"[gateway] Siphoning charge {state.Held}/{state.Maximum} until={state.Until:0.000} t={at:0.000}");
    }

    public static long SiphoningRevision(ClientState conn)
    {
        lock (conn.Monsters) return SiphoningAt(conn, AreaTime(conn)).Revision;
    }

    public static bool SiphoningDegenerationActive(ClientState conn)
    {
        lock (conn.Monsters) return SiphoningAt(conn, AreaTime(conn)).Degenerating;
    }

    public static void ClearSiphoningOnDeath(ClientState conn)
    {
        var state = conn.Siphoning;
        state.Held = 0; state.Until = state.UsedUntil = 0;
        state.Pending = state.TakenCarry = state.BypassCarry = 0; state.Degenerating = false; state.Revision++;
    }

    public static IReadOnlyList<(uint Key, int Value)> SiphoningRawStatsLocked(ClientState conn,
        IReadOnlyDictionary<uint, int> raw)
    {
        var state = SiphoningAt(conn, AreaTime(conn), raw);
        if (state.Sources.Count == 0 && state.Held == 0 && state.Maximum == 0) return [];
        int Get(string n) => Stats.Key(n) is uint key ? raw.GetValueOrDefault(key) : 0;
        var rows = new List<(uint, int)>();
        void Add(string n, long v) { if (Stats.Key(n) is uint key) rows.Add((key, ActorScale.Saturate(v))); }
        Add("current_siphoning_charges", state.Held);
        Add("virtual_maximum_siphoning_charges", state.Maximum);
        foreach (string end in new[] { "minimum", "maximum" })
        {
            long value = (long)state.Held * Get($"attack_and_spell_{end}_added_physical_damage_per_siphoning_charge");
            Add($"attack_{end}_added_physical_damage", value);
            Add($"spell_{end}_added_physical_damage", value);
        }
        Add("life_leech_from_any_damage_permyriad", (long)state.Held * Get("life_leech_from_any_damage_permyriad_per_siphoning_charge"));
        Add("non_chaos_damage_to_add_as_chaos_damage_%", (long)state.Held * Get("non_chaos_damage_%_to_add_as_chaos_damage_per_siphoning_charge"));
        Add(SiphoningHitReduction, (long)state.Held * Get("additional_physical_damage_reduction_against_hits_%_per_siphoning_charge"));
        return rows;
    }

    public static int AdvanceSiphoningDamageLocked(ClientState conn, double now, IReadOnlyDictionary<uint, int> stats)
    {
        var state = SiphoningAt(conn, now);
        double raw = state.Pending; state.Pending = 0;
        if (raw <= 0 || InGracePeriod(conn) || InTown(conn) || conn.Immune || HardcoreProgressBlocked(conn)) return 0;
        int Get(string n) => Stats.Key(n) is uint key ? stats.GetValueOrDefault(key) : 0;
        if (DamageImmunities.From(Get).Contains(DamageType.Physical)) return 0;
        int damage = IncomingResourceDefences.TakenOverTime(raw, Get, ref state.TakenCarry,
            IncomingResourceDefences.LessTakenPct(conn.Pools, Get),
            typedIncreasedPct: Get("physical_damage_taken_+%"), reductionPct: ChargeEffects.Reduction(Get).Physical,
            finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        int life = IncomingResourceDefences.DamageOverTime(conn.EnergyShield, damage,
            Get(IncomingResourceDefences.BypassStat), now, ref state.BypassCarry);
        conn.PlayerLife = (uint)Math.Max(0, (long)conn.PlayerLife - life);
        if (conn.PlayerLife == 0) { conn.InvalidateIncomingHits(); RecordHardcoreDeath(conn); }
        return life;
    }

    static async Task PresentSiphoningCharges(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        lock (conn.Monsters)
            if (conn.Siphoning.Sources.Count == 0 && conn.Siphoning.Buff == 0) return;
        bool changed = false;
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, [], "Siphoning charge state", atGate: () =>
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
        if (changed) await SendChargeStats(remote, stream, ciphers, conn, "Siphoning charges");
    }
}
