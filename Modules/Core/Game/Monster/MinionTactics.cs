using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    internal static bool SignalPreyLocked(Player conn, ushort source, SupportedGem gem,
        (int X, int Y) aim, uint target, double now)
    {
        bool Valid(SpawnedMonster m) => m.Hostile && !m.Dead && m.Life > 0 && !m.Invulnerable;
        if (!conn.Monsters.TryGetValue(target, out var monster) || !Valid(monster))
        {
            var closest = conn.Monsters.Where(p => Valid(p.Value) && Dist(p.Value.Walk.Position(now), aim) <= 15)
                .OrderBy(p => Dist(p.Value.Walk.Position(now), aim)).FirstOrDefault();
            if (closest.Value is null) return false;
            target = closest.Key; monster = closest.Value;
        }
        ClearPreyLocked(conn);
        double duration = SkillDurationModifiers.Seconds(GolemStat(gem.Stats, Stat.BaseSkillEffectDuration),
            n => GolemStat(CharacterPlayerStats(conn), n), n => GolemStat(gem.Stats, n));
        ushort instance = conn.NextBuff++;
        conn.Summons.SignalledPrey = new(target, monster, source, now + duration, instance);
        conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(target, instance, 1376, PlayerId, source,
            u68: BitConverter.SingleToUInt32Bits((float)duration)), "Signal Prey", target, monster));
        return true;
    }

    static void ClearPreyLocked(Player conn)
    {
        if (conn.Summons.SignalledPrey is not { } prey) return;
        if (conn.Monsters.GetValueOrDefault(prey.Id) == prey.Monster)
            conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(prey.Id, prey.Buff), "Signal Prey ended", prey.Id, prey.Monster));
        conn.Summons.SignalledPrey = null;
    }

    static bool IsPrey(Player conn, GolemState.Golem fighter, uint id, SpawnedMonster monster, double now) =>
        GolemStat(fighter.Own, Stat.SupportMinionUseFocussedTarget) != 0
        && conn.Summons.SignalledPrey is { } prey && now < prey.Until && prey.Id == id
        && ReferenceEquals(prey.Monster, monster) && conn.Monsters.GetValueOrDefault(id) == monster;

    static double MinionTacticsBaseDamage(Player conn, GolemState.Golem fighter, double now)
    {
        double more = Math.Max(0, 1 + GolemStat(fighter.Own, Stat.DeathmarkMinionDamagePlusPctFinal) / 100d);
        if (conn.PlayerLife > 0 && (now < conn.Summons.FeedingFrenzyUntil
            || GolemStat(fighter.Player, Stat.HasPuppetMaster) != 0 && conn.Summons.FeedingFrenzyBuff == 0
            || GolemStat(fighter.Player, Stat.VirtualYouHaveFeedingFrenzy) != 0))
            more *= 1.1 * Math.Max(0, 1 + GolemStat(fighter.Own,
                Stat.SupportMinionOffensiveStanceMinionDamagePlusPctFinalWhileYouHavePuppetMaster) / 100d);
        return more;
    }

    static double MinionTacticsDamage(Player conn, GolemState.Golem fighter, uint id, SpawnedMonster target, double now)
    {
        double more = MinionTacticsBaseDamage(conn, fighter, now);
        if (IsPrey(conn, fighter, id, target, now))
            more *= Math.Max(0, 1 + GolemStat(fighter.Own, Stat.SupportMinionFocusFireDamagePlusPctFinalVsFocussedTarget) / 100d);
        return more;
    }

    internal static void FeedingFrenzyOnHitLocked(Player conn, GolemState.Golem fighter, double now)
    {
        int chance = GolemStat(fighter.Own, Stat.MinionGrantPuppetMasterBuffToParentOnHitPct);
        if (conn.PlayerLife == 0 || chance <= 0 || conn.Minions.Random.Next(100) >= chance) return;
        double duration = GolemStat(fighter.Own, Stat.PuppetMasterBaseDurationMs) / 1000d;
        if (duration <= 0) return;
        bool starting = now >= conn.Summons.FeedingFrenzyUntil;
        if (conn.Summons.FeedingFrenzyBuff != 0)
            conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, conn.Summons.FeedingFrenzyBuff), "Feeding Frenzy refreshed"));
        conn.Summons.FeedingFrenzyUntil = now + duration;
        ushort instance = conn.NextBuff++;
        conn.Summons.FeedingFrenzyBuff = instance;
        conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(PlayerId, instance, 1372, PlayerId, fighter.Source,
            u68: BitConverter.SingleToUInt32Bits((float)duration)), "Feeding Frenzy"));
        if (starting) RefreshMinionTacticsLocked(conn);
    }

    static void RefreshMinionTacticsLocked(Player conn)
    {
        conn.Minions.StatRevision++;
        conn.Golems.RefreshAt = conn.Summons.RefreshAt = 0;
        using var scope = new PlayerStatScope(conn, fresh: true);
        var player = CharacterPlayerStats(conn);
        foreach (var fighter in UtilityFighters(conn)) RefreshGolemLocked(conn, fighter, player);
    }

    static void MinionTacticsTick(Player conn, double now)
    {
        if (conn.Summons.FeedingFrenzyBuff != 0 && (conn.PlayerLife == 0 || now >= conn.Summons.FeedingFrenzyUntil))
        {
            conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, conn.Summons.FeedingFrenzyBuff), "Feeding Frenzy ended"));
            conn.Summons.FeedingFrenzyBuff = 0; conn.Summons.FeedingFrenzyUntil = 0;
            RefreshMinionTacticsLocked(conn);
        }
        if (conn.Summons.SignalledPrey is { } prey && (conn.PlayerLife == 0 || now >= prey.Until
            || prey.Monster.Dead || prey.Monster.Life == 0 || conn.Monsters.GetValueOrDefault(prey.Id) != prey.Monster
            || !GemSkillEffects(conn).Any(g => g.Id == prey.Source && SkillsFor(conn).EffectId(g.Effect) == "SignalPrey")))
            ClearPreyLocked(conn);
    }
}
