using GameServer.Core.Protocol.Instance;

static partial class Game
{
    const string AvianAspect = "AspectOfTheAvian";
    static AuraCatalog.Aura? AvianRoot(ClientState conn) => conn.PlayerLife > 0
        ? conn.Auras.Values.FirstOrDefault(a => a.EffectId == AvianAspect) : null;

    internal static bool AvianActive(ClientState conn) => AvianRoot(conn) is { } root
        && conn.Reservations.AvianOwner == new ReservationState.CatAspectOwner(root.SkillId, root.Buff)
        && conn.Reservations.Avian != ReservationState.AvianPhase.None;

    static int AspectInput(ClientState conn, IReadOnlyDictionary<uint, int> raw, string name) =>
        Stats.Key(name) is uint key ? conn.PlayerStats.TryGetValue(key, out int value) ? value
            : raw.GetValueOrDefault(key) : 0;

    static void EndAvianAspectLocked(ClientState conn, AuraCatalog.Aura root)
    {
        var state = conn.Reservations;
        if (state.AvianOwner != new ReservationState.CatAspectOwner(root.SkillId, root.Buff)) return;
        state.AvianOwner = null;
        state.Avian = ReservationState.AvianPhase.None;
        state.AvianStartedAt = state.AvianDeadline = 0;
        state.AvianBuff = 0;
        conn.AuraStatsDirty = true;
        conn.Minions.StatRevision++;
    }

    internal static bool AvianReconcileLocked(ClientState conn, double now,
        IReadOnlyDictionary<uint, int>? raw = null)
    {
        var state = conn.Reservations;
        var root = AvianRoot(conn);
        if (root is null)
        {
            if (state.AvianOwner is not { } owner) return false;
            EndAvianAspectLocked(conn, new(owner.Skill, 0, AvianAspect, "", owner.RootBuff, 1039, 0, 0));
            return true;
        }
        raw ??= CharacterStatSources(conn).Stats;
        int Get(string name) => AspectInput(conn, raw, name);
        var rows = root.SkillStats ?? SkillsFor(conn).EffectStats(root.Effect, root.Level, root.Quality);
        int Own(string name) => Stats.Key(name) is uint key ? rows.FirstOrDefault(p => p.Key == key).Value : 0;
        double Duration(string source, string modifier) => Math.Max(.001,
            SkillDurationModifiers.Seconds(Math.Max(0, Own(source) + Get(modifier)), Get, Own));
        double might = Duration("base_skill_effect_duration", "avians_might_duration_ms_+");
        double flight = Duration("base_secondary_skill_effect_duration", "avians_flight_duration_ms_+");
        bool changed = false;
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
        if (changed)
        {
            state.AvianBuff = conn.NextBuff++;
            conn.AuraStatsDirty = true;
            conn.Minions.StatRevision++;
        }
        return changed;
    }

    internal static IReadOnlyList<(uint Key, int Value)> AvianRawStatsLocked(ClientState conn,
        IReadOnlyDictionary<uint, int> raw)
    {
        if (!AvianActive(conn) || AvianRoot(conn) is not { } root) return [];
        int Get(string name) => AspectInput(conn, raw, name);
        var rows = root.SkillStats ?? SkillsFor(conn).EffectStats(root.Effect, root.Level, root.Quality);
        var result = new List<(uint, int)>();
        void Add(string name, int value)
        {
            if (value != 0 && Stats.Key(name) is uint key) result.Add((key, value));
        }
        void Scaled(string name)
        {
            if (Stats.Key(name) is not uint key) return;
            int value = rows.FirstOrDefault(p => p.Key == key).Value;
            // Aspect is a buff, not an Aura: ordinary aura effect never scales it.
            Add(name, (int)Math.Round(value * Math.Max(0, 1 + Get("aspect_of_the_avian_buff_effect_+%") / 100d),
                MidpointRounding.AwayFromZero));
        }
        if (conn.Reservations.Avian == ReservationState.AvianPhase.Might)
        {
            Scaled("chance_to_deal_double_damage_%");
            Scaled("minion_chance_to_deal_double_damage_%");
            foreach (string element in new[] { "cold", "lightning" })
                foreach (string bound in new[] { "minimum", "maximum" })
                    foreach (string kind in new[] { "global", "attack", "spell" })
                        Add($"{kind}_{bound}_added_{element}_damage",
                            Get($"{bound}_added_{element}_damage_while_you_have_avians_might"));
        }
        else
        {
            Scaled("base_movement_velocity_+%");
            Scaled("minion_movement_speed_+%");
            Add("base_life_regeneration_rate_per_minute", Get("life_regeneration_per_minute_while_you_have_avians_flight"));
            Add("base_mana_regeneration_rate_per_minute", Get("mana_regeneration_rate_per_minute_while_you_have_avians_flight"));
        }
        return result;
    }

    static void PresentAvianPhaseLocked(ClientState conn, List<byte[]> packets)
    {
        var state = conn.Reservations;
        ushort wanted = AvianActive(conn) ? state.AvianBuff : (ushort)0;
        if (state.PresentedAvianBuff != 0 && state.PresentedAvianBuff != wanted)
            packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.PresentedAvianBuff));
        if (wanted != 0 && wanted != state.PresentedAvianBuff)
        {
            ushort row = state.Avian == ReservationState.AvianPhase.Might ? (ushort)1040 : (ushort)1041;
            // Both rows are non-recovery buffs. Granted stats travel in the ordinary final stat update.
            packets.Add(InstanceEntities.AddBuff(PlayerId, wanted, row, PlayerId, state.AvianOwner!.Value.Skill,
                u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, state.AvianDeadline - AreaTime(conn)))));
            state.PresentedAvianRow = row;
        }
        state.PresentedAvianBuff = wanted;
        if (wanted == 0) state.PresentedAvianRow = 0;
    }
}
