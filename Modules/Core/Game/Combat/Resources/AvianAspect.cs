using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string AvianAspect = "AspectOfTheAvian";
    // Nearby-allies policy pending retail range measurement, kept separate from ordinary aura area/effect scaling.
    public const int AvianSharingRadius = 40;

    public static SharedAura[] SharedAvianAuras(Player player)
    {
        if (player.Closed || player.LeavingArea || InTown(player) || !AvianActive(player) || AvianRoot(player) is not { } root) return [];
        var raw = StatSourceValues(player);
        int Get(uint n) => AspectInput(player, raw, n);
        if (Get(Stat.AspectOfTheAvianGrantsAviansMightAndAviansFlightToNearbyAllies) == 0) return [];
        bool might = player.Reservations.Avian == ReservationState.AvianPhase.Might;
        var rows = root.SkillStats ?? SkillsFor(player).EffectStats(root.Effect, root.Level, root.Quality);
        var names = might ? new[] { Stat.ChanceToDealDoubleDamagePct, Stat.MinionChanceToDealDoubleDamagePct }
            : new[] { Stat.BaseMovementVelocityPlusPct, Stat.MinionMovementSpeedPlusPct };
        var frozen = names.Select(key =>
        {
            int value = rows.FirstOrDefault(s => s.Key == key).Value;
            return (Key: key, Value: (int)Math.Round(value * Math.Max(0, 1 + Get(Stat.AspectOfTheAvianBuffEffectPlusPct) / 100d),
                MidpointRounding.AwayFromZero));
        }).ToArray();
        // Phase and sender effect are frozen in the immutable peer snapshot; observers never read another simulation.
        return [new(root with { EffectId = might ? "AvianMightShared" : "AvianFlightShared",
            BuffRow = might ? (ushort)1040 : (ushort)1041, SourceBuff = null, SkillStats = frozen,
            ExternalEffectPct = 0, SupportEffectPct = 0, AffectsSelf = true }, AvianSharingRadius, 0)];
    }
    static AuraCatalog.Aura? AvianRoot(Player player) => player.PlayerLife > 0
        ? player.Auras.Values.FirstOrDefault(a => a.EffectId == AvianAspect) : null;

    public static bool AvianActive(Player player) => AvianRoot(player) is { } root
        && player.Reservations.AvianOwner == new ReservationState.CatAspectOwner(root.SkillId, root.Buff)
        && player.Reservations.Avian != ReservationState.AvianPhase.None;

    static int AspectInput(Player player, IReadOnlyDictionary<uint, int> raw, uint name) =>
        name is var key ? raw.GetValueOrDefault(key) : 0;

    static void EndAvianAspectLocked(Player player, AuraCatalog.Aura root)
    {
        var state = player.Reservations;
        if (state.AvianOwner != new ReservationState.CatAspectOwner(root.SkillId, root.Buff)) return;
        state.AvianOwner = null;
        state.Avian = ReservationState.AvianPhase.None;
        state.AvianStartedAt = state.AvianDeadline = 0;
        player.AuraStatsDirty = true;
        player.Minions.StatRevision++;
    }

    public static bool AvianReconcileLocked(Player player, double now,
        IReadOnlyDictionary<uint, int>? raw = null)
    {
        var state = player.Reservations;
        var root = AvianRoot(player);
        ReconcileTwisterSourcesLocked(player);
        if (root is null)
        {
            if (state.AvianOwner is not { } owner) return false;
            EndAvianAspectLocked(player, new(owner.Skill, 0, AvianAspect, "", owner.RootBuff, 1039, 0, 0));
            return true;
        }
        raw ??= StatSourceValues(player);
        int Get(uint name) => AspectInput(player, raw, name);
        var rows = root.SkillStats ?? SkillsFor(player).EffectStats(root.Effect, root.Level, root.Quality);
        int Own(uint key) => rows.FirstOrDefault(p => p.Key == key).Value;
        double Duration(uint source, uint modifier) => Math.Max(.001,
            SkillDurationModifiers.Seconds(Math.Max(0, Own(source) + Get(modifier)), Get, Own));
        double might = Duration(Stat.BaseSkillEffectDuration, Stat.AviansMightDurationMsPlus);
        double flight = Duration(Stat.BaseSecondarySkillEffectDuration, Stat.AviansFlightDurationMsPlus);
        bool changed = false;
        var previousOwner = state.AvianOwner;
        double previousStart = state.AvianStartedAt;
        var previousPhase = state.Avian;
        var wanted = new ReservationState.CatAspectOwner(root.SkillId, root.Buff);
        if (state.AvianOwner != wanted || state.Avian == ReservationState.AvianPhase.None)
        {
            state.AvianOwner = wanted;
            state.Avian = ReservationState.AvianPhase.Might;
            state.AvianStartedAt = now;
            changed = true;
        }
        double deadline = state.AvianStartedAt + (state.Avian == ReservationState.AvianPhase.Might ? might : flight);
        // Reuse the tested alternating-clock arithmetic, including exact boundaries and long paused intervals.
        var settled = SettleCatPhase(state.Avian == ReservationState.AvianPhase.Might
            ? ReservationState.CatPhase.Stealth : ReservationState.CatPhase.Agility,
            state.AvianStartedAt, deadline, now, might, flight);
        var phase = settled.Phase == ReservationState.CatPhase.Stealth
            ? ReservationState.AvianPhase.Might : ReservationState.AvianPhase.Flight;
        changed |= state.Avian != phase || Math.Abs(state.AvianDeadline - settled.Deadline) > 1e-9;
        state.Avian = phase;
        state.AvianStartedAt = settled.Started;
        state.AvianDeadline = settled.Deadline;
        if (previousOwner != wanted || previousPhase != phase || Math.Abs(previousStart - settled.Started) > 1e-9)
            QueueTwisterGainLocked(player);
        if (changed)
        {
            PresentAvianPhaseLocked(player, moved: true);
            player.AuraStatsDirty = true;
            player.Minions.StatRevision++;
        }
        return changed;
    }

    public static IReadOnlyList<(uint Key, int Value)> AvianRawStatsLocked(Player player,
        IReadOnlyDictionary<uint, int> raw, bool includeReceived = false)
    {
        var root = AvianRoot(player);
        bool own = AvianActive(player) && root is not null;
        var received = player.ReceivedAuras.Values.Where(a => a.EffectId is "AvianMightShared" or "AvianFlightShared").ToArray();
        if (!own && received.Length == 0) return [];
        int Get(uint name) => AspectInput(player, raw, name);
        var rows = root is null ? [] : root.SkillStats ?? SkillsFor(player).EffectStats(root.Effect, root.Level, root.Quality);
        var result = new List<(uint, int)>();
        void Add(uint name, int value)
        {
            if (value != 0 && name is var key) result.Add((key, value));
        }
        int Received(uint key) => received.Select(a => (a.SkillStats ?? []).FirstOrDefault(p => p.Key == key).Value).DefaultIfEmpty().Max();
        void Scaled(uint name, bool local)
        {
            uint key = name;
            int value = local ? rows.FirstOrDefault(p => p.Key == key).Value : 0;
            // Aspect is a buff, not an Aura: ordinary aura effect never scales it.
            int scaled = (int)Math.Round(value * Math.Max(0, 1 + Get(Stat.AspectOfTheAvianBuffEffectPlusPct) / 100d),
                MidpointRounding.AwayFromZero);
            int external = Received(name);
            Add(name, Math.Max(scaled, external) - (includeReceived ? 0 : external));
        }
        bool ownMight = own && player.Reservations.Avian == ReservationState.AvianPhase.Might;
        bool ownFlight = own && player.Reservations.Avian == ReservationState.AvianPhase.Flight;
        bool receivedMight = received.Any(a => a.EffectId == "AvianMightShared");
        bool receivedFlight = received.Any(a => a.EffectId == "AvianFlightShared");
        Add(Stat.HasAviansMight, (ownMight || receivedMight) && (includeReceived || !receivedMight) ? 1 : 0);
        Add(Stat.HasAviansFlight, (ownFlight || receivedFlight) && (includeReceived || !receivedFlight) ? 1 : 0);
        Scaled(Stat.ChanceToDealDoubleDamagePct, ownMight);
        Scaled(Stat.MinionChanceToDealDoubleDamagePct, ownMight);
        Scaled(Stat.BaseMovementVelocityPlusPct, ownFlight);
        Scaled(Stat.MinionMovementSpeedPlusPct, ownFlight);
        if (ownMight || receivedMight)
        {
            foreach (string element in new[] { "cold", "lightning" })
                foreach (string bound in new[] { "minimum", "maximum" })
                    // RawAdded already includes global added damage for either attack or spell. Publishing typed
                    // copies as well doubles the same Winds roll in both real hit paths.
                    Add(Stat.Of($"global_{bound}_added_{element}_damage"),
                        Get(Stat.Of($"{bound}_added_{element}_damage_while_you_have_avians_might")));
        }
        if (ownFlight || receivedFlight)
        {
            Add(Stat.BaseLifeRegenerationRatePerMinute, Get(Stat.LifeRegenerationPerMinuteWhileYouHaveAviansFlight));
            Add(Stat.BaseManaRegenerationRatePerMinute, Get(Stat.ManaRegenerationRatePerMinuteWhileYouHaveAviansFlight));
        }
        return result;
    }

    // Avian's Might (row 1040) or Flight (1041) while its root aura runs; its countdown is set when the phase changes.
    static void PresentAvianPhaseLocked(Player player, bool moved = false)
    {
        var state = player.Reservations;
        var buffs = player.Area.Buffs;
        ushort row = !AvianActive(player) ? (ushort)0 : state.Avian == ReservationState.AvianPhase.Might ? (ushort)1040 : (ushort)1041;
        foreach (ushort phase in (ushort[])[1040, 1041])
            if (phase != row) buffs.Clear(player.Id, phase, player.Id);
        if (row == 0) return;
        // Both rows are non-recovery buffs. Granted stats travel in the ordinary final stat update.
        var buff = new Buff(row, player.Id, state.AvianOwner!.Value.Skill, U68: Buff.Seconds(state.AvianDeadline - AreaTime(player)));
        if (moved) buffs.Set(player.Id, buff); else buffs.Keep(player.Id, buff);
    }
}
