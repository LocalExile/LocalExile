using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public const string MadnessEffect = "EmbraceMadness";
    const double MadnessTouchSeconds = 20;
    const string MadnessAllPoison = "all_damage_can_poison_while_affected_by_glorious_madness";

    static MadnessState MadnessAt(ClientState conn)
    {
        var state = conn.Madness;
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || !ReferenceEquals(state.Owner, CombatLifetime))
            state.Explosions.Clear();
        var grant = !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0 && !InTown(conn) && CurrentGameRules
            ? AllWornGrantedSkillsOf(conn).Where(g => g.EffectId == MadnessEffect && !g.Support
                && !IsInactive(conn, g.Host) && LeagueFor(conn).Rules.AllowsGemEffect(g.EffectId))
                .Select(g => (WornGrantedSkills.Grant?)g).FirstOrDefault() : null;
        if (grant is null && state.Source is null && !state.Active) return state;
        if (state.Source != grant || !ReferenceEquals(state.Owner, CombatLifetime))
        {
            state.Clear();
            state.Source = grant;
            state.Owner = CombatLifetime;
            state.Skill = 0;
            conn.AuraStatsDirty = true;
        }
        return state;
    }

    public static bool MadnessActiveLocked(ClientState conn) => MadnessAt(conn).Active;

    public static void MadnessExpiryRateLocked(ClientState conn, double now, double rate)
    {
        var state = conn.Madness;
        rate = Math.Max(.25, rate);
        if (state.ExpiryRate == rate) return;
        for (int i = 0; i < 4; i++)
            if (state.Touches[i] > 0)
            {
                state.Until[i] = RecipientEffectTime.RetimedDeadline(state.Until[i], now, state.ExpiryRate, rate);
                state.TouchBuffs[i] = conn.NextBuff++;
            }
        state.ExpiryRate = rate;
        state.Revision++;
        conn.AuraStatsDirty = true;
    }

    static int MadnessSkillValue(ClientState conn, string name) => Stats.Key(name) is uint key
        ? SkillsFor(conn).EffectStats(SkillsFor(conn).EffectRow(MadnessEffect), 1)
            .FirstOrDefault(p => p.Item1 == key).Item2 : 0;

    public static bool MadnessReconcileLocked(ClientState conn, double now)
    {
        long before = conn.Madness.Revision;
        var state = MadnessAt(conn);
        if (!state.Active) return before != state.Revision;
        double interval = Math.Max(.001, MadnessSkillValue(conn, "glorious_madness_timer_ms") / 1000d);
        double duration = MadnessTouchSeconds / state.ExpiryRate;
        // Only touches still alive at now can contribute after a long unobserved interval. Do not replay expired
        // debuffs or run an unbounded catch-up loop; ordinary pause stops this game clock altogether.
        if (state.NextTouch <= now - duration)
            state.NextTouch += (Math.Floor((now - duration - state.NextTouch) / interval) + 1) * interval;
        for (double at = state.NextTouch; at <= now + 1e-9; at = state.NextTouch)
        {
            Expire(at);
            int kind = state.Random.Next(4);
            state.Touches[kind] = Math.Min(10, state.Touches[kind] + 1);
            state.Until[kind] = at + duration;
            state.TouchBuffs[kind] = conn.NextBuff++;
            state.NextTouch = at + interval;
            state.Revision++;
        }
        Expire(now);
        bool changed = before != state.Revision;
        if (changed) conn.AuraStatsDirty = true;
        return changed;

        void Expire(double at)
        {
            for (int i = 0; i < 4; i++)
                if (state.Touches[i] > 0 && state.Until[i] <= at)
                {
                    state.Touches[i] = 0;
                    state.TouchBuffs[i] = 0;
                    state.Revision++;
                }
        }
    }

    public static IReadOnlyList<(uint Key, int Value)> MadnessRawStatsLocked(ClientState conn,
        IReadOnlyDictionary<uint, int> raw)
    {
        var state = MadnessAt(conn);
        if (!state.Active) return [];
        int Get(string name) => AspectInput(conn, raw, name);
        if (Get("your_spells_are_disabled") != 0)
        {
            state.Clear();
            conn.AuraStatsDirty = true;
            return [];
        }
        var result = new List<(uint, int)>();
        void Add(string name, int value)
        {
            if (Stats.Key(name) is uint key && value != 0) result.Add((key, value));
        }
        Add("affected_by_glorious_madness", 1);
        Add("embrace_madness_reactivation_cooldown", 1);
        if (Get(MadnessAllPoison) != 0)
        {
            Add("all_damage_can_poison", 1);
            // The Beacon modifier says "inflicts Poison", rather than merely allowing elemental poison bases.
            Add("base_chance_to_poison_on_hit_%", 100);
        }
        Add("chance_to_deal_double_damage_%", Get("chance_to_deal_double_damage_while_affected_by_glorious_madness_%"));
        Add("base_max_fortification", Get("max_fortification_while_affected_by_glorious_madness_+1_per_4") / 4);
        if (Get("gain_chilling_shocking_igniting_conflux_while_affected_by_glorious_madness") != 0)
        {
            foreach (string ailment in new[] { "chill", "shock", "ignite" }) Add($"all_damage_can_{ailment}", 1);
            Add("base_chance_to_ignite_%", 100);
            Add("chance_to_shock_%", 100);
        }
        if (Get("immune_to_elemental_status_ailments_while_affected_by_glorious_madness") != 0)
        {
            Add("immune_to_elemental_ailments", 1);
            foreach (string ailment in new[] { "ignite", "freeze", "chill", "shock" })
                Add($"base_immune_to_{ailment}", 1);
            Add("immune_to_scorch", 1);
            Add("immune_to_sap", 1);
            Add("cannot_gain_brittle", 1);
        }
        Add("life_recovery_rate_+%", -9 * state.Touches[0]);
        Add("energy_shield_recovery_rate_+%", -9 * state.Touches[0]);
        Add("action_speed_-%", 6 * state.Touches[1]);
        Add("base_damage_taken_+%", 6 * state.Touches[2]);
        Add("flask_effect_+%", -9 * state.Touches[3]);
        Add("charges_gained_+%", -9 * state.Touches[3]);
        return result;
    }

    public static async Task<bool> ToggleMadness(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skill, int effect, Func<bool> current, CooldownUseTicket? admission = null)
    {
        bool accepted = false;
        await SendWorldRecords(remote, stream, ciphers, conn, PlayerId, () =>
        {
            lock (conn.Monsters)
            {
                if (!current() || conn.PlayerLife == 0 || conn.Paused || InTown(conn)
                    || GemSkillInstanceDisabled(conn, effect, skill) is not null) return null;
                var state = MadnessAt(conn);
                var source = SupportedSkill(conn, skill);
                double now = AreaTime(conn);
                if (source.Effect != effect || SkillsFor(conn).EffectId(effect) != MadnessEffect
                    || source.WornGrant != state.Source || state.Source is null || !source.RequirementsMet
                    || (admission is null ? now < state.ReadyAt : !admission.Current(conn))) return null;
                var plan = CooldownFor(conn, effect, source.Level, source.Quality);
                double baseMilliseconds = SkillsFor(conn).CooldownMs(effect, 1)
                    + (state.Active ? MadnessSkillValue(conn, "embrace_madness_amount_of_cooldown_to_gain_ms") : 0);
                double delay = plan is { } cooldown
                    ? cooldown.RecoveryRate > 0 ? cooldown.BaseMilliseconds / (1000 * cooldown.RecoveryRate) : double.PositiveInfinity
                    : baseMilliseconds / 1000d;
                if (state.Active)
                {
                    state.Clear();
                }
                else
                {
                    state.Active = true;
                    state.Skill = skill;
                    state.Buff = conn.NextBuff++;
                    state.NextTouch = now + MadnessSkillValue(conn, "glorious_madness_timer_ms") / 1000d;
                    state.Revision++;
                }
                state.ReadyAt = now + delay;
                conn.AuraStatsDirty = true;
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
                accepted = true;
                return WithOwnAuraRecords(conn, () => []);
            }
        }, "Glorious Madness toggled");
        return accepted;
    }

    static void PresentMadnessLocked(ClientState conn, List<byte[]> packets)
    {
        var state = MadnessAt(conn);
        Present(ref state.PresentedBuff, state.Active ? state.Buff : (ushort)0, 1535);
        for (int i = 0; i < 4; i++)
            Present(ref state.PresentedTouches[i], state.Active ? state.TouchBuffs[i] : (ushort)0,
                (ushort)(1496 + i), state.Touches[i], state.Until[i]);

        void Present(ref ushort shown, ushort wanted, ushort row, int stacks = 1, double until = 0)
        {
            if (shown != 0 && shown != wanted) packets.Add(InstanceEntities.RemoveBuff(PlayerId, shown));
            if (wanted != 0 && shown != wanted)
                packets.Add(InstanceEntities.AddBuff(PlayerId, wanted, row, PlayerId, state.Skill,
                    stacks: (ushort)stacks,
                    u68: until > 0 ? BitConverter.SingleToUInt32Bits((float)Math.Max(0, until - AreaTime(conn))) : 0));
            shown = wanted;
        }
    }
}
