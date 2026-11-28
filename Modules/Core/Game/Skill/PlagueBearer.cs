using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    internal const string PlagueBearerEffect = "PlagueBearer";
    internal const ushort PlagueBearerBuffRow = 1370; // BuffDefinitions corrosive_shroud_buff
    internal const ushort PlagueBearerAccumulatingBuffRow = 1381; // BuffDefinitions corrosive_shroud_accumulating_damage
    internal const ushort PlagueBearerAuraBuffRow = 1382; // BuffDefinitions corrosive_shroud_aura
    internal const ushort PlagueBearerAtMaxBuffRow = 1417; // BuffDefinitions corrosive_shroud_at_max_damage
    internal const int PlagueBearerBaseRadius = 24;

    internal enum PlagueBearerMode { Off, Incubating, Infecting }

    sealed class PlagueBearerState
    {
        public PlagueBearerMode Mode;
        public ushort Skill;
        public int Effect, Epoch, LastShownPercent = -1;
        public double Stored, MaxStored, GainPct, RatePct, LessPoisonPct, LastTickAt;
        public object? Rules;
        public GemSourceLifetimes.SourceLifetime? Source;
        public readonly Dictionary<uint, double> AuraCarry = [];
        public readonly HashSet<uint> AuraMarked = [];
    }

    static readonly RuleTable<Player, PlagueBearerState> PlagueBearers = new();

    internal static (PlagueBearerMode Mode, double Stored, double MaxStored) PlagueBearerStatus(Player player) =>
        PlagueBearers.TryGetValue(player, out var state) && state.Mode != PlagueBearerMode.Off
            ? (state.Mode, state.Stored, state.MaxStored) : default;

    static bool PlagueBearerLive(Player player, PlagueBearerState state) =>
        state.Mode != PlagueBearerMode.Off && CurrentGameWork && state.Epoch == player.ProjectileEpoch
        && ReferenceEquals(state.Rules, CombatLifetime) && !player.Closed && !player.LeavingArea
        && player.PendingTransfer is null && player.PlayerLife > 0
        && GemSourceLifetimeCurrentLocked(player, state.Skill, state.Effect, state.Source);

    static bool PlagueBearerCurrentLocked(Player player, PlagueBearerState state)
    {
        if (state.Mode == PlagueBearerMode.Off) return false;
        if (!PlagueBearerLive(player, state))
        {
            EndPlagueBearerLocked(player, state);
            return false;
        }
        return true;
    }

    internal static bool CanUsePlagueBearerReadOnly(Player player, bool automated = false)
    {
        if (!PlagueBearers.TryGetValue(player, out var state) || !PlagueBearerLive(player, state))
            return true; // Off -> can activate into Incubating
        if (state.Mode == PlagueBearerMode.Incubating)
            return automated ? state.MaxStored > 0 && state.Stored >= state.MaxStored - 1e-6 : state.Stored > 0;
        // Infecting: manual cast can toggle back to Incubating; Automation lets Infecting finish draining.
        return !automated;
    }

    internal static bool CanUsePlagueBearerLocked(Player player, bool automated = false)
    {
        if (!PlagueBearers.TryGetValue(player, out var state) || !PlagueBearerCurrentLocked(player, state))
            return true; // Off -> can activate into Incubating
        if (state.Mode == PlagueBearerMode.Incubating)
            return automated ? state.MaxStored > 0 && state.Stored >= state.MaxStored - 1e-6 : state.Stored > 0;
        // Infecting: manual cast can toggle back to Incubating; Automation lets Infecting finish draining.
        return !automated;
    }

    static void ReadPlagueBearerStats(Player player, SupportedGem gem, IReadOnlyDictionary<uint, int> playerStats,
        out double maxStored, out double gainPct, out double ratePct, out double lessPoisonPct)
    {
        int Own(uint key) => gem.Stats.GetValueOrDefault(key);
        double baseMax = Math.Max(1, Own(Stat.CorrosiveShroudMaximumStoredPoisonDamage));
        int maxBonusPct = playerStats.GetValueOrDefault(Stat.PlagueBearerMaximumStoredPoisonDamagePlusPct)
            + Own(Stat.PlagueBearerMaximumStoredPoisonDamagePlusPct);
        maxStored = Math.Max(1, Math.Floor(baseMax * Math.Max(0, 1 + maxBonusPct / 100d)));
        gainPct = Math.Max(0, Own(Stat.CorrosiveShroudGainsPctOfDamageFromInflictedPoisons));
        ratePct = Math.Max(0, Own(Stat.CorrosiveShroudPctOfStoredPoisonDamageToDealPerSecond));
        lessPoisonPct = Own(Stat.CorrosiveShroudPoisonDamagePlusPctFinalWhileAccumulatingPoison);
    }

    static bool TogglePlagueBearerLocked(Player player, ushort skillId, double now, bool automated = false)
    {
        var gem = SupportedSkill(player, skillId);
        if (SkillsFor(player).EffectId(gem.Effect) != PlagueBearerEffect) return false;
        var source = CaptureGemSourceLifetimeLocked(player, skillId, gem.Effect);
        if (source is null) return false;
        var playerStats = CharacterPlayerStats(player);
        ReadPlagueBearerStats(player, gem, playerStats, out double maxStored, out double gainPct, out double ratePct, out double lessPoisonPct);

        var state = PlagueBearers.GetValue(player, static _ => new());
        bool current = PlagueBearerCurrentLocked(player, state);
        if (!current)
        {
            state.Mode = PlagueBearerMode.Incubating;
            state.Skill = skillId;
            state.Effect = gem.Effect;
            state.Epoch = player.ProjectileEpoch;
            state.Rules = CombatLifetime;
            state.Source = source;
            state.Stored = 0;
            state.MaxStored = maxStored;
            state.GainPct = gainPct;
            state.RatePct = ratePct;
            state.LessPoisonPct = lessPoisonPct;
            state.LastTickAt = now;
            state.LastShownPercent = -1;
            PresentPlagueBearerBuffsLocked(player, state, force: true);
            MarkPlayerStats(player, PlayerStatStep.Sources);
            ProjectilesFor(player).Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
            return true;
        }

        state.MaxStored = maxStored;
        state.GainPct = gainPct;
        state.RatePct = ratePct;
        state.LessPoisonPct = lessPoisonPct;
        state.Stored = Math.Min(state.Stored, state.MaxStored);

        if (state.Mode == PlagueBearerMode.Incubating)
        {
            if (automated ? state.Stored < state.MaxStored - 1e-6 : state.Stored <= 0) return false;
            state.Mode = PlagueBearerMode.Infecting;
            state.LastTickAt = now;
            PresentPlagueBearerBuffsLocked(player, state, force: true);
            MarkPlayerStats(player, PlayerStatStep.Sources);
            RecomputePoolsAndSpeed(player);
            ProjectilesFor(player).Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
            return true;
        }

        if (automated) return false;
        SwitchPlagueBearerToIncubatingLocked(player, state, now);
        return true;
    }

    static void SwitchPlagueBearerToIncubatingLocked(Player player, PlagueBearerState state, double now)
    {
        ClearPlagueBearerAuraMarksLocked(player, state);
        state.Mode = PlagueBearerMode.Incubating;
        state.LastTickAt = now;
        PresentPlagueBearerBuffsLocked(player, state, force: true);
        MarkPlayerStats(player, PlayerStatStep.Sources);
        if (!player.Closed && !player.LeavingArea && player.PlayerLife > 0)
        {
            RecomputePoolsAndSpeed(player);
            ProjectilesFor(player).Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
        }
    }

    static void ClearPlagueBearerAuraMarksLocked(Player player, PlagueBearerState state)
    {
        foreach (uint id in state.AuraMarked)
            player.Area.Buffs.Clear(id, PlagueBearerAuraBuffRow, player.Id);
        state.AuraMarked.Clear();
        state.AuraCarry.Clear();
        player.Area.Buffs.Clear(player.Id, PlagueBearerAuraBuffRow, player.Id, auraSource: true);
    }

    static void PresentPlagueBearerBuffsLocked(Player player, PlagueBearerState state, bool force = false)
    {
        int percent = state.MaxStored > 0
            ? Math.Clamp((int)Math.Floor(100.0 * state.Stored / state.MaxStored + 1e-9), 0, 100) : 0;
        if (!force && percent == state.LastShownPercent) return;
        state.LastShownPercent = percent;
        var activeSkill = SkillsFor(player).ActionHash(state.Effect) is ushort action
            ? (state.Skill, action) : ((ushort, ushort)?)null;
        bool infecting = state.Mode == PlagueBearerMode.Infecting;
        bool atMax = !infecting && state.MaxStored > 0 && state.Stored >= state.MaxStored - 1e-6;
        player.Area.Buffs.Set(player.Id, new(PlagueBearerBuffRow, player.Id, state.Skill,
            ActiveSkill: activeSkill, DisplayState: (byte)(infecting ? 1 : 0),
            Stacks: (ushort)percent, KeepInstance: true));
        if (infecting)
        {
            player.Area.Buffs.Clear(player.Id, PlagueBearerAccumulatingBuffRow, player.Id);
            player.Area.Buffs.Clear(player.Id, PlagueBearerAtMaxBuffRow, player.Id);
            player.Area.Buffs.Keep(player.Id, new(PlagueBearerAuraBuffRow, player.Id, state.Skill,
                AuraSource: true, DisplayState: 0));
        }
        else if (atMax)
        {
            player.Area.Buffs.Clear(player.Id, PlagueBearerAccumulatingBuffRow, player.Id);
            player.Area.Buffs.Clear(player.Id, PlagueBearerAuraBuffRow, player.Id, auraSource: true);
            player.Area.Buffs.Keep(player.Id, new(PlagueBearerAtMaxBuffRow, player.Id, state.Skill));
        }
        else
        {
            player.Area.Buffs.Clear(player.Id, PlagueBearerAtMaxBuffRow, player.Id);
            player.Area.Buffs.Clear(player.Id, PlagueBearerAuraBuffRow, player.Id, auraSource: true);
            player.Area.Buffs.Keep(player.Id, new(PlagueBearerAccumulatingBuffRow, player.Id, state.Skill));
        }
    }

    static void EndPlagueBearerLocked(Player player, PlagueBearerState state)
    {
        if (state.Mode == PlagueBearerMode.Off) return;
        ClearPlagueBearerAuraMarksLocked(player, state);
        state.Mode = PlagueBearerMode.Off;
        state.Stored = 0;
        state.LastShownPercent = -1;
        player.Area.Buffs.Clear(player.Id, PlagueBearerBuffRow, player.Id);
        player.Area.Buffs.Clear(player.Id, PlagueBearerAccumulatingBuffRow, player.Id);
        player.Area.Buffs.Clear(player.Id, PlagueBearerAtMaxBuffRow, player.Id);
        MarkPlayerStats(player, PlayerStatStep.Sources);
        if (!player.Closed && !player.LeavingArea && player.PlayerLife > 0)
        {
            RecomputePoolsAndSpeed(player);
            ProjectilesFor(player).Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
        }
    }

    internal static void ClearPlagueBearerLocked(Player player, IReadOnlyCollection<ushort>? onlyIds = null)
    {
        if (!PlagueBearers.TryGetValue(player, out var state) || state.Mode == PlagueBearerMode.Off) return;
        if (onlyIds is not null && !onlyIds.Contains(state.Skill)) return;
        EndPlagueBearerLocked(player, state);
    }

    internal static bool CancelPlagueBearerLocked(Player player, Buff buff)
    {
        if (buff.Row != PlagueBearerBuffRow || buff.Source != player.Id
            || !PlagueBearers.TryGetValue(player, out var state) || state.Mode == PlagueBearerMode.Off) return false;
        EndPlagueBearerLocked(player, state);
        return true;
    }

    internal static void PlagueBearerOnPoisonInflictedLocked(Player player, double perSecond, double seconds, int count)
    {
        if (perSecond <= 0 || seconds <= 0 || count <= 0) return;
        if (!PlagueBearers.TryGetValue(player, out var state) || state.Mode != PlagueBearerMode.Incubating) return;
        if (!PlagueBearerCurrentLocked(player, state) || state.Stored >= state.MaxStored) return;
        double added = perSecond * seconds * count * (state.GainPct / 100d);
        if (added <= 0) return;
        bool wasMax = state.Stored >= state.MaxStored - 1e-6;
        state.Stored = Math.Min(state.MaxStored, state.Stored + added);
        bool nowMax = state.Stored >= state.MaxStored - 1e-6;
        PresentPlagueBearerBuffsLocked(player, state, force: !wasMax && nowMax);
        if (!wasMax && nowMax)
        {
            MarkPlayerStats(player, PlayerStatStep.Sources);
            ProjectilesFor(player).Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
        }
    }

    internal static async Task TickPlagueBearer(Player player, double now)
    {
        if (!PlagueBearers.TryGetValue(player, out var state) || state.Mode == PlagueBearerMode.Off) return;
        var kills = new List<(uint Id, SpawnedMonster M)>();
        using (ProjectileMutation(player, advance: false))
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return;
            if (!PlagueBearerCurrentLocked(player, state) || state.Mode != PlagueBearerMode.Infecting) return;
            double dt = Math.Max(0, now - state.LastTickAt);
            state.LastTickAt = now;
            if (dt <= 0) return;

            var gem = SupportedSkill(player, state.Skill);
            var playerStats = CharacterPlayerStats(player);
            ReadPlagueBearerStats(player, gem, playerStats, out state.MaxStored, out state.GainPct, out state.RatePct, out state.LessPoisonPct);
            double dps = state.MaxStored * (state.RatePct / 100d);
            if (dps <= 0 || state.Stored <= 0)
            {
                state.Stored = 0;
                SwitchPlagueBearerToIncubatingLocked(player, state, now);
                return;
            }
            double drained = Math.Min(state.Stored, dps * dt);
            double effectiveDt = drained / dps;
            state.Stored = Math.Max(0, state.Stored - drained);

            int radius = SkillRadius(player, PlagueBearerBaseRadius, gem.Stats, spell: true, gem.Effect, playerStats);
            var origin = player.Walk.Position(now);
            var seen = new HashSet<uint>();
            if (!InGracePeriod(player))
            {
                foreach (var (id, m) in player.Area.MonstersNear(origin, radius))
                {
                    if (!m.Hostile || m.Dead || m.Life == 0 || m.WorldHidden || m.Invulnerable
                        || ArenaHazards.Untargetable(player, m)) continue;
                    var pos = m.Walk.Position(now);
                    if (Dist(origin, pos) > radius) continue;
                    if (player.Layout.Sight is not null && !MeleeArcSight(player, origin, pos)) continue;
                    seen.Add(id);
                    if (state.AuraMarked.Add(id))
                        player.Area.Buffs.Set(id, new(PlagueBearerAuraBuffRow, player.Id, state.Skill));
                    // Plague Bearer's chaos DoT is unaffected by player damage modifiers (KillNoDamageModifiers);
                    // only enemy chaos resistance and damage-taken modifiers apply.
                    double total = state.AuraCarry.GetValueOrDefault(id)
                        + effectiveDt * dps * MonsterDotTaken(m, DamageType.Chaos, now);
                    int damage = (int)Math.Min(int.MaxValue, Math.Floor(total));
                    state.AuraCarry[id] = total - damage;
                    if (damage <= 0) continue;
                    int lifeDamage = ApplyMonsterDotEnergyShieldLocked(player.Area, m, damage, chaos: true, now);
                    bool stageStarts = false;
                    if (lifeDamage > 0) (m.Life, stageStarts) = TakeOwnerDamageLocked(player, m, lifeDamage);
                    m.Aggro = m.Life > 0;
                    if (m.Life == 0)
                    {
                        m.Dead = true;
                        CleanupDeadMonsterLocked(player.Area, id, m);
                        CollectVaalSoulAtDeathLocked(player, m);
                        if (state.AuraMarked.Remove(id))
                            player.Area.Buffs.Clear(id, PlagueBearerAuraBuffRow, player.Id);
                        state.AuraCarry.Remove(id);
                        kills.Add((id, m));
                    }
                    if (stageStarts) CommitBossStage(player, id, m, now);
                }
            }
            foreach (uint id in state.AuraMarked.ToArray())
            {
                if (seen.Contains(id)) continue;
                state.AuraMarked.Remove(id);
                state.AuraCarry.Remove(id);
                player.Area.Buffs.Clear(id, PlagueBearerAuraBuffRow, player.Id);
            }
            if (state.Stored <= 1e-6)
            {
                state.Stored = 0;
                SwitchPlagueBearerToIncubatingLocked(player, state, now);
            }
            else
            {
                PresentPlagueBearerBuffsLocked(player, state);
            }
        }
        foreach (var (id, m) in kills)
        {
            SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
            await MonsterKilled(player, id, m, byHit: false);
        }
    }

    static void AddPlagueBearerStats(Player player, Dictionary<uint, int> stats)
    {
        if (!PlagueBearers.TryGetValue(player, out var state) || state.Mode == PlagueBearerMode.Off || player.PlayerLife <= 0) return;
        if (state.Epoch != player.ProjectileEpoch || !ReferenceEquals(state.Rules, CombatLifetime)) return;
        if (state.Mode == PlagueBearerMode.Incubating && state.Stored < state.MaxStored - 1e-6)
        {
            int less = (int)Math.Round(state.LessPoisonPct);
            if (less != 0)
                stats[Stat.CorrosiveShroudPoisonDamagePlusPctFinalWhileAccumulatingPoison] = less;
            int chaosTaken = stats.GetValueOrDefault(Stat.PlagueBearerChaosDamageTakenPlusPctWhileIncubating);
            if (chaosTaken != 0)
                stats[Stat.ChaosDamageTakenPlusPct] = ActorScale.Saturate(
                    (long)stats.GetValueOrDefault(Stat.ChaosDamageTakenPlusPct) + chaosTaken);
        }
        else if (state.Mode == PlagueBearerMode.Infecting)
        {
            int move = stats.GetValueOrDefault(Stat.PlagueBearerMovementSpeedPlusPctWhileInfecting);
            if (move != 0)
                stats[Stat.BaseMovementVelocityPlusPct] = ActorScale.Saturate(
                    (long)stats.GetValueOrDefault(Stat.BaseMovementVelocityPlusPct) + move);
            int dotMult = stats.GetValueOrDefault(Stat.CorrosiveShroudPoisonDotMultiplierPlusWhileAuraActive);
            if (dotMult != 0)
                stats[Stat.PoisonDotMultiplierPlus] = ActorScale.Saturate(
                    (long)stats.GetValueOrDefault(Stat.PoisonDotMultiplierPlus) + dotMult);
        }
    }
}
