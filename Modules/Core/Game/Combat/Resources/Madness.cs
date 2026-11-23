using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public const string MadnessEffect = "EmbraceMadness";
    const double MadnessTouchSeconds = 20;
    const uint MadnessAllPoison = Stat.AllDamageCanPoisonWhileAffectedByGloriousMadness;

    static MadnessState MadnessAt(Player player)
    {
        var state = player.Madness;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0 || !ReferenceEquals(state.Owner, CombatLifetime))
            state.Explosions.Clear();
        var grant = !player.Closed && !player.LeavingArea && player.PlayerLife > 0 && !InTown(player) && CurrentGameRules
            ? AllWornGrantedSkillsOf(player).Where(g => g.EffectId == MadnessEffect && !g.Support
                && !IsInactive(player, g.Host) && LeagueFor(player).Rules.AllowsGemEffect(g.EffectId))
                .Select(g => (WornGrantedSkills.Grant?)g).FirstOrDefault() : null;
        if (grant is null && state.Source is null && !state.Active) return state;
        if (state.Source != grant || !ReferenceEquals(state.Owner, CombatLifetime))
        {
            state.Clear();
            state.Source = grant;
            state.Owner = CombatLifetime;
            state.Skill = 0;
            player.AuraStatsDirty = true;
        }
        return state;
    }

    public static bool MadnessActiveLocked(Player player) => MadnessAt(player).Active;

    public static void MadnessExpiryRateLocked(Player player, double now, double rate)
    {
        var state = player.Madness;
        rate = Math.Max(.25, rate);
        if (state.ExpiryRate == rate) return;
        for (int i = 0; i < 4; i++)
            if (state.Touches[i] > 0)
            {
                state.Until[i] = RecipientEffectTime.RetimedDeadline(state.Until[i], now, state.ExpiryRate, rate);
            }
        state.ExpiryRate = rate;
        PresentMadnessLocked(player, moved: 4);
        state.Revision++; MarkPlayerStats(player, PlayerStatStep.Sources);
        player.AuraStatsDirty = true;
    }

    static int MadnessSkillValue(Player player, uint name) => SkillsFor(player).EffectStats(SkillsFor(player).EffectRow(MadnessEffect), 1)
        .FirstOrDefault(p => p.Item1 == name).Item2;

    public static bool MadnessReconcileLocked(Player player, double now)
    {
        long before = player.Madness.Revision;
        var state = MadnessAt(player);
        if (!state.Active) return before != state.Revision;
        double interval = Math.Max(.001, MadnessSkillValue(player, Stat.GloriousMadnessTimerMs) / 1000d);
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
            state.NextTouch = at + interval;
            state.Revision++; MarkPlayerStats(player, PlayerStatStep.Sources);
            PresentMadnessLocked(player, moved: kind);
        }
        Expire(now);
        bool changed = before != state.Revision;
        if (changed) player.AuraStatsDirty = true;
        return changed;

        void Expire(double at)
        {
            for (int i = 0; i < 4; i++)
                if (state.Touches[i] > 0 && state.Until[i] <= at)
                {
                    state.Touches[i] = 0;
                    state.Revision++; MarkPlayerStats(player, PlayerStatStep.Sources);
                }
        }
    }

    public static IReadOnlyList<(uint Key, int Value)> MadnessRawStatsLocked(Player player,
        IReadOnlyDictionary<uint, int> raw)
    {
        var state = MadnessAt(player);
        if (!state.Active) return [];
        int Get(uint name) => AspectInput(player, raw, name);
        if (Get(Stat.YourSpellsAreDisabled) != 0)
        {
            state.Clear();
            player.AuraStatsDirty = true;
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

    public static async Task<bool> ToggleMadness(Player player, ushort skill, int effect, Func<bool> current, CooldownUseTicket? admission = null)
    {
        bool accepted = false;
        await SendWorldRecords(player, player.Id, () =>
        {
            if (!current() || player.PlayerLife == 0 || player.Paused || InTown(player)
                || GemSkillInstanceDisabled(player, effect, skill) is not null) return null;
            var state = MadnessAt(player);
            var source = SupportedSkill(player, skill);
            double now = AreaTime(player);
            if (source.Effect != effect || SkillsFor(player).EffectId(effect) != MadnessEffect
                || source.WornGrant != state.Source || state.Source is null || !source.RequirementsMet
                || (admission is null ? now < state.ReadyAt : !admission.Current(player))) return null;
            var plan = CooldownFor(player, effect, source.Level, source.Quality);
            double baseMilliseconds = SkillsFor(player).CooldownMs(effect, 1)
                + (state.Active ? MadnessSkillValue(player, Stat.EmbraceMadnessAmountOfCooldownToGainMs) : 0);
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
                state.NextTouch = now + MadnessSkillValue(player, Stat.GloriousMadnessTimerMs) / 1000d;
                state.Revision++; MarkPlayerStats(player, PlayerStatStep.Sources);
            }
            state.ReadyAt = now + delay;
            player.AuraStatsDirty = true;
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeed();
            accepted = true;
            return WithOwnAuraRecords(player, () => []);
        }, "Glorious Madness toggled");
        return accepted;
    }

    // Glorious Madness (row 1535) and its four touches (1496-1499, their stacks and countdowns) while it is active. A touch
    // gained or retimed moves its countdown (moved).
    static void PresentMadnessLocked(Player player, int moved = -1)
    {
        var state = MadnessAt(player);
        var buffs = player.Area.Buffs;
        if (state.Active) buffs.Keep(player.Id, new(1535, player.Id, state.Skill));
        else buffs.Clear(player.Id, 1535, player.Id);
        for (int i = 0; i < 4; i++)
        {
            ushort row = (ushort)(1496 + i);
            if (!state.Active || state.Touches[i] == 0) { buffs.Clear(player.Id, row, player.Id); continue; }
            var buff = new Buff(row, player.Id, state.Skill, Stacks: (ushort)state.Touches[i],
                U68: Buff.Seconds(state.Until[i] - AreaTime(player)));
            if (moved == i || moved == 4) buffs.Set(player.Id, buff); else buffs.Keep(player.Id, buff);
        }
    }
}
