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

    public static CarriedSiphoning CaptureSiphoning(Player player, double at)
    {
        var state = SiphoningAt(player, at);
        return new(state.Held, [.. state.Sources], Math.Max(0, state.Until - at),
            Math.Max(0, state.UsedUntil - at), state.Pending, state.TakenCarry, state.BypassCarry);
    }

    public static void RestoreSiphoning(Player player, CarriedSiphoning value, double at)
    {
        var state = player.Siphoning;
        state.Rules = CombatLifetime; state.Epoch = player.ProjectileEpoch;
        state.Held = value.Held; state.Sources = [.. value.Sources];
        state.Until = at + value.Seconds; state.UsedUntil = at + value.UsedSeconds; state.At = at;
        state.Pending = value.Pending; state.TakenCarry = value.TakenCarry; state.BypassCarry = value.BypassCarry;
        state.Revision++; MarkPlayerStats(player, PlayerStatStep.Siphoning);
    }

    static void SettleSiphoning(Player player, SiphoningState state, double now)
    {
        double end = Math.Min(now, Math.Min(state.Until, state.UsedUntil));
        if (end > state.At) state.Pending += (end - state.At) * state.Held * state.PerMinute / 60d;
        state.At = Math.Max(state.At, now);
        if (now >= state.Until && state.Held != 0) { state.Held = 0; state.Revision++; MarkPlayerStats(player, PlayerStatStep.Siphoning); }
        bool active = state.Held > 0 && now < state.Until && now < state.UsedUntil && state.PerMinute > 0;
        if (state.Degenerating != active) { state.Degenerating = active; state.Revision++; MarkPlayerStats(player, PlayerStatStep.Siphoning); }
    }

    static SiphoningState SiphoningAt(Player player, double now, IReadOnlyDictionary<uint, int>? raw = null)
    {
        var state = player.Siphoning;
        if (player.ResourceEffectsState is ResourceEffectsState { Loading: true }) now = 0;
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != player.ProjectileEpoch)
        {
            state.Rules = CombatLifetime; state.Epoch = player.ProjectileEpoch; state.At = now;
            state.Held = state.Maximum = state.Gain = 0; state.Until = state.UsedUntil = 0;
            state.Pending = state.TakenCarry = state.BypassCarry = 0; state.Sources.Clear();
            state.Degenerating = false; state.Revision++; MarkPlayerStats(player, PlayerStatStep.Siphoning);
        }
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.RageRetired
            || (player.PlayerLife == 0 && player.Connection.PlayerObjectCreated))
        {
            if (state.Held != 0 || state.Degenerating) { state.Revision++; MarkPlayerStats(player, PlayerStatStep.Siphoning); }
            state.Held = state.Maximum = state.Gain = 0; state.Sources.Clear();
            state.Pending = state.TakenCarry = state.BypassCarry = 0; state.At = now; state.Degenerating = false;
            return state;
        }
        var sources = player.StatSourcesCache;
        var active = (sources?.Worn ?? []).Where(w => sources?.Inactive.Contains(w.Id) != true).ToArray();
        var hosts = active.Where(w => w.Item.Mods.Any(m => ItemsFor(player).Mods.ByHash(m.Hash)?.Stats
            .Any(s => s.Id == SiphoningGain || s.Id == SiphoningMaximum) == true)).Select(w => w.Id).ToHashSet();
        // Only the Elder and Shaper items' mods grant Siphoning Charges: with none worn there is nothing to read, and the
        // stats cache's key (PlayerStatsKey → SiphoningRevision) no longer gathers every stat source on each lookup to
        // find that out (CPU sample 2026-11-18: half of all CharacterStatSources work in a Fireball fight).
        if (raw is null && hosts.Count == 0 && state.Sources.Count == 0)
        { state.At = Math.Max(state.At, now); return state; }
        raw ??= StatSourceValues(player);
        int Get(uint key) => raw.GetValueOrDefault(key);
        if (state.Sources.Count == 0 && Get(SiphoningGain) == 0 && Get(SiphoningMaximum) == 0)
        { state.At = Math.Max(state.At, now); return state; }
        if (!state.Sources.SetEquals(hosts))
        {
            state.Sources = hosts; state.Held = 0; state.Until = state.UsedUntil = 0;
            state.Pending = state.TakenCarry = state.BypassCarry = 0; state.At = now; state.Revision++; MarkPlayerStats(player, PlayerStatStep.Siphoning);
        }
        SettleSiphoning(player, state, now);
        int count = active.Count(w => (w.Item.Influences & (ItemInfluence.Elder | ItemInfluence.Shaper)) != 0);
        state.Maximum = (int)Math.Clamp((long)Math.Max(0, Get(SiphoningMaximum)) * count, 0, ushort.MaxValue);
        state.Gain = Math.Max(0, Get(SiphoningGain));
        // This owner integrates the exact four-second use window, including its expiry inside a recovery step.
        // Read its coefficient before generic recent filtering, from the same qualified physical equipment.
        state.PerMinute = Math.Max(0, ItemModStats.Sum(active.Select(w => w.Item), ItemsFor(player).Mods)
            .GetValueOrDefault(SiphoningDegen));
        if (state.Held > state.Maximum) { state.Held = state.Maximum; state.Revision++; MarkPlayerStats(player, PlayerStatStep.Siphoning); }
        SettleSiphoning(player, state, now);
        return state;
    }

    public static void SiphoningSkillUsedLocked(Player player, double at, Random? random = null) =>
        ApplySiphoningSkillUseLocked(player, at, StatSourceValues(player), random);

    static void ApplySiphoningSkillUseLocked(Player player, double at, IReadOnlyDictionary<uint, int> raw, Random? random = null)
    {
        var state = SiphoningAt(player, at, raw);
        if (player.Paused || InTown(player) || state.Maximum <= 0 || state.Sources.Count == 0) return;
        // Install the existing transfer hook before any accepted charge can leave this area.
        ResourceEffectsOf(player);
        state.UsedUntil = Math.Max(state.At, at) + RecentEvents.WindowSeconds;
        SettleSiphoning(player, state, state.At);
        if (!RandomRolls.Chance(state.Gain, 100, random ?? AreaRng(player))) return;
        int Get(uint key) => raw.GetValueOrDefault(key);
        double duration = Charges.BaseDurationMs / 1000d
            * Math.Max(0, 1 + ((long)Get(Stat.ChargeDurationPlusPct) + Get(Stat.BaseChargeDurationPlusPct)) / 100d);
        if (duration <= 0) return;
        state.Held = Math.Min(state.Maximum, state.Held + 1);
        state.Until = state.At + duration;
        player.Area.Buffs.Set(player.Id, SiphoningBuff(player, state));
        state.Revision++; MarkPlayerStats(player, PlayerStatStep.Siphoning);
        SettleSiphoning(player, state, state.At);
        Console.WriteLine($"[gateway] Siphoning charge {state.Held}/{state.Maximum} until={state.Until:0.000} t={at:0.000}");
    }

    public static long SiphoningRevision(Player player)
    {
        return SiphoningAt(player, AreaTime(player)).Revision;
    }

    public static bool SiphoningDegenerationActive(Player player)
    {
        return SiphoningAt(player, AreaTime(player)).Degenerating;
    }

    public static void ClearSiphoningOnDeath(Player player)
    {
        var state = player.Siphoning;
        state.Held = 0; state.Until = state.UsedUntil = 0;
        state.Pending = state.TakenCarry = state.BypassCarry = 0; state.Degenerating = false; state.Revision++; MarkPlayerStats(player, PlayerStatStep.Siphoning);
    }

    public static IReadOnlyList<(uint Key, int Value)> SiphoningRawStatsLocked(Player player,
        IReadOnlyDictionary<uint, int> raw)
    {
        var state = SiphoningAt(player, AreaTime(player), raw);
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

    public static int AdvanceSiphoningDamageLocked(Player player, double now, IReadOnlyDictionary<uint, int> stats)
    {
        var state = SiphoningAt(player, now);
        double raw = state.Pending; state.Pending = 0;
        if (raw <= 0 || InGracePeriod(player) || InTown(player) || player.Immune || HardcoreProgressBlocked(player)) return 0;
        int Get(uint key) => stats.GetValueOrDefault(key);
        if (DamageImmunities.From(Get).Contains(DamageType.Physical)) return 0;
        int damage = IncomingResourceDefences.TakenOverTime(raw, Get, ref state.TakenCarry,
            IncomingResourceDefences.LessTakenPct(player.Pools, Get),
            typedIncreasedPct: Get(Stat.PhysicalDamageTakenPlusPct), reductionPct: ChargeEffects.Reduction(Get).Physical,
            finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        int life = IncomingResourceDefences.DamageOverTime(player.EnergyShield, damage,
            Get(IncomingResourceDefences.BypassStat), now, ref state.BypassCarry);
        player.PlayerLife = VaalImmortalCallLife(player, (uint)Math.Max(0, (long)player.PlayerLife - life), now);
        if (player.PlayerLife == 0) { player.InvalidateIncomingHits(); RecordHardcoreDeath(player); }
        return life;
    }

    // Siphoning charges (row 32): their count and the countdown from the last gain.
    static Buff SiphoningBuff(Player player, SiphoningState state) =>
        new(32, player.Id, Stacks: (ushort)state.Held, U68: Buff.Seconds(state.Until - AreaTime(player)));

    static async Task PresentSiphoningCharges(Player player)
    {
        if (player.Siphoning.Sources.Count == 0 && player.Siphoning.Buff == 0) return;
        bool changed = false;
        await SendWorldRecord(player, player.Id, [], "Siphoning charge state", atGate: () =>
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea) return null;
            var state = SiphoningAt(player, AreaTime(player));
            if (state.Held > 0) player.Area.Buffs.Keep(player.Id, SiphoningBuff(player, state));
            else player.Area.Buffs.Clear(player.Id, 32, player.Id);
            state.Buff = (ushort)(state.Held > 0 ? 1 : 0);
            if (state.PresentedDegenerating == state.Degenerating) return [];
            state.PresentedDegenerating = state.Degenerating;
            changed = true;
            return [];
        });
        if (changed) await SendChargeStats(player, "Siphoning charges");
    }
}
