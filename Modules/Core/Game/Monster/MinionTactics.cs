using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const int AggressiveMinionMinimumRange = 80;
    const int DefensiveMinionMaximumRange = 30;
    const int MeatShieldNearbyRange = 30;

    static (bool Aggressive, bool Defensive) MinionStance(IReadOnlyDictionary<uint, int> own,
        IReadOnlyDictionary<uint, int> player, bool isGolem, bool recentlyBlocked)
    {
        bool aggressive = GolemStat(own, "minion_larger_aggro_radius") != 0
            || GolemStat(player, "minion_larger_aggro_radius") != 0
            || isGolem && GolemStat(player, "golems_larger_aggro_radius") != 0
            || GolemStat(player, "minion_are_aggressive_if_have_blocked_recently") != 0
                && recentlyBlocked;
        bool defensive = GolemStat(own, "minions_are_defensive") != 0
            || GolemStat(player, "minions_are_defensive") != 0;
        return (aggressive && !defensive, defensive && !aggressive);
    }

    internal static bool SignalPreyLocked(ClientState conn, ushort source, SupportedGem gem,
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
        double duration = SkillDurationModifiers.Seconds(GolemStat(gem.Stats, "base_skill_effect_duration"),
            n => GolemStat(CharacterPlayerStats(conn), n), n => GolemStat(gem.Stats, n));
        ushort instance = conn.NextBuff++;
        conn.Summons.SignalledPrey = new(target, monster, source, now + duration, instance);
        conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(target, instance, 1376, PlayerId, source,
            u68: BitConverter.SingleToUInt32Bits((float)duration)), "Signal Prey", target, monster));
        return true;
    }

    static void ClearPreyLocked(ClientState conn)
    {
        if (conn.Summons.SignalledPrey is not { } prey) return;
        if (conn.Monsters.GetValueOrDefault(prey.Id) == prey.Monster)
            conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(prey.Id, prey.Buff), "Signal Prey ended", prey.Id, prey.Monster));
        conn.Summons.SignalledPrey = null;
    }

    static bool IsPrey(ClientState conn, IReadOnlyDictionary<uint, int> own, uint id, SpawnedMonster monster, double now) =>
        GolemStat(own, "support_minion_use_focussed_target") != 0
        && conn.Summons.SignalledPrey is { } prey && now < prey.Until && prey.Id == id
        && ReferenceEquals(prey.Monster, monster) && conn.Monsters.GetValueOrDefault(id) == monster;

    static double MinionTacticsBaseDamage(ClientState conn, GolemState.Golem fighter, double now)
    {
        double more = Math.Max(0, 1 + GolemStat(fighter.Own, "deathmark_minion_damage_+%_final") / 100d);
        if (conn.PlayerLife > 0 && (now < conn.Summons.FeedingFrenzyUntil
            || GolemStat(fighter.Player, "has_puppet_master") != 0 && conn.Summons.FeedingFrenzyBuff == 0
            || GolemStat(fighter.Player, "virtual_you_have_feeding_frenzy") != 0))
            more *= 1.1 * Math.Max(0, 1 + GolemStat(fighter.Own,
                "support_minion_offensive_stance_minion_damage_+%_final_while_you_have_puppet_master") / 100d);
        return more;
    }

    static double MinionTacticsDamage(ClientState conn, GolemState.Golem fighter, uint id, SpawnedMonster target, double now)
    {
        double more = MinionTacticsBaseDamage(conn, fighter, now);
        if (IsPrey(conn, fighter.Own, id, target, now))
            more *= Math.Max(0, 1 + GolemStat(fighter.Own, "support_minion_focus_fire_damage_+%_final_vs_focussed_target") / 100d);
        return more * MeatShieldDamage(conn, fighter.Own, target, now);
    }

    static double MeatShieldDamage(ClientState conn, IReadOnlyDictionary<uint, int> own, SpawnedMonster target, double now)
    {
        int nearby = GolemStat(own, "support_minion_defensive_stance_minion_damage_+%_final_against_enemies_near_you");
        if (nearby != 0 && Dist(conn.Walk.Position(now), target.Walk.Position(now)) <= MeatShieldNearbyRange)
            return Math.Max(0, 1 + nearby / 100d);
        return 1;
    }

    static void MinionTauntOnHitLocked(ClientState conn, GolemState.Golem fighter, GolemState.Ability ability,
        IReadOnlyDictionary<uint, int> own, uint id, SpawnedMonster target, double now)
    {
        int chance = GolemStat(own, "minion_chance_to_taunt_on_hit_%");
        if (ability.Effect == 136) chance += GolemStat(ability.Stats, "chance_to_taunt_on_hit_%");
        if (chance <= 0 || target.Effects["cannot_be_taunted"] != 0
            || conn.Summons.Definitions.GetValueOrDefault(fighter.Id) is { Targetable: false }
            || conn.Minions.Random.Next(100) >= chance) return;

        // A permanent minion can taunt many different monsters over its lifetime.
        if (fighter.Taunts.Count >= 32)
            foreach (var expired in fighter.Taunts.Where(t => t.Value <= now || !conn.Monsters.ContainsKey(t.Key)).ToArray())
                fighter.Taunts.Remove(expired.Key);
        fighter.Taunts[id] = now + FlaskGrounds.TauntSeconds / target.RecipientDebuffRate;
    }

    internal static void FeedingFrenzyOnHitLocked(ClientState conn, GolemState.Golem fighter, double now)
    {
        int chance = GolemStat(fighter.Own, "minion_grant_puppet_master_buff_to_parent_on_hit_%");
        if (conn.PlayerLife == 0 || chance <= 0 || conn.Minions.Random.Next(100) >= chance) return;
        double duration = GolemStat(fighter.Own, "puppet_master_base_duration_ms") / 1000d;
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

    static void RefreshMinionTacticsLocked(ClientState conn)
    {
        conn.Minions.StatRevision++;
        conn.Golems.RefreshAt = conn.Summons.RefreshAt = 0;
        using var scope = new PlayerStatScope(conn, fresh: true);
        var player = CharacterPlayerStats(conn);
        foreach (var fighter in UtilityFighters(conn)) RefreshGolemLocked(conn, fighter, player);
    }

    static void MinionTacticsTick(ClientState conn, double now)
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
