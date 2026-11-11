using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string SpiderAspect = "AspectOfTheSpider";
    // Local nearby policy, matching the ordinary aura fallback. The client export provides no radius;
    // this value is not a measurement of the retail Spider range.
    public const int SpiderSimulationRadius = 40;

    public static void SpiderReconcileOwnerLocked(ClientState conn, double now)
    {
        var state = conn.Reservations;
        var root = ReservationAura(conn, SpiderAspect);
        if (root is null) { state.SpiderOwner = null; state.NextSpiderWeb = 0; return; }
        var wanted = new ReservationState.CatAspectOwner(root.SkillId, root.Buff);
        if (state.SpiderOwner == wanted) return;
        var raw = CharacterStatSources(conn).Stats;
        int interval = AspectInput(conn, raw, "spider_aspect_web_interval_ms_override");
        if (interval <= 0) interval = ReservationStat(conn, root, "spider_aspect_web_interval_ms");
        state.SpiderOwner = wanted;
        state.NextSpiderWeb = now + Math.Max(.05, interval / 1000d);
    }

    static void EndSpiderAspectLocked(ClientState conn, AuraCatalog.Aura root)
    {
        if (conn.Reservations.SpiderOwner != new ReservationState.CatAspectOwner(root.SkillId, root.Buff)) return;
        conn.Reservations.SpiderOwner = null;
        conn.Reservations.NextSpiderWeb = 0;
        // Existing recipient webs keep their earned expiry when a reservation or its equipment is removed.
    }

    public static void SpiderAspectTickLocked(ClientState conn, double now)
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
                var player = SkillArea.WithoutPlayerBase(new(CharacterPlayerStats(conn)), Stats.Key(SkillArea.BaseIncreased));
                int Get(string n) => GolemStat(player, n) + GolemStat(source.Stats, n);
                int intervalMs = Get("spider_aspect_web_interval_ms_override");
                if (intervalMs <= 0) intervalMs = ReservationStat(conn, root, "spider_aspect_web_interval_ms");
                double interval = Math.Max(.05, intervalMs / 1000d);
                if (state.SpiderOwner != wanted)
                { state.SpiderOwner = wanted; state.NextSpiderWeb = now + interval; }
                if (now + 1e-9 >= state.NextSpiderWeb)
                {
                    // A late tick cannot apply past pulses to enemies which have only just entered range.
                    state.NextSpiderWeb = now + interval;
                    int maximum = ReservationStat(conn, root, "spider_aspect_max_web_count") + Get("aspect_of_the_spider_web_count_+");
                    double seconds = SkillDurationModifiers.Seconds(
                        ReservationStat(conn, root, "base_skill_effect_duration"),
                        n => GolemStat(player, n) + (n == "skill_effect_duration_+%" ? Get("spider_aspect_debuff_duration_+%") : 0),
                        n => GolemStat(source.Stats, n));
                    int taken = (int)Math.Round(ReservationStat(conn, root, "base_damage_taken_+%")
                        * Math.Max(0, 1 + Get("aspect_of_the_spider_debuff_effect_+%") / 100d));
                    double radius = SkillArea.Radius(SpiderSimulationRadius,
                        n => n == "base_skill_area_of_effect_+%" ? Get(n) + Get("spider_aspect_skill_area_of_effect_+%") : Get(n),
                        spell: true);
                    var here = conn.Walk.Position(now);
                    foreach (var (id, monster) in conn.Monsters)
                        if (monster is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false, Asleep: false }
                            && !conn.AsleepMonsters.Contains(id) && Dist(here, monster.Walk.Position(now)) <= radius)
                            monster.SpiderWebs.Apply(conn.PeerId, now, seconds, maximum, taken,
                                Get("resist_all_%_for_enemies_you_inflict_spiders_web_upon"),
                                Get("damage_+%_for_enemies_you_inflict_spiders_web_upon"));
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
            foreach (string name in new[] { "base_damage_taken_+%", "damage_+%", "is_hindered", "base_fire_damage_resistance_%",
                         "base_cold_damage_resistance_%", "base_lightning_damage_resistance_%", "base_chaos_damage_resistance_%" })
            {
                if (Stats.Key(name) is not uint key) continue;
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
        && target.Effects["immune_to_hinder"] == 0 && target.Effects["you_cannot_be_hindered"] == 0
        ? (int)Math.Max(0, Math.Round(30 * (1 + target.Effects["hinder_effect_on_self_+%"] / 100d))) : 0;

    public static int SpiderRecipientStat(SpawnedMonster target, string name, double now) => name switch
    {
        "base_damage_taken_+%" => target.SpiderWebs.Taken(now),
        "damage_+%" => target.SpiderWebs.Damage(now),
        "is_hindered" => SpiderHinder(target, now) > 0 ? 1 : 0,
        "base_fire_damage_resistance_%" or "base_cold_damage_resistance_%" or "base_lightning_damage_resistance_%"
            or "base_chaos_damage_resistance_%" or "base_resist_all_%" => target.SpiderWebs.Resistance(now),
        _ => 0,
    };

    public static double SpiderOutgoingFactor(SpawnedMonster source, double now)
    {
        int reduction = source.SpiderWebs.Damage(now);
        double increased = 100d + source.Effects["damage_+%"] + source.Effects["physical_damage_+%"];
        return reduction == 0 ? 1 : increased > 0 ? Math.Max(0, increased + reduction) / increased : 0;
    }

    public static void SpiderHitRewardsLocked(ClientState conn, SpawnedMonster target, double now,
        IReadOnlyDictionary<uint, int>? player = null, Random? random = null)
    {
        if (!target.Hostile || target.SpiderWebs.Count(now) == 0 || conn.PlayerLife == 0) return;
        player ??= CharacterPlayerStats(conn);
        int Get(string n) => GolemStat(player, n);
        int gain = Get("energy_shield_gain_when_you_hit_enemy_affected_by_spiders_web");
        if (gain > 0) conn.EnergyShield.Recover((uint)gain);
        if (RandomRolls.Chance(Get("chance_to_gain_power_charge_on_hitting_enemy_affected_by_spiders_web_%"),
                100, random ?? Random.Shared))
            ChargesOf(conn).Enqueue(new ChargeEvent.Grant(ChargeType.Power, 1, "Spider's Web hit"));
    }
}
