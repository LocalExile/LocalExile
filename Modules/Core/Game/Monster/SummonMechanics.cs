using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static Dictionary<uint, int> WithSummonBuffs(Player conn, Dictionary<uint, int> sources)
    {
        lock (conn.Monsters)
        {
            Dictionary<uint, int>? result = null;
            void Add(uint name, int value)
            {
                if (value == 0 || !(name is var key)) return;
                result ??= new(sources); result[key] = result.GetValueOrDefault(key) + value;
            }
            foreach (double until in new[] { conn.Summons.HolyRegenerationUntil, conn.Summons.FeedingFrenzyUntil })
                if (AreaTime(conn) < until) StatsChangeAt(until, () => AreaTime(conn));
            if (AreaTime(conn) < conn.Summons.HolyRegenerationUntil) Add(Stat.BaseLifeRegenerationRatePerMinute, conn.Summons.HolyRegenerationPerMinute);
            if (AreaTime(conn) < conn.Summons.FeedingFrenzyUntil && conn.PlayerLife > 0)
            {
                Add(Stat.HasPuppetMaster, 1);
                Add(Stat.PuppetMasterMinionDamagePlusPctFinal, 10);
                Add(Stat.MinionMovementSpeedPlusPct, 10);
                Add(Stat.MinionAttackSpeedPlusPct, 10);
                Add(Stat.MinionCastSpeedPlusPct, 10);
            }
            foreach (var name in new[] { "damage", "maximum_life" })
                Add(Stat.Of("active_skill_non_reaper_minion_" + name + "_+%_final"), conn.Summons.Fighters.Values
                    .Where(g => g.Definition.Kind == "Reaper" && !g.Monster.Dead)
                    .Select(g => GolemStat(g.Own, Stat.Of("active_skill_non_reaper_minion_" + name + "_+%_final"))).DefaultIfEmpty().Min());
            return result ?? sources;
        }
    }

    static void HolyRelicsOnHitLocked(Player conn, uint target, SpawnedMonster victim, double now)
    {
        if (conn.PlayerLife == 0 || InTown(conn)) return;
        foreach (var relic in conn.Summons.Fighters.Values.Where(g => g.Definition.Kind == "HolyRelic" && !g.Monster.Dead && g.Pending is null))
            if (relic.Abilities.FirstOrDefault(a => a.Effect is 9927 or 9928) is { } nova && GolemReady(relic, nova, now))
            {
                if (GolemStat(relic.Own, Stat.HolyRelicTargetsNearestEnemy) != 0)
                {
                    var at = relic.Monster.Walk.Position(now);
                    var nearest = conn.Monsters.Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.Life > 0
                            && !p.Value.Invulnerable && !p.Value.Asleep && !conn.AsleepMonsters.Contains(p.Key))
                        .Where(p => Dist(at, p.Value.Walk.Position(now)) <= nova.Attack.Range)
                        .MinBy(p => Dist(at, p.Value.Walk.Position(now)));
                    if (nearest.Value is not null)
                        GolemReserveLocked(conn, relic, nova, nearest.Key, nearest.Value, nearest.Value.Walk.Position(now), now);
                    continue;
                }
                GolemReserveLocked(conn, relic, nova, target, victim, victim.Walk.Position(now), now);
            }
    }

    public static void HolyRelicRegenerationLocked(Player conn, GolemState.Golem relic, GolemState.Ability nova, double now)
    {
        double duration = GolemStat(nova.Stats, Stat.BaseSkillEffectDuration) / 1000d;
        double until = now + duration;
        var centre = relic.Monster.Walk.Position(now);
        int rate = GolemStat(relic.Own, Stat.HolyRelicNovaLifeRegenerationRatePerMinute);
        if (rate > 0 && Dist(centre, conn.Walk.Position(now)) <= nova.Radius)
        {
            bool changed = conn.Summons.HolyRegenerationPerMinute != rate || now >= conn.Summons.HolyRegenerationUntil;
            conn.Summons.HolyRegenerationPerMinute = rate; conn.Summons.HolyRegenerationUntil = until;
            if (conn.Summons.HolyBuff != 0) conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, conn.Summons.HolyBuff), null, WorldId: PlayerId));
            conn.Summons.HolyBuff = conn.NextBuff++;
            conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(PlayerId, conn.Summons.HolyBuff, 1181, relic.Id,
                u68: BitConverter.SingleToUInt32Bits((float)duration)), "Holy Relic regeneration", WorldId: PlayerId));
            if (changed) conn.Minions.StatRevision++;
        }
        foreach (var ally in conn.Golems.All.Values.Concat(conn.Summons.Fighters.Values).Concat(conn.Reservations.Fighters.Values))
            if (!ally.Monster.Dead && Dist(centre, ally.Monster.Walk.Position(now)) <= nova.Radius)
            { ally.HolyRegenerationUntil = until; ally.HolyRegenerationPerSecond = GolemStat(relic.Own, Stat.HolyRelicNovaMinionLifeRegenerationRatePerSecond); }
    }

    static bool ReaperConsumeLocked(Player conn, GolemState.Golem reaper, double now)
    {
        if (reaper.Definition.Kind != "Reaper" || reaper.Definition.Id == "SummonReaperAltX") return false;
        if (reaper.WrathUntil > 0 && now >= reaper.WrathUntil)
        {
            reaper.WrathUntil = 0;
            reaper.Gear.Clear(); reaper.InputHash = 0; conn.Summons.RefreshAt = 0;
        }
        int effect = reaper.Monster.Life < reaper.Monster.MaxLife * .75 ? 12032 : 12033;
        if (effect == 12033 && now < reaper.SummonedAt + 25) return false;
        if (reaper.Abilities.FirstOrDefault(a => a.Effect == effect) is not { } consume || !GolemReady(reaper, consume, now)) return false;
        var meal = conn.Golems.All.Values.Concat(conn.Summons.Fighters.Values).Concat(conn.Reservations.Fighters.Values)
            .Where(g => g != reaper && g.Definition.Kind is not ("Reaper" or "SkitterCold" or "SkitterLightning" or "Agony")
                && !g.Monster.Dead && g.Monster.Life > 0 && Dist(g.Monster.Walk.Position(now), reaper.Monster.Walk.Position(now)) <= 15)
            .MinBy(g => Dist(g.Monster.Walk.Position(now), reaper.Monster.Walk.Position(now)));
        if (meal is null) return false;
        GolemReserveLocked(conn, reaper, consume, meal.Id, meal.Monster, meal.Monster.Walk.Position(now), now);
        return true;
    }

    static void ReaperConsumedLocked(Player conn, GolemState.Golem reaper, GolemState.Action action, double now)
    {
        if (OwnedFighter(conn, action.Target) is not { } meal || meal.Monster.Dead || meal.Monster.Life == 0) return;
        GolemDieLocked(conn, meal, now);
        reaper.Monster.Life = (uint)Math.Min(reaper.Monster.MaxLife, reaper.Monster.Life + reaper.Monster.MaxLife / 2L);
        reaper.WrathUntil = now + GolemStat(action.Ability.Stats, Stat.BaseSkillEffectDuration) / 1000d;
        reaper.Gear = new() { [Stat.AttackSpeedPlusPct] = 20, [Stat.BaseMovementVelocityPlusPct] = 20, [Stat.BaseSkillAreaOfEffectPlusPct] = 40, [Stat.BaseActorScalePlusPct] = 10 };
        reaper.InputHash = 0; conn.Summons.RefreshAt = 0;
        conn.Minions.OutputQueue.Add(new([], "Reaper consumed allied minion", reaper.Id, reaper.Monster, PlayerMinions.OutputKind.Life));
        conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(reaper.Id, conn.NextBuff++, 1870, reaper.Id,
            u68: BitConverter.SingleToUInt32Bits(6)), "Reaper consume buff", WorldId: reaper.Id));
    }
}
