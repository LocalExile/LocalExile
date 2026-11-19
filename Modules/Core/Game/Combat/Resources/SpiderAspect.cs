using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string SpiderAspect = "AspectOfTheSpider";
    // Local nearby policy, matching the ordinary aura fallback. The client export provides no radius;
    // this value is not a measurement of the retail Spider range.
    public const int SpiderSimulationRadius = 40;

    public static void SpiderReconcileOwnerLocked(Player conn, double now)
    {
        var state = conn.Reservations;
        var root = ReservationAura(conn, SpiderAspect);
        if (root is null) { state.SpiderOwner = null; state.NextSpiderWeb = 0; return; }
        var wanted = new ReservationState.CatAspectOwner(root.SkillId, root.Buff);
        if (state.SpiderOwner == wanted) return;
        var raw = CharacterStatSources(conn).Stats;
        int interval = AspectInput(conn, raw, Stat.SpiderAspectWebIntervalMsOverride);
        if (interval <= 0) interval = ReservationStat(conn, root, Stat.SpiderAspectWebIntervalMs);
        state.SpiderOwner = wanted;
        state.NextSpiderWeb = now + Math.Max(.05, interval / 1000d);
    }

    static void EndSpiderAspectLocked(Player conn, AuraCatalog.Aura root)
    {
        if (conn.Reservations.SpiderOwner != new ReservationState.CatAspectOwner(root.SkillId, root.Buff)) return;
        conn.Reservations.SpiderOwner = null;
        conn.Reservations.NextSpiderWeb = 0;
        // Existing recipient webs keep their earned expiry when a reservation or its equipment is removed.
    }

    public static void SpiderAspectTickLocked(Player conn, double now)
    {
        if (!CurrentGameRules || conn.Closed || conn.LeavingArea || conn.Paused) return;
        var state = conn.Reservations;
        SpiderReconcileOwnerLocked(conn, now);
        var root = ReservationAura(conn, SpiderAspect);
        if (root is null) { state.SpiderOwner = null; state.NextSpiderWeb = 0; }
        else
        {
            var wanted = new ReservationState.CatAspectOwner(root.SkillId, root.Buff);
            var source = SupportedSkill(conn, root.SkillId);
            if (source.RequirementsMet && source.Effect == root.Effect && SkillsFor(conn).EffectId(source.Effect) == SpiderAspect)
            {
                var player = SkillArea.WithoutPlayerBase(new(CharacterPlayerStats(conn)), (SkillArea.BaseIncreased));
                int Get(uint n) => GolemStat(player, n) + GolemStat(source.Stats, n);
                int intervalMs = Get(Stat.SpiderAspectWebIntervalMsOverride);
                if (intervalMs <= 0) intervalMs = ReservationStat(conn, root, Stat.SpiderAspectWebIntervalMs);
                double interval = Math.Max(.05, intervalMs / 1000d);
                if (state.SpiderOwner != wanted)
                { state.SpiderOwner = wanted; state.NextSpiderWeb = now + interval; }
                if (now + 1e-9 >= state.NextSpiderWeb)
                {
                    // A late tick cannot apply past pulses to enemies which have only just entered range.
                    state.NextSpiderWeb = now + interval;
                    int maximum = ReservationStat(conn, root, Stat.SpiderAspectMaxWebCount) + Get(Stat.AspectOfTheSpiderWebCountPlus);
                    double seconds = SkillDurationModifiers.Seconds(
                        ReservationStat(conn, root, Stat.BaseSkillEffectDuration),
                        n => GolemStat(player, n) + (n == Stat.SkillEffectDurationPlusPct ? Get(Stat.SpiderAspectDebuffDurationPlusPct) : 0),
                        n => GolemStat(source.Stats, n));
                    int taken = (int)Math.Round(ReservationStat(conn, root, Stat.BaseDamageTakenPlusPct)
                        * Math.Max(0, 1 + Get(Stat.AspectOfTheSpiderDebuffEffectPlusPct) / 100d));
                    double radius = SkillArea.Radius(SpiderSimulationRadius,
                        n => n == Stat.BaseSkillAreaOfEffectPlusPct ? Get(n) + Get(Stat.SpiderAspectSkillAreaOfEffectPlusPct) : Get(n),
                        spell: true);
                    var here = conn.Walk.Position(now);
                    foreach (var (id, monster) in conn.Monsters)
                        if (monster is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                            && !conn.AsleepMonsters.Contains(id) && Dist(here, monster.Walk.Position(now)) <= radius)
                            monster.SpiderWebs.Apply(conn.PeerId, now, seconds, maximum, taken,
                                Get(Stat.ResistAllPctForEnemiesYouInflictSpidersWebUpon),
                                Get(Stat.DamagePlusPctForEnemiesYouInflictSpidersWebUpon));
                }
            }
            else { state.SpiderOwner = null; state.NextSpiderWeb = 0; }
        }
        foreach (var (id, monster) in conn.Monsters)
        {
            var webs = monster.SpiderWebs;
            if (webs.Webs.Count == 0 && webs.PresentedCount == 0 && webs.PresentedBuff == 0
                && webs.HinderBuff == 0 && webs.ShroudBuff == 0) continue;
            if (monster.Dead || monster.Life == 0) webs.Webs.Clear();
            webs.Expire(now);
            int count = webs.Count(now);
            double until = webs.Until(now);
            if (webs.PresentedBuff != 0 && (count != webs.PresentedCount || until != webs.PresentedUntil))
            {
                conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(id, webs.PresentedBuff), null, id, monster, WorldId: id));
                webs.PresentedBuff = 0;
            }
            if (count > 0 && webs.PresentedBuff == 0)
            {
                webs.PresentedBuff = conn.NextBuff++;
                conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(id, webs.PresentedBuff, 1043, PlayerId,
                    state.SpiderOwner?.Skill ?? 0, stacks: (ushort)Math.Min(ushort.MaxValue, count),
                    u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, until - now))), "Spider's Web", id, monster, WorldId: id));
            }
            webs.PresentedCount = count;
            webs.PresentedUntil = until;
            Present(ref webs.HinderBuff, SpiderHinder(monster, now) != 0, 505);
            Present(ref webs.ShroudBuff, count > 0 && (webs.Resistance(now) != 0 || webs.Damage(now) != 0), 1092);
            var changes = new List<(uint, int)>();
            foreach (uint name in new[] { Stat.BaseDamageTakenPlusPct, Stat.DamagePlusPct, Stat.IsHindered, Stat.BaseFireDamageResistancePct,
                         Stat.BaseColdDamageResistancePct, Stat.BaseLightningDamageResistancePct, Stat.BaseChaosDamageResistancePct })
            {
                if (!(name is var key)) continue;
                int value = monster.Effects[name] + SpiderRecipientStat(monster, name, now);
                if (webs.PresentedStats.GetValueOrDefault(key) != value) changes.Add((key, value));
                webs.PresentedStats[key] = value;
            }
            if (changes.Count > 0)
                conn.Minions.OutputQueue.Add(new(InstanceEntities.StatsUpdate(id, changes), "Spider recipient stats", id, monster, WorldId: id));

            void Present(ref ushort buff, bool on, ushort row)
            {
                if (buff != 0 && !on)
                { conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(id, buff), null, id, monster, WorldId: id)); buff = 0; }
                if (buff == 0 && on)
                { buff = conn.NextBuff++; conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(id, buff, row, PlayerId), null, id, monster, WorldId: id)); }
            }
        }
    }

    public static int SpiderHinder(SpawnedMonster target, double now) => target.SpiderWebs.Count(now) > 0
        && target.Effects[Stat.ImmuneToHinder] == 0 && target.Effects[Stat.YouCannotBeHindered] == 0
        ? (int)Math.Max(0, Math.Round(30 * (1 + target.Effects[Stat.HinderEffectOnSelfPlusPct] / 100d))) : 0;

    static readonly uint BaseResistAll = Stat.Of("base_resist_all_%");

    public static int SpiderRecipientStat(SpawnedMonster target, uint name, double now) => name switch
    {
        Stat.BaseDamageTakenPlusPct => target.SpiderWebs.Taken(now),
        Stat.DamagePlusPct => target.SpiderWebs.Damage(now),
        Stat.IsHindered => SpiderHinder(target, now) > 0 ? 1 : 0,
        Stat.BaseFireDamageResistancePct or Stat.BaseColdDamageResistancePct or Stat.BaseLightningDamageResistancePct
            or Stat.BaseChaosDamageResistancePct => target.SpiderWebs.Resistance(now),
        _ when name == BaseResistAll => target.SpiderWebs.Resistance(now),
        _ => 0,
    };

    public static double SpiderOutgoingFactor(SpawnedMonster source, double now)
    {
        int reduction = source.SpiderWebs.Damage(now);
        double increased = 100d + source.Effects[Stat.DamagePlusPct] + source.Effects[Stat.PhysicalDamagePlusPct];
        return reduction == 0 ? 1 : increased > 0 ? Math.Max(0, increased + reduction) / increased : 0;
    }

    public static void SpiderHitRewardsLocked(Player conn, SpawnedMonster target, double now,
        IReadOnlyDictionary<uint, int>? player = null, Random? random = null)
    {
        if (!target.Hostile || target.SpiderWebs.Count(now) == 0 || conn.PlayerLife == 0) return;
        player ??= CharacterPlayerStats(conn);
        int Get(uint n) => GolemStat(player, n);
        int gain = Get(Stat.EnergyShieldGainWhenYouHitEnemyAffectedBySpidersWeb);
        if (gain > 0) conn.EnergyShield.Recover((uint)gain);
        if (RandomRolls.Chance(Get(Stat.ChanceToGainPowerChargeOnHittingEnemyAffectedBySpidersWebPct),
                100, random ?? Random.Shared))
            ChargesOf(conn).Enqueue(new ChargeEvent.Grant(ChargeType.Power, 1, "Spider's Web hit"));
    }
}
