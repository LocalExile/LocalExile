using GameServer.Core.World;

public static partial class Game
{
    sealed class InfernalLegionField
    {
        public double LastAt;
        public readonly HashSet<SpawnedMonster> Burning = [];
        public readonly Dictionary<uint, (SpawnedMonster Target, double Remainder)> Carry = new();
    }

    sealed class InfernalLegionSelfBurn { public double Carry; }

    static readonly RuleTable<Player, InfernalLegionField> InfernalLegionFields = new();
    static readonly RuleTable<GolemState.Golem, InfernalLegionSelfBurn> InfernalLegionSelfBurns = new();
    static readonly HashSet<int> MinionAreaDamageTags = [7, 28];

    static double MinionFireDamage(GolemState.Golem minion, double basis, bool damageOverTime) =>
        basis * MinionAreaDamageFactor(minion.Monster, DamageType.Fire, minion.Own, damageOverTime);

    static double MinionAreaDamageFactor(SpawnedMonster minion, DamageType type,
        IReadOnlyDictionary<uint, int>? skill, bool damageOverTime = false)
    {
        var effects = minion.Effects;
        long increased = (long)effects[Stat.DamagePlusPct] + effects[Stat.AreaDamagePlusPct]
            + (type == DamageType.Fire ? (long)effects[Stat.FireDamagePlusPct] + effects[Stat.ElementalDamagePlusPct]
                : effects[Stat.ChaosDamagePlusPct]);
        if (damageOverTime) increased += (long)effects[Stat.DamageOverTimePlusPct] + effects[Stat.BurnDamagePlusPct];
        int Own(uint stat) => skill?.GetValueOrDefault(stat) ?? 0;
        if (DamageStats.Prohibited(type, Own)) return 0;
        var support = skill?.Select(p => (p.Key, p.Value)) ?? [];
        var (general, typed) = GemSupportDamage.Multipliers(DamageTypes.HistoryOf(type), support, MinionAreaDamageTags);
        double factor = Math.Max(0, 1 + increased / 100d) * general * typed
            * Math.Max(0, 1 + Own(Stat.ActiveSkillMinionDamagePlusPctFinal) / 100d)
            * Math.Max(0, 1 + Own(Stat.SupportMinionDamagePlusPctFinal) / 100d)
            * Math.Max(0, 1 + effects[Stat.NecromancerMinionDamagePlusPctFinal] / 100d);
        if (damageOverTime)
            factor *= Math.Max(0, 1 + (effects[Stat.DotMultiplierPlus] + effects[Stat.FireDotMultiplierPlus]) / 100d);
        return factor;
    }

    static double MinionFireRadius(GolemState.Golem minion, double basis) =>
        basis * Math.Sqrt(Math.Max(0, 1 + minion.Monster.Effects[Stat.BaseSkillAreaOfEffectPlusPct] / 100d));

    internal static int InfernalLegionSelfDamage(GolemState.Golem minion, double elapsed, double taken)
    {
        int coefficient = GolemStat(minion.Own, Stat.MinionFireDamagePctOfMaximumLifeTakenPerMinute);
        if (coefficient <= 0 || elapsed <= 0 || taken <= 0) return 0;
        var state = InfernalLegionSelfBurns.GetOrCreateValue(minion);
        double total = state.Carry + minion.Monster.MaxLife * (coefficient / 6000d) * elapsed * taken
            * Math.Max(0, 1 + GolemStat(minion.Own, Stat.SupportMinionDefensiveStanceMinionDamageTakenPlusPctFinal) / 100d);
        int damage = (int)Math.Min(int.MaxValue, Math.Floor(total));
        state.Carry = total - Math.Floor(total);
        return damage;
    }

