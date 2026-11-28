using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    internal const string CorruptingFeverEffect = "CorruptingFever";
    internal const ushort CorruptingFeverBuffRow = 1797; // BuffDefinitions blood_surge
    internal const ushort MonsterCorruptedBloodBuffRow = 238; // BuffDefinitions corrupted_blood
    const int MonsterCorruptedBloodMaxStacks = 10;

    sealed class CorruptingFeverState
    {
        public bool Active;
        public ushort Skill;
        public int Effect, Epoch;
        public double Until, Duration;
        public long LifeSpent, RefreshThreshold;
        public object? Rules;
        public GemSourceLifetimes.SourceLifetime? Source;
    }

    sealed class MonsterCorruptedBloodState
    {
        public readonly List<double> Stacks = new(MonsterCorruptedBloodMaxStacks);
        public double Until, Settled, Accrued, Carry;
        public ushort Skill;
        public uint SourcePlayer;
        public bool Shown;

        public bool ActiveAt(double now) => Stacks.Count > 0 && now < Until;

        public void Settle(double now)
        {
            if (now <= Settled) return;
            double end = Math.Min(now, Until);
            if (end > Settled && Stacks.Count > 0)
            {
                double totalDps = 0;
                for (int i = 0; i < Stacks.Count; i++) totalDps += Stacks[i];
                Accrued += (end - Settled) * totalDps;
            }
            Settled = now;
            if (now >= Until) Stacks.Clear();
        }

        public void Clear()
        {
            Stacks.Clear();
            Until = Settled = Accrued = Carry = 0;
            Shown = false;
        }
    }

    static readonly RuleTable<Player, CorruptingFeverState> CorruptingFevers = new();
    static readonly RuleTable<SpawnedMonster, MonsterCorruptedBloodState> MonsterCorruptedBloods = new();
    static readonly RuleTable<GameArea, HashSet<uint>> ActiveCorruptedBloodAreas = new();

    static readonly uint[] CorruptingFeverSupportMore =
    [
        Stat.SupportBrutalityPhysicalDamagePlusPctFinal,
        Stat.SupportEfficacyDamageOverTimePlusPctFinal,
        Stat.SupportRapidDecayDamageOverTimePlusPctFinal,
        Stat.SupportReducedDurationDamagePlusPctFinal,
        Stat.CrueltyDamageOverTimePlusPctFinal,
    ];

    internal static bool CorruptingFeverActiveLocked(Player player, double now)
    {
        if (!CorruptingFevers.TryGetValue(player, out var state) || !state.Active) return false;
        if (!CurrentGameWork || state.Epoch != player.ProjectileEpoch || !ReferenceEquals(state.Rules, CombatLifetime)
            || player.Closed || player.LeavingArea || player.PendingTransfer is not null || player.PlayerLife == 0
            || now >= state.Until
            || !GemSourceLifetimeCurrentLocked(player, state.Skill, state.Effect, state.Source))
        {
            EndCorruptingFeverLocked(player, state);
            return false;
        }
        return true;
    }

    internal static double CorruptingFeverDuration(int baseMs, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int> own) =>
        SkillDurationModifiers.Seconds(baseMs,
            key => playerStats.GetValueOrDefault(key)
                + (key == SkillDurationModifiers.IncreasedDuration ? playerStats.GetValueOrDefault(Stat.CorruptingFeverDurationPlusPct) : 0),
            own.GetValueOrDefault);

    static bool StartCorruptingFeverLocked(Player player, ushort skillId, double now)
    {
        var gem = SupportedSkill(player, skillId);
        if (SkillsFor(player).EffectId(gem.Effect) != CorruptingFeverEffect) return false;
        var source = CaptureGemSourceLifetimeLocked(player, skillId, gem.Effect);
        if (source is null) return false;
        var playerStats = CharacterPlayerStats(player);
        double duration = CorruptingFeverDuration(gem.Stats.GetValueOrDefault(Stat.BaseSkillEffectDuration), playerStats, gem.Stats);
        if (duration <= 0) return false;

        var state = CorruptingFevers.GetValue(player, static _ => new());
        state.Active = true;
        state.Skill = skillId;
        state.Effect = gem.Effect;
        state.Epoch = player.ProjectileEpoch;
        state.Rules = CombatLifetime;
        state.Source = source;
        state.Duration = duration;
        state.Until = now + duration;
        state.LifeSpent = 0;
        state.RefreshThreshold = Math.Max(0, gem.Stats.GetValueOrDefault(Stat.BloodSurgeRefreshOnTotalLifeSpent));
        PresentCorruptingFeverBuffLocked(player, state, now);
        return true;
    }

    static void PresentCorruptingFeverBuffLocked(Player player, CorruptingFeverState state, double now)
    {
        var activeSkill = SkillsFor(player).ActionHash(state.Effect) is ushort action
            ? (state.Skill, action) : ((ushort, ushort)?)null;
        player.Area.Buffs.Set(player.Id, new(CorruptingFeverBuffRow, player.Id, state.Skill,
            U68: Buff.Seconds(Math.Max(0, state.Until - now)), ActiveSkill: activeSkill));
    }

    static void EndCorruptingFeverLocked(Player player, CorruptingFeverState state)
    {
        if (!state.Active) return;
        state.Active = false;
        state.Until = 0;
        state.LifeSpent = 0;
        player.Area.Buffs.Clear(player.Id, CorruptingFeverBuffRow, player.Id);
    }

    internal static void ClearCorruptingFeverLocked(Player player, IReadOnlyCollection<ushort>? onlyIds = null)
    {
        if (!CorruptingFevers.TryGetValue(player, out var state) || !state.Active) return;
        if (onlyIds is not null && !onlyIds.Contains(state.Skill)) return;
        EndCorruptingFeverLocked(player, state);
    }

    internal static bool CancelCorruptingFeverLocked(Player player, Buff buff)
    {
        if (buff.Row != CorruptingFeverBuffRow || buff.Source != player.Id
            || !CorruptingFevers.TryGetValue(player, out var state) || !state.Active) return false;
        EndCorruptingFeverLocked(player, state);
        return true;
    }

    internal static void TickCorruptingFeverLocked(Player player, double now)
    {
        _ = CorruptingFeverActiveLocked(player, now);
    }

    internal static void CorruptingFeverOnLifeSpentLocked(Player player, uint lifeSpent, ushort? skillId, double now)
    {
        if (lifeSpent == 0 || !CorruptingFevers.TryGetValue(player, out var state) || !state.Active) return;
        if (!CorruptingFeverActiveLocked(player, now)) return;
        if (skillId is ushort castSkill && castSkill == state.Skill) return;
        if (state.RefreshThreshold <= 0) return;
        state.LifeSpent += lifeSpent;
        if (state.LifeSpent < state.RefreshThreshold) return;
        state.LifeSpent = 0;
        state.Until = now + state.Duration;
        PresentCorruptingFeverBuffLocked(player, state, now);
    }

    internal static void CorruptingFeverOnHitLocked(Player player, uint targetId, SpawnedMonster monster,
        IReadOnlyDictionary<uint, int> playerStats, double now, Random random)
    {
        if (!CorruptingFevers.TryGetValue(player, out var state) || !state.Active) return;
        if (!CorruptingFeverActiveLocked(player, now)) return;
        if (!monster.Hostile || monster.Dead || monster.Life == 0 || monster.Invulnerable
            || monster.Effects[Stat.ImmuneToCorruptedBlood] != 0
            || monster.Effects[Stat.CannotBeInflictedByCorruptedBlood] != 0) return;

        var gem = SupportedSkill(player, state.Skill);
        var own = gem.Stats;
        int Own(uint key) => own.GetValueOrDefault(key);
        int Get(uint key) => ActorScale.Saturate((long)playerStats.GetValueOrDefault(key) + Own(key));
        if (DamageStats.Prohibited(DamageType.Physical, Get)) return;
        double baseDps = Own(Stat.BasePhysicalDamageToDealPerMinute) / 60d;
        if (baseDps <= 0) return;

        double increased = (double)Get(Stat.DamagePlusPct) + Get(Stat.PhysicalDamagePlusPct)
            + Get(Stat.DamageOverTimePlusPct) + Get(Stat.PhysicalDamageOverTimePlusPct)
            + Get(Stat.CorruptingFeverDamagePlusPct);
        double more = ChargeDamageMore(playerStats)
            * Math.Max(0, 1 + playerStats.GetValueOrDefault(Stat.DeliriumAuraDamageOverTimePlusPctFinal) / 100d)
            * Math.Max(0, 1 + playerStats.GetValueOrDefault(Stat.HeraldOfPurityPhysicalDamagePlusPctFinal) / 100d);
        foreach (uint stat in CorruptingFeverSupportMore)
            if (Own(stat) is int pct and not 0) more *= Math.Max(0, 1 + pct / 100d);
        if (player.Lifetap.Until > now && Own(Lifetap.DamageStat) is int lifetap and not 0)
            more *= Math.Max(0, 1 + lifetap / 100d);
        double dotMult = Math.Max(0, 1 + ((double)Get(Stat.DotMultiplierPlus) + Get(Stat.PhysicalDotMultiplierPlus)) / 100d);
        double dps = baseDps * Math.Max(0, 1 + increased / 100d) * more * dotMult;
        if (dps <= 0) return;

        double baseSeconds = CorruptingFeverDuration(Own(Stat.BaseSecondarySkillEffectDuration), playerStats, own);
        if (baseSeconds <= 0) return;
        double duration = baseSeconds / Math.Max(0.01, monster.RecipientDebuffRate);

        int count = 1 + (RandomRolls.Chance(Get(Stat.CorruptingFeverApplyAdditionalCorruptedBloodPct), 100, random) ? 1 : 0);
        var cb = MonsterCorruptedBloods.GetValue(monster, static _ => new());
        cb.Settle(now);
        for (int i = 0; i < count; i++)
        {
            if (cb.Stacks.Count < MonsterCorruptedBloodMaxStacks) cb.Stacks.Add(dps);
            else
            {
                int weakest = 0;
                for (int j = 1; j < cb.Stacks.Count; j++)
                    if (cb.Stacks[j] < cb.Stacks[weakest]) weakest = j;
                if (cb.Stacks[weakest] < dps) cb.Stacks[weakest] = dps;
            }
        }
        cb.Until = now + duration;
        cb.Settled = now;
        cb.Skill = state.Skill;
        cb.SourcePlayer = player.Id;
        cb.Shown = true;
        ActiveCorruptedBloodAreas.GetValue(player.Area, static _ => []).Add(targetId);
        player.Area.Buffs.Set(targetId, new(MonsterCorruptedBloodBuffRow, player.Id, state.Skill,
            U68: Buff.Seconds(duration), Stacks: (ushort)cb.Stacks.Count, KeepInstance: true));
    }

    internal static int MonsterCorruptedBloodStacks(SpawnedMonster monster, double now) =>
        MonsterCorruptedBloods.TryGetValue(monster, out var cb) && cb.ActiveAt(now) ? cb.Stacks.Count : 0;

    internal static bool HasCorruptedBloodDamage(GameArea area) =>
        ActiveCorruptedBloodAreas.TryGetValue(area, out var active) && active.Count > 0;

    internal static async Task TickMonsterCorruptedBlood(Player player)
    {
        if (!ActiveCorruptedBloodAreas.TryGetValue(player.Area, out var active) || active.Count == 0) return;
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage)>();
        using (ProjectileMutation(player))
        {
            if (player.Closed || player.LeavingArea || active.Count == 0) return;
            double now = ProjectileWorldTime(player);
            List<uint>? done = null;
            foreach (uint id in active)
            {
                if (!player.Area.Monsters.TryGetValue(id, out var m) || OwnedGolemActor(player, id, m)
                    || !MonsterCorruptedBloods.TryGetValue(m, out var cb))
                {
                    (done ??= []).Add(id);
                    continue;
                }
                if (m.Dead || m.Life == 0)
                {
                    if (cb.Shown) player.Area.Buffs.Clear(id, MonsterCorruptedBloodBuffRow, cb.SourcePlayer);
                    cb.Clear();
                    (done ??= []).Add(id);
                    continue;
                }
                cb.Settle(now);
                double raw = cb.Accrued;
                cb.Accrued = 0;
                int damage = 0;
                if (raw > 0 && !m.Invulnerable)
                {
                    double total = cb.Carry + raw * MonsterDotTaken(m, DamageType.Physical, now);
                    damage = (int)Math.Min(int.MaxValue, Math.Floor(total));
                    cb.Carry = total - damage;
                }
                if (cb.Stacks.Count == 0)
                {
                    if (cb.Shown)
                    {
                        player.Area.Buffs.Clear(id, MonsterCorruptedBloodBuffRow, cb.SourcePlayer);
                        cb.Shown = false;
                    }
                    cb.Carry = 0;
                    (done ??= []).Add(id);
                }
                if (damage == 0) continue;
                int lifeDamage = ApplyMonsterDotEnergyShieldLocked(player.Area, m, damage, chaos: false, AreaTime(player));
                bool stageStarts = false;
                if (lifeDamage > 0) (m.Life, stageStarts) = TakeOwnerDamageLocked(player, m, lifeDamage);
                m.Aggro = m.Life > 0;
                if (m.Life == 0)
                {
                    m.Dead = true;
                    CleanupDeadMonsterLocked(player.Area, id, m);
                    CollectVaalSoulAtDeathLocked(player, m);
                    if (cb.Shown) player.Area.Buffs.Clear(id, MonsterCorruptedBloodBuffRow, cb.SourcePlayer);
                    cb.Clear();
                    (done ??= []).Add(id);
                }
                hits.Add((id, m, m.Life, damage));
                if (stageStarts) CommitBossStage(player, id, m, now);
            }
            if (done is not null)
                foreach (uint id in done) active.Remove(id);
        }
        foreach (var (id, m, life, _) in hits)
        {
            if (life == 0)
            {
                SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
                await MonsterKilled(player, id, m, byHit: false);
            }
        }
    }
}
