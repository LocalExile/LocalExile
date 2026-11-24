using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static Dictionary<uint, int> WithSummonBuffs(Player player, Dictionary<uint, int> sources)
    {
        Dictionary<uint, int>? result = null;
        void Add(uint name, int value)
        {
            if (value == 0 || !(name is var key)) return;
            result ??= new(sources); result[key] = result.GetValueOrDefault(key) + value;
        }
        foreach (double until in new[] { player.Summons.HolyRegenerationUntil, player.Summons.FeedingFrenzyUntil })
            if (AreaTime(player) < until) StatsChangeAt(until, () => AreaTime(player));
        if (AreaTime(player) < player.Summons.HolyRegenerationUntil) Add(Stat.BaseLifeRegenerationRatePerMinute, player.Summons.HolyRegenerationPerMinute);
        if (AreaTime(player) < player.Summons.FeedingFrenzyUntil && player.PlayerLife > 0)
        {
            Add(Stat.HasPuppetMaster, 1);
            Add(Stat.PuppetMasterMinionDamagePlusPctFinal, 10);
            Add(Stat.MinionMovementSpeedPlusPct, 10);
            Add(Stat.MinionAttackSpeedPlusPct, 10);
            Add(Stat.MinionCastSpeedPlusPct, 10);
        }
        if (player.PlayerLife > 0 && GolemStat(sources, Stat.SummonedPhantasmsGrantBuff) > 0)
        {
            double now = AreaTime(player), nextExpiry = double.PositiveInfinity;
            long min = 0, max = 0;
            foreach (var fighter in player.Summons.Fighters.Values)
                if (PhantasmGrantsMight(player, fighter, now, out double until))
                {
                    min += GolemStat(fighter.Own, Stat.PhantasmMinimumAddedPhysicalDamageToGrant);
                    max += GolemStat(fighter.Own, Stat.PhantasmMaximumAddedPhysicalDamageToGrant);
                    nextExpiry = Math.Min(nextExpiry, until);
                }
            Add(Stat.SpellMinimumAddedPhysicalDamage, ActorScale.Saturate(min));
            Add(Stat.SpellMaximumAddedPhysicalDamage, ActorScale.Saturate(max));
            if (double.IsFinite(nextExpiry)) StatsChangeAt(nextExpiry, () => AreaTime(player));
        }
        foreach (var name in new[] { "damage", "maximum_life" })
            Add(Stat.Of("active_skill_non_reaper_minion_" + name + "_+%_final"), player.Summons.Fighters.Values
                .Where(g => g.Definition.Kind == "Reaper" && !g.Monster.Dead)
                .Select(g => GolemStat(g.Own, Stat.Of("active_skill_non_reaper_minion_" + name + "_+%_final"))).DefaultIfEmpty().Min());
        return result ?? sources;
    }

    static void HolyRelicsOnHitLocked(Player player, uint target, SpawnedMonster victim, double now)
    {
        if (player.PlayerLife == 0 || InTown(player)) return;
        foreach (var relic in player.Summons.Fighters.Values.Where(g => g.Definition.Kind == "HolyRelic" && !g.Monster.Dead && g.Pending is null))
            if (relic.Abilities.FirstOrDefault(a => a.Effect is 9927 or 9928) is { } nova && GolemReady(relic, nova, now))
            {
                if (GolemStat(relic.Own, Stat.HolyRelicTargetsNearestEnemy) != 0)
                {
                    var at = relic.Monster.Walk.Position(now);
                    var nearest = player.Area.Monsters.Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.Life > 0
                            && !p.Value.Invulnerable && !p.Value.Asleep && !MonsterAsleep(player, p.Key))
                        .Where(p => Dist(at, p.Value.Walk.Position(now)) <= nova.Attack.Range)
                        .MinBy(p => Dist(at, p.Value.Walk.Position(now)));
                    if (nearest.Value is not null)
                        GolemReserveLocked(player, relic, nova, nearest.Key, nearest.Value, nearest.Value.Walk.Position(now), now);
                    continue;
                }
                GolemReserveLocked(player, relic, nova, target, victim, victim.Walk.Position(now), now);
            }
    }

    public static void HolyRelicRegenerationLocked(Player player, GolemState.Golem relic, GolemState.Ability nova, double now)
    {
        double duration = GolemStat(nova.Stats, Stat.BaseSkillEffectDuration) / 1000d;
        if (duration <= 0) return;
        var centre = relic.Monster.Walk.Position(now);
        double effect = Math.Max(0, 1 + GolemStat(relic.Own, Stat.SkillBuffEffectPlusPct) / 100d);
        int Rate(uint key) => ActorScale.Saturate((long)Math.Round(GolemStat(relic.Own, key) * effect));
        int playerRate = Rate(Stat.HolyRelicNovaLifeRegenerationRatePerMinute);
        int minionRate = Rate(Stat.HolyRelicNovaMinionLifeRegenerationRatePerSecond);
        void Receive(Player ally, double at)
        {
            var state = ally.Summons;
            double until = at + duration;
            if (playerRate > 0 && ally.PlayerLife > 0 && Dist(centre, ally.Walk.Position(at)) <= nova.Radius)
            {
                bool changed = state.HolyRegenerationPerMinute != playerRate || at >= state.HolyRegenerationUntil;
                state.HolyRegenerationPerMinute = playerRate;
                state.HolyRegenerationUntil = until;
                ally.Area.Buffs.ClearRow(ally.Id, 1181);
                state.HolyBuff = 1;
                ally.Area.Buffs.Set(ally.Id, new(1181, relic.Id, U68: Buff.Seconds(duration)));
                if (changed) ally.Minions.StatRevision++;
            }
            if (minionRate <= 0) return;
            foreach (var minion in UtilityFighters(ally))
                if (GolemCurrent(ally, minion) && Dist(centre, minion.Monster.Walk.Position(at)) <= nova.Radius)
                {
                    minion.Regeneration += HolyRelicRecoveryBetween(minion, minion.LastStep, at);
                    minion.HolyRegenerationSince = at;
                    minion.HolyRegenerationUntil = until;
                    minion.HolyRegenerationPerSecond = minionRate;
                }
        }
        Receive(player, now);
        foreach (var ally in player.Instance.PlayersExcept(player.Id))
            if (!ally.Closed && !ally.LeavingArea && ReferenceEquals(player.Instance, ally.Instance))
                Receive(ally, AreaTime(ally));
    }

    static double HolyRelicRecoveryBetween(GolemState.Golem minion, double from, double to) =>
        Math.Max(0, Math.Min(to, minion.HolyRegenerationUntil) - Math.Max(from, minion.HolyRegenerationSince))
            * minion.HolyRegenerationPerSecond;

    static bool ReaperConsumeLocked(Player player, GolemState.Golem reaper, double now)
    {
        if (reaper.Definition.Kind != "Reaper") return false;
        if (reaper.WrathUntil > 0 && now >= reaper.WrathUntil)
        {
            reaper.WrathUntil = 0;
            reaper.Gear.Clear(); reaper.InputHash = 0; player.Summons.RefreshAt = 0;
        }
        int effect = reaper.Monster.Life < reaper.Monster.MaxLife * .75 ? 12032 : 12033;
        if (effect == 12033 && now < reaper.SummonedAt + 25) return false;
        if (reaper.Abilities.FirstOrDefault(a => a.Effect == effect) is not { } consume || !GolemReady(reaper, consume, now)) return false;
        var meal = player.Golems.All.Values.Concat(player.Summons.Fighters.Values).Concat(player.Reservations.Fighters.Values)
            .Where(g => g != reaper && g.Definition.Kind is not ("Reaper" or "SkitterCold" or "SkitterLightning" or "Agony")
                && !g.Monster.Dead && g.Monster.Life > 0 && Dist(g.Monster.Walk.Position(now), reaper.Monster.Walk.Position(now)) <= 15)
            .MinBy(g => Dist(g.Monster.Walk.Position(now), reaper.Monster.Walk.Position(now)));
        if (meal is null) return false;
        GolemReserveLocked(player, reaper, consume, meal.Id, meal.Monster, meal.Monster.Walk.Position(now), now);
        return true;
    }

    static void ReaperConsumedLocked(Player player, GolemState.Golem reaper, GolemState.Action action, double now)
    {
        if (OwnedFighter(player, action.Target) is not { } meal || meal.Monster.Dead || meal.Monster.Life == 0) return;
        GolemDieLocked(player, meal, now);
        reaper.Monster.Life = (uint)Math.Min(reaper.Monster.MaxLife, reaper.Monster.Life + reaper.Monster.MaxLife / 2L);
        reaper.WrathUntil = now + GolemStat(action.Ability.Stats, Stat.BaseSkillEffectDuration) / 1000d;
        reaper.Gear = new() { [Stat.AttackSpeedPlusPct] = 20, [Stat.BaseMovementVelocityPlusPct] = 20, [Stat.BaseSkillAreaOfEffectPlusPct] = 40, [Stat.BaseActorScalePlusPct] = 10 };
        reaper.InputHash = 0; player.Summons.RefreshAt = 0;
        player.Minions.OutputQueue.Add(new([], "Reaper consumed allied minion", reaper.Id, reaper.Monster, PlayerMinions.OutputKind.Life));
        player.Area.Buffs.Set(reaper.Id, new(1870, reaper.Id, U68: Buff.Seconds(6)));
    }
}