    internal static void InfernalLegionTickLocked(Player player, double now)
    {
        List<(uint Owner, GolemState.Golem Minion, (int X, int Y) At, double Radius, double Dps)>? fields = null;
        void AddFields(Player owner)
        {
            if (owner.PlayerLife == 0 || InTown(owner)) return;
            foreach (var minion in UtilityFighters(owner))
            {
                int basis = GolemStat(minion.Own, Stat.SupportMinionInstabilityMinionBaseFireAreaDamagePerMinute);
                if (basis <= 0) continue;
                double dps = MinionFireDamage(minion, basis / 60d, damageOverTime: true);
                if (dps <= 0) continue;
                var at = minion.Monster.Walk.Position(now);
                double radius = MinionFireRadius(minion, 15 + GolemStat(minion.Own, Stat.InfernalLegionMinionsHaveBurningEffectRadiusPlus));
                (fields ??= []).Add((owner.Id, minion, at, radius, dps));
            }
        }
        AddFields(player);
        if (fields is null)
        {
            InfernalLegionFields.Remove(player);
            return;
        }
        if (!InfernalLegionFields.TryGetValue(player, out var state))
        {
            InfernalLegionFields.GetOrCreateValue(player).LastAt = now;
            return;
        }
        double elapsed = Math.Max(0, now - state.LastAt);
        state.LastAt = Math.Max(state.LastAt, now);
        if (elapsed == 0) return;
        state.Burning.Clear();
        foreach (var owner in player.Instance?.PlayersExcept(player.Id) ?? [])
            if (!owner.Closed && owner.Instance == player.Instance) AddFields(owner);
        fields.Sort((a, b) => a.Dps == b.Dps ? a.Owner.CompareTo(b.Owner) : b.Dps.CompareTo(a.Dps));
        foreach (var (id, target) in player.Area.Monsters.ToArray())
        {
            if (!target.Hostile || target.Dead || target.Life == 0 || target.Invulnerable || target.Asleep) continue;
            var at = target.Walk.Position(now);
            var field = fields.FirstOrDefault(f => Dist(f.At, at) <= f.Radius + LivingCollision.Size(target));
            if (field.Dps <= 0 || field.Owner != player.Id) continue;
            state.Burning.Add(target);
            double total = field.Dps * elapsed * MonsterDotTaken(target, DamageType.Fire, now);
            if (state.Carry.TryGetValue(id, out var carry) && ReferenceEquals(carry.Target, target)) total += carry.Remainder;
            int damage = (int)Math.Min(int.MaxValue, Math.Floor(total));
            state.Carry[id] = (target, total - Math.Floor(total));
            if (damage == 0) continue;
            int lifeDamage = ApplyMonsterDotEnergyShieldLocked(player.Area, target, damage, chaos: false, AreaTime(player));
            bool stageStarts = false;
            if (lifeDamage > 0) (target.Life, stageStarts) = TakeOwnerDamageLocked(player, target, lifeDamage);
            target.Aggro = target.Life > 0;
            if (target.Life == 0)
            {
                target.Dead = true;
                CleanupDeadMonsterLocked(player.Area, id, target);
                CollectVaalSoulAtDeathLocked(player, target);
                OwnedKillLocked(target, 0);
                TriggerElementalRelicLocked(player, target, hit: false, killed: true, field.At);
            }
            if (stageStarts) CommitBossStage(player, id, target, now);
            CommitAcceptedHitLocked(player, id, target, new(target.Life, false), damage, now,
                "Infernal Legion", playerKill: false, damageOverTime: true);
            if (target.Life == 0)
                ResolveCursedMinionKillLocked(player, id, target, field.Minion.Monster, field.Minion.Own, now);
        }
        foreach (var (id, carry) in state.Carry.ToArray())
            if (carry.Target.Dead || player.Area.Monsters.GetValueOrDefault(id) != carry.Target) state.Carry.Remove(id);
    }

    internal static bool MinionInstabilityLocked(Player player, GolemState.Golem minion, double now)
    {
        var monster = minion.Monster;
        if (monster.Dead || monster.Life == 0 || monster.Life * 2L > monster.MaxLife || GolemIgnoresDots(minion)) return false;
        int percent = GolemStat(minion.Player, Stat.MinionsExplodeOnLowLifeMaximumLifePctToDeal);
        if (GolemStat(minion.Player, Stat.KeystoneMinionInstability) > 0) percent = Math.Max(percent, 33);
        if (percent <= 0) return false;
        var at = monster.Walk.Position(now);
        double radius = MinionFireRadius(minion, 22);
        double raw = MinionFireDamage(minion, monster.MaxLife * (percent / 100d), damageOverTime: false);
        var damagePacket = DamagePacket.Of(DamageType.Fire, raw, raw);
        GolemDieLocked(player, minion, now);
        foreach (var (id, target) in player.Area.Monsters.ToArray())
        {
            if (!target.Hostile || target.Dead || target.Life == 0 || target.Invulnerable || target.Asleep
                || Dist(target.Walk.Position(now), at) > radius + LivingCollision.Size(target)) continue;
            var mitigated = DamageModel.Mitigate(damagePacket, type => MonsterHitResistance(target, type, default, now));
            int damage = MonsterTakesMitigated(target, mitigated, spell: false, now);
            if (damage > 0) CommitMinionFireDamage(player, id, target, damage, at, now, "Minion Instability", minion);
        }
        return true;
    }

    static void CommitMinionFireDamage(Player player, uint id, SpawnedMonster target, int damage,
        (int X, int Y) at, double now, string effect, GolemState.Golem minion)
    {
        var cull = MinionIgniteKillRule(minion.Player, target, now);
        if (ApplyMonsterHitLocked(player, target, damage, allyAt: at, cull: cull) is not { } hit) return;
        if (hit.StageStarts)
        {
            CommitBossStage(player, id, target, now);
            hit = hit with { StageStarts = false };
        }
        CommitMinionHitLocked(player, id, target, hit, damage, now, effect, source: minion.Monster, skill: minion.Own);
    }
}
