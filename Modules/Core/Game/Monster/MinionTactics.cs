using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const int AggressiveMinionMinimumRange = 80;
    const int DefensiveMinionMaximumRange = 30;
    const int MeatShieldNearbyRange = 30;

    static (bool Aggressive, bool Defensive) MinionStance(IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> player, bool isGolem, bool recentlyBlocked)
    {
        bool aggressive = GolemStat(own, Stat.MinionLargerAggroRadius) != 0
            || GolemStat(player, Stat.MinionLargerAggroRadius) != 0
            || isGolem && GolemStat(player, Stat.GolemsLargerAggroRadius) != 0
            || GolemStat(player, Stat.MinionAreAggressiveIfHaveBlockedRecently) != 0
                && recentlyBlocked;
        bool defensive = GolemStat(own, Stat.MinionsAreDefensive) != 0
            || GolemStat(player, Stat.MinionsAreDefensive) != 0;
        return (aggressive && !defensive, defensive && !aggressive);
    }

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

    static bool IsPrey(Player player, IReadOnlyDictionary<uint, int> own, uint id, SpawnedMonster monster, double now) =>
        GolemStat(own, Stat.SupportMinionUseFocussedTarget) != 0
        && player.Summons.SignalledPrey is { } prey && now < prey.Until && prey.Id == id
        && ReferenceEquals(prey.Monster, monster) && player.Area.Monsters.GetValueOrDefault(id) == monster;

    // Puppet Master's Feeding Frenzy buff on the player (BuffDefinitions row).
    const ushort FeedingFrenzyBuffRow = 1372;

    static double MinionTacticsBaseDamage(Player player, GolemState.Golem fighter, double now)
    {
        double more = Math.Max(0, 1 + GolemStat(fighter.Own, Stat.DeathmarkMinionDamagePlusPctFinal) / 100d);
        if (player.PlayerLife > 0 && (now < player.Summons.FeedingFrenzyUntil
            || GolemStat(fighter.Player, Stat.HasPuppetMaster) != 0 && !player.Area.Buffs.HasRow(player.Id, FeedingFrenzyBuffRow)
            || GolemStat(fighter.Player, Stat.VirtualYouHaveFeedingFrenzy) != 0))
            more *= 1.1 * Math.Max(0, 1 + GolemStat(fighter.Own,
                Stat.SupportMinionOffensiveStanceMinionDamagePlusPctFinalWhileYouHavePuppetMaster) / 100d);
        return more;
    }

    static double MinionTacticsDamage(Player player, GolemState.Golem fighter, uint id, SpawnedMonster target, double now)
    {
        double more = MinionTacticsBaseDamage(player, fighter, now);
        if (IsPrey(player, fighter.Own, id, target, now))
            more *= Math.Max(0, 1 + GolemStat(fighter.Own, Stat.SupportMinionFocusFireDamagePlusPctFinalVsFocussedTarget) / 100d);
        return more * MeatShieldDamage(player, fighter.Own, target, now);
    }

    static double MeatShieldDamage(Player player, IReadOnlyDictionary<uint, int> own, SpawnedMonster target, double now)
    {
        int nearby = GolemStat(own, Stat.SupportMinionDefensiveStanceMinionDamagePlusPctFinalAgainstEnemiesNearYou);
        if (nearby != 0 && Dist(player.Walk.Position(now), target.Walk.Position(now)) <= MeatShieldNearbyRange)
            return Math.Max(0, 1 + nearby / 100d);
        return 1;
    }

    static void MinionTauntOnHitLocked(Player player, GolemState.Golem fighter, GolemState.Ability ability,
        IReadOnlyDictionary<uint, int> own, uint id, SpawnedMonster target, double now)
    {
        int chance = GolemStat(own, Stat.MinionChanceToTauntOnHitPct);
        if (ability.Effect == 136) chance += GolemStat(ability.Stats, Stat.ChanceToTauntOnHitPct);
        if (chance <= 0 || target.Effects[Stat.CannotBeTaunted] != 0
            || player.Summons.Definitions.GetValueOrDefault(fighter.Id) is { Targetable: false }
            || player.Minions.Random.Next(100) >= chance) return;

        // A permanent minion can taunt many different monsters over its lifetime.
        if (fighter.Taunts.Count >= 32)
            foreach (var expired in fighter.Taunts.Where(t => t.Value <= now || !player.Area.Monsters.ContainsKey(t.Key)).ToArray())
                fighter.Taunts.Remove(expired.Key);
        fighter.Taunts[id] = now + FlaskGrounds.TauntSeconds / target.RecipientDebuffRate;
    }

    internal static void FeedingFrenzyOnHitLocked(Player player, GolemState.Golem fighter, double now)
    {
        int chance = GolemStat(fighter.Own, Stat.MinionGrantPuppetMasterBuffToParentOnHitPct);
        if (player.PlayerLife == 0 || chance <= 0 || player.Minions.Random.Next(100) >= chance) return;
        double duration = GolemStat(fighter.Own, Stat.PuppetMasterBaseDurationMs) / 1000d;
        if (duration <= 0) return;
        bool starting = now >= player.Summons.FeedingFrenzyUntil;
        player.Summons.FeedingFrenzyUntil = now + duration;
        player.Area.Buffs.Set(player.Id, new(FeedingFrenzyBuffRow, player.Id, fighter.Source, U68: Buff.Seconds(duration)));
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
        if (player.Area.Buffs.HasRow(player.Id, FeedingFrenzyBuffRow) && (player.PlayerLife == 0 || now >= player.Summons.FeedingFrenzyUntil))
        {
            player.Area.Buffs.Clear(player.Id, FeedingFrenzyBuffRow, player.Id);
            player.Summons.FeedingFrenzyUntil = 0;
            RefreshMinionTacticsLocked(player);
        }
        if (player.Summons.SignalledPrey is { } prey && (player.PlayerLife == 0 || now >= prey.Until
            || prey.Monster.Dead || prey.Monster.Life == 0 || player.Area.Monsters.GetValueOrDefault(prey.Id) != prey.Monster
            || !GemSkillEffects(player).Any(g => g.Id == prey.Source && SkillsFor(player).EffectId(g.Effect) == "SignalPrey")))
            ClearPreyLocked(player);
    }
}
