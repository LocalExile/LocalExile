using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public const string MadnessEffect = "EmbraceMadness";
    const double MadnessTouchSeconds = 20;
    const uint MadnessAllPoison = Stat.AllDamageCanPoisonWhileAffectedByGloriousMadness;

    static MadnessState MadnessAt(Player conn)
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

    public static bool MadnessActiveLocked(Player conn) => MadnessAt(conn).Active;

    public static void MadnessExpiryRateLocked(Player conn, double now, double rate)
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
        state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Sources);
        conn.AuraStatsDirty = true;
    }

    static int MadnessSkillValue(Player conn, uint name) => SkillsFor(conn).EffectStats(SkillsFor(conn).EffectRow(MadnessEffect), 1)
        .FirstOrDefault(p => p.Item1 == name).Item2;

    public static bool MadnessReconcileLocked(Player conn, double now)
    {
        long before = conn.Madness.Revision;
        var state = MadnessAt(conn);
        if (!state.Active) return before != state.Revision;
        double interval = Math.Max(.001, MadnessSkillValue(conn, Stat.GloriousMadnessTimerMs) / 1000d);
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
            state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Sources);
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
                    state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Sources);
                }
        }
    }

    public static IReadOnlyList<(uint Key, int Value)> MadnessRawStatsLocked(Player conn,
        IReadOnlyDictionary<uint, int> raw)
    {
        var state = MadnessAt(conn);
        if (!state.Active) return [];
        int Get(uint name) => AspectInput(conn, raw, name);
        if (Get(Stat.YourSpellsAreDisabled) != 0)
        {
            state.Clear();
            conn.AuraStatsDirty = true;
            return [];
        }
        var result = new List<(uint, int)>();
        void Add(uint name, int value)
        {
            if (name is var key && value != 0) result.Add((key, value));
        }
        Add(Stat.AffectedByGloriousMadness, 1);
        Add(Stat.EmbraceMadnessReactivationCooldown, 1);
        if (Get(MadnessAllPoison) != 0)
        {
            Add(Stat.AllDamageCanPoison, 1);
            // The Beacon modifier says "inflicts Poison", rather than merely allowing elemental poison bases.
            Add(Stat.BaseChanceToPoisonOnHitPct, 100);
        }
        Add(Stat.ChanceToDealDoubleDamagePct, Get(Stat.ChanceToDealDoubleDamageWhileAffectedByGloriousMadnessPct));
        Add(Stat.BaseMaxFortification, Get(Stat.MaxFortificationWhileAffectedByGloriousMadnessPlus1Per4) / 4);
        if (Get(Stat.GainChillingShockingIgnitingConfluxWhileAffectedByGloriousMadness) != 0)
        {
            foreach (string ailment in new[] { "chill", "shock", "ignite" }) Add(Stat.Of($"all_damage_can_{ailment}"), 1);
            Add(Stat.BaseChanceToIgnitePct, 100);
            Add(Stat.ChanceToShockPct, 100);
        }
        if (Get(Stat.ImmuneToElementalStatusAilmentsWhileAffectedByGloriousMadness) != 0)
        {
            Add(Stat.ImmuneToElementalAilments, 1);
            foreach (string ailment in new[] { "ignite", "freeze", "chill", "shock" })
                Add(Stat.Of($"base_immune_to_{ailment}"), 1);
            Add(Stat.ImmuneToScorch, 1);
            Add(Stat.ImmuneToSap, 1);
            Add(Stat.CannotGainBrittle, 1);
        }
        Add(Stat.LifeRecoveryRatePlusPct, -9 * state.Touches[0]);
        Add(Stat.EnergyShieldRecoveryRatePlusPct, -9 * state.Touches[0]);
        Add(Stat.ActionSpeedMinusPct, 6 * state.Touches[1]);
        Add(Stat.BaseDamageTakenPlusPct, 6 * state.Touches[2]);
        Add(Stat.FlaskEffectPlusPct, -9 * state.Touches[3]);
        Add(Stat.ChargesGainedPlusPct, -9 * state.Touches[3]);
        return result;
    }

    public static async Task<bool> ToggleMadness(Player conn, ushort skill, int effect, Func<bool> current, CooldownUseTicket? admission = null)
    {
        bool accepted = false;
        await SendWorldRecords(conn, PlayerId, () =>
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
                    + (state.Active ? MadnessSkillValue(conn, Stat.EmbraceMadnessAmountOfCooldownToGainMs) : 0);
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
                    state.NextTouch = now + MadnessSkillValue(conn, Stat.GloriousMadnessTimerMs) / 1000d;
                    state.Revision++; MarkPlayerStats(conn, PlayerStatStep.Sources);
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

    static void PresentMadnessLocked(Player conn, List<byte[]> packets)
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
