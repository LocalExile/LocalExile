using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string SpiderAspect = "AspectOfTheSpider";
    // Local nearby policy, matching the ordinary aura fallback. The client export provides no radius;
    // this value is not a measurement of the retail Spider range.
    public const int SpiderSimulationRadius = 40;

    public static void SpiderReconcileOwnerLocked(Player player, double now)
    {
        var state = player.Reservations;
        var root = ReservationAura(player, SpiderAspect);
        if (root is null) { state.SpiderOwner = null; state.NextSpiderWeb = 0; return; }
        var wanted = new ReservationState.CatAspectOwner(root.SkillId, root.Buff);
        if (state.SpiderOwner == wanted) return;
        var raw = StatSourceValues(player);
        int interval = AspectInput(player, raw, Stat.SpiderAspectWebIntervalMsOverride);
        if (interval <= 0) interval = ReservationStat(player, root, Stat.SpiderAspectWebIntervalMs);
        state.SpiderOwner = wanted;
        state.NextSpiderWeb = now + Math.Max(.05, interval / 1000d);
    }

    static void EndSpiderAspectLocked(Player player, AuraCatalog.Aura root)
    {
        if (player.Reservations.SpiderOwner != new ReservationState.CatAspectOwner(root.SkillId, root.Buff)) return;
        player.Reservations.SpiderOwner = null;
        player.Reservations.NextSpiderWeb = 0;
        // Existing recipient webs keep their earned expiry when a reservation or its equipment is removed.
    }

    public static void SpiderAspectTickLocked(Player player, double now)
    {
        if (!CurrentGameRules || player.Closed || player.LeavingArea || player.Paused) return;
        var state = player.Reservations;
        SpiderReconcileOwnerLocked(player, now);
        var root = ReservationAura(player, SpiderAspect);
        if (root is null) { state.SpiderOwner = null; state.NextSpiderWeb = 0; }
        else
        {
            var wanted = new ReservationState.CatAspectOwner(root.SkillId, root.Buff);
            var source = SupportedSkill(player, root.SkillId);
            if (source.RequirementsMet && source.Effect == root.Effect && SkillsFor(player).EffectId(source.Effect) == SpiderAspect)
            {
                var playerStats = SkillArea.WithoutPlayerBase(new(CharacterPlayerStats(player)), (SkillArea.BaseIncreased));
                int Get(uint n) => GolemStat(playerStats, n) + GolemStat(source.Stats, n);
                int intervalMs = Get(Stat.SpiderAspectWebIntervalMsOverride);
                if (intervalMs <= 0) intervalMs = ReservationStat(player, root, Stat.SpiderAspectWebIntervalMs);
                double interval = Math.Max(.05, intervalMs / 1000d);
                if (state.SpiderOwner != wanted)
                { state.SpiderOwner = wanted; state.NextSpiderWeb = now + interval; }
                if (now + 1e-9 >= state.NextSpiderWeb)
                {
                    // A late tick cannot apply past pulses to enemies which have only just entered range.
                    state.NextSpiderWeb = now + interval;
                    int maximum = ReservationStat(player, root, Stat.SpiderAspectMaxWebCount) + Get(Stat.AspectOfTheSpiderWebCountPlus);
                    double seconds = SkillDurationModifiers.Seconds(
                        ReservationStat(player, root, Stat.BaseSkillEffectDuration),
                        n => GolemStat(playerStats, n) + (n == Stat.SkillEffectDurationPlusPct ? Get(Stat.SpiderAspectDebuffDurationPlusPct) : 0),
                        n => GolemStat(source.Stats, n));
                    int taken = (int)Math.Round(ReservationStat(player, root, Stat.BaseDamageTakenPlusPct)
                        * Math.Max(0, 1 + Get(Stat.AspectOfTheSpiderDebuffEffectPlusPct) / 100d));
                    double radius = SkillArea.Radius(SpiderSimulationRadius,
                        n => n == Stat.BaseSkillAreaOfEffectPlusPct ? Get(n) + Get(Stat.SpiderAspectSkillAreaOfEffectPlusPct) : Get(n),
                        spell: true);
                    var here = player.Walk.Position(now);
                    foreach (var (id, monster) in player.Area.Monsters)
                        if (monster is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                            && !MonsterAsleep(player, id) && Dist(here, monster.Walk.Position(now)) <= radius)
                        {
                            player.Area.HasSpiderWebs = true;
                            monster.SpiderWebs.Apply(player.Id, now, seconds, maximum, taken,
                                Get(Stat.ResistAllPctForEnemiesYouInflictSpidersWebUpon),
                                Get(Stat.DamagePlusPctForEnemiesYouInflictSpidersWebUpon));
                        }
                }
            }
            else { state.SpiderOwner = null; state.NextSpiderWeb = 0; }
        }
        if (root is null && !player.Area.HasSpiderWebs) return;
        bool anyWebs = false;
        foreach (var (id, monster) in player.Area.Monsters)
        {
            var webs = monster.SpiderWebs;
            if (webs.Webs.Count == 0 && webs.PresentedCount == 0 && webs.PresentedBuff == 0
                && webs.HinderBuff == 0 && webs.ShroudBuff == 0) continue;
            if (monster.Dead || monster.Life == 0) webs.Webs.Clear();
            webs.Expire(now);
            int count = webs.Count(now);
            if (count > 0 || webs.Webs.Count > 0) anyWebs = true;
            double until = webs.Until(now);
            // The webs' count, and the countdown to the last one's end (a new web moves it).
            if (count > 0 && (count != webs.PresentedCount || until != webs.PresentedUntil))
                player.Area.Buffs.Set(id, new(1043, player.Id, state.SpiderOwner?.Skill ?? 0,
                    Stacks: (ushort)Math.Min(ushort.MaxValue, count), U68: Buff.Seconds(until - now)));
            else if (count == 0) player.Area.Buffs.Clear(id, 1043, player.Id);
            webs.PresentedBuff = (ushort)(count > 0 ? 1 : 0);
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
                SetStats(player, id, changes);

            void Present(ref ushort buff, bool on, ushort row)
            {
                buff = (ushort)(on ? 1 : 0);
                if (on) player.Area.Buffs.Keep(id, new(row, player.Id)); else player.Area.Buffs.Clear(id, row, player.Id);
            }
        }
        player.Area.HasSpiderWebs = anyWebs;
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

    public static void SpiderHitRewardsLocked(Player player, SpawnedMonster target, double now,
        IReadOnlyDictionary<uint, int>? playerStats = null, Random? random = null)
    {
        if (!target.Hostile || target.SpiderWebs.Count(now) == 0 || player.PlayerLife == 0) return;
        playerStats ??= CharacterPlayerStats(player);
        int Get(uint n) => GolemStat(playerStats, n);
        int gain = Get(Stat.EnergyShieldGainWhenYouHitEnemyAffectedBySpidersWeb);
        if (gain > 0) player.EnergyShield.Recover((uint)gain);
        if (RandomRolls.Chance(Get(Stat.ChanceToGainPowerChargeOnHittingEnemyAffectedBySpidersWebPct),
                100, random ?? AreaRng(player)))
            ChargesOf(player).Enqueue(new ChargeEvent.Grant(ChargeType.Power, 1, "Spider's Web hit"));
    }
}
