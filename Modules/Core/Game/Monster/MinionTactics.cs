using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    internal static bool SignalPreyLocked(Player player, ushort source, SupportedGem gem,
        (int X, int Y) aim, uint target, double now)
    {
        bool Valid(SpawnedMonster m) => m.Hostile && !m.Dead && m.Life > 0 && !m.Invulnerable;
        if (!player.Area.Monsters.TryGetValue(target, out var monster) || !Valid(monster))
        {
            var closest = player.Area.MonstersNear(aim, 15).Where(p => Valid(p.Value) && Dist(p.Value.Walk.Position(now), aim) <= 15)
                .OrderBy(p => Dist(p.Value.Walk.Position(now), aim)).FirstOrDefault();
            if (closest.Value is null) return false;
            target = closest.Key; monster = closest.Value;
        }
        ClearPreyLocked(player);
        double duration = SkillDurationModifiers.Seconds(GolemStat(gem.Stats, Stat.BaseSkillEffectDuration),
            n => GolemStat(CharacterPlayerStats(player), n), n => GolemStat(gem.Stats, n));
        if (player.Summons.SignalledPrey is { } old && old.Id != target) player.Area.Buffs.Clear(old.Id, 1376, player.Id);
        player.Summons.SignalledPrey = new(target, monster, source, now + duration, 1);
        player.Area.Buffs.Set(target, new(1376, player.Id, source, U68: Buff.Seconds(duration)));
        return true;
    }

    static void ClearPreyLocked(Player player)
    {
        if (player.Summons.SignalledPrey is not { } prey) return;
        if (player.Area.Monsters.GetValueOrDefault(prey.Id) == prey.Monster)
            player.Area.Buffs.Clear(prey.Id, 1376, player.Id);
        player.Summons.SignalledPrey = null;
    }

    static bool IsPrey(Player player, GolemState.Golem fighter, uint id, SpawnedMonster monster, double now) =>
        GolemStat(fighter.Own, Stat.SupportMinionUseFocussedTarget) != 0
        && player.Summons.SignalledPrey is { } prey && now < prey.Until && prey.Id == id
        && ReferenceEquals(prey.Monster, monster) && player.Area.Monsters.GetValueOrDefault(id) == monster;

    static double MinionTacticsBaseDamage(Player player, GolemState.Golem fighter, double now)
    {
        double more = Math.Max(0, 1 + GolemStat(fighter.Own, Stat.DeathmarkMinionDamagePlusPctFinal) / 100d);
        if (player.PlayerLife > 0 && (now < player.Summons.FeedingFrenzyUntil
            || GolemStat(fighter.Player, Stat.HasPuppetMaster) != 0 && player.Summons.FeedingFrenzyBuff == 0
            || GolemStat(fighter.Player, Stat.VirtualYouHaveFeedingFrenzy) != 0))
            more *= 1.1 * Math.Max(0, 1 + GolemStat(fighter.Own,
                Stat.SupportMinionOffensiveStanceMinionDamagePlusPctFinalWhileYouHavePuppetMaster) / 100d);
        return more;
    }

    static double MinionTacticsDamage(Player player, GolemState.Golem fighter, uint id, SpawnedMonster target, double now)
    {
        double more = MinionTacticsBaseDamage(player, fighter, now);
        if (IsPrey(player, fighter, id, target, now))
            more *= Math.Max(0, 1 + GolemStat(fighter.Own, Stat.SupportMinionFocusFireDamagePlusPctFinalVsFocussedTarget) / 100d);
        return more;
    }

    internal static void FeedingFrenzyOnHitLocked(Player player, GolemState.Golem fighter, double now)
    {
        int chance = GolemStat(fighter.Own, Stat.MinionGrantPuppetMasterBuffToParentOnHitPct);
        if (player.PlayerLife == 0 || chance <= 0 || player.Minions.Random.Next(100) >= chance) return;
        double duration = GolemStat(fighter.Own, Stat.PuppetMasterBaseDurationMs) / 1000d;
        if (duration <= 0) return;
        bool starting = now >= player.Summons.FeedingFrenzyUntil;
        player.Summons.FeedingFrenzyUntil = now + duration;
        player.Summons.FeedingFrenzyBuff = 1;
        player.Area.Buffs.Set(player.Id, new(1372, player.Id, fighter.Source, U68: Buff.Seconds(duration)));
        if (starting) RefreshMinionTacticsLocked(player);
    }

    static void RefreshMinionTacticsLocked(Player player)
    {
        player.Minions.StatRevision++;
        player.Golems.RefreshAt = player.Summons.RefreshAt = 0;
        using var scope = new PlayerStatScope(player, fresh: true);
        var playerStats = CharacterPlayerStats(player);
        foreach (var fighter in UtilityFighters(player)) RefreshGolemLocked(player, fighter, playerStats);
    }

    static void MinionTacticsTick(Player player, double now)
    {
        if (player.Summons.FeedingFrenzyBuff != 0 && (player.PlayerLife == 0 || now >= player.Summons.FeedingFrenzyUntil))
        {
            player.Area.Buffs.Clear(player.Id, 1372, player.Id);
            player.Summons.FeedingFrenzyBuff = 0; player.Summons.FeedingFrenzyUntil = 0;
            RefreshMinionTacticsLocked(player);
        }
        if (player.Summons.SignalledPrey is { } prey && (player.PlayerLife == 0 || now >= prey.Until
            || prey.Monster.Dead || prey.Monster.Life == 0 || player.Area.Monsters.GetValueOrDefault(prey.Id) != prey.Monster
            || !GemSkillEffects(player).Any(g => g.Id == prey.Source && SkillsFor(player).EffectId(g.Effect) == "SignalPrey")))
            ClearPreyLocked(player);
    }
}
