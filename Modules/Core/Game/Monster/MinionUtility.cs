using GameServer.Core.Protocol.Instance;

static partial class Game
{
    internal static bool MinionUtility(string? effect) => effect is "ConvocationOld" or "Convocation"
        or "FleshOffering" or "BoneOffering" or "SpiritOffering";

    static IEnumerable<GolemState.Golem> UtilityFighters(ClientState conn) =>
        conn.Golems.All.Values.Concat(conn.Summons.Fighters.Values).Concat(conn.Reservations.Fighters.Values)
            .Where(g => !g.Monster.Dead && g.Monster.Life > 0);

    internal static async Task<bool> CastMinionUtility(ClientState conn, ushort source, (int X, int Y) aim,
        uint target, int recovery, Func<bool> current)
    {
        var gem = SupportedSkill(conn, source);
        string? effect = SkillsFor(conn).EffectId(gem.Effect);
        if (!MinionUtility(effect)) return false;
        bool recall = effect is "Convocation" or "ConvocationOld";
        double release = AreaTime(conn) + (recall ? 0 : (CastMs(conn, gem.Effect, source) ?? 1000) / 1000d);
        var lifetime = CombatLifetime;
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || conn.Closed
                    || conn.LeavingArea || conn.PlayerLife == 0 || recovery != conn.ActionInterruptionGeneration
                    || !SameTriggerSource(gem, SupportedSkill(conn, source))) return false;
                if (!conn.Paused && AreaTime(conn) >= release)
                {
                    if (recall) RecallMinionsLocked(conn, gem, release);
                    else StartOfferingLocked(conn, source, gem, effect!, aim, target, release);
                    return true;
                }
            }
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
    }

    internal static void RecallMinionsLocked(ClientState conn, SupportedGem gem, double now)
    {
        var player = CharacterPlayerStats(conn);
        double duration = SkillDurationModifiers.Seconds(GolemStat(gem.Stats, "base_skill_effect_duration"),
            n => GolemStat(player, n), n => GolemStat(gem.Stats, n));
        double rate = GolemStat(gem.Stats, "life_regeneration_rate_per_minute_%")
            * Math.Max(0, 1 + GolemStat(player, "convocation_buff_effect_+%") / 100d);
        foreach (var minion in conn.Minions.All)
        {
            var m = minion.Monster;
            if (m.Dead || m.Life == 0 || conn.Summons.Definitions.GetValueOrDefault(minion.Id) is { Follows: false }) continue;
            var at = NextToPlayer(conn);
            m.Walk = conn.NewMonsterWalk(minion.Id, at.X, at.Y, Math.Max(m.Walk.Speed, 1), LivingCollision.Size(m), false);
            m.ActionGeneration++; m.LastTarget = null; m.BusyUntil = now;
            if (OwnedFighter(conn, minion.Id) is { } fighter)
            {
                fighter.Pending = null; fighter.Target = 0; fighter.LastStep = now;
                fighter.Pulses.RemoveAll(p => p.Follows);
                fighter.RecallUntil = now + duration; fighter.RecallRate = rate;
            }
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.Teleport(minion.Id, at.X, at.Y), "Convocation recall", minion.Id, m));
        }
    }

    internal static bool StartOfferingLocked(ClientState conn, ushort source, SupportedGem gem, string effect,
        (int X, int Y) aim, uint target, double now)
    {
        bool Usable(SpawnedMonster m) => m.Hostile && m.Dead && !m.CorpseConsumed && !m.Ailments.Shattered
            && m.Variety?.LifeData.IsUsableAsCorpse(m.Life) == true;
        var first = conn.Monsters.GetValueOrDefault(target);
        if (first is null || !Usable(first)) first = conn.Monsters.Values.Where(Usable)
            .Where(m => Dist(m.Walk.Position(now), aim) <= 15).MinBy(m => Dist(m.Walk.Position(now), aim));
        var player = CharacterPlayerStats(conn);
        if (first is null && GolemStat(player, "offering_skills_do_not_require_corpses") == 0) return false;
        var centre = first?.Walk.Position(now) ?? aim;
        var corpses = conn.Monsters.Where(p => Usable(p.Value) && Dist(p.Value.Walk.Position(now), centre) <= 15)
            .OrderBy(p => ReferenceEquals(p.Value, first) ? 0 : 1).ThenBy(p => Dist(p.Value.Walk.Position(now), centre)).Take(5).ToArray();
        foreach (var (id, corpse) in corpses)
        {
            corpse.CorpseConsumed = true;
            conn.RecentEvents.Record(StatConditions.ConsumedCorpse, now);
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), "Offering corpse consumed"));
        }
        int extra = Math.Max(0, corpses.Length - 1);
        string kind = effect == "FleshOffering" ? "flesh" : effect == "BoneOffering" ? "bone" : "spirit";
        double duration = SkillDurationModifiers.Seconds(GolemStat(gem.Stats, "base_skill_effect_duration")
                + extra * GolemStat(gem.Stats, "offering_skill_effect_duration_per_corpse"),
            n => GolemStat(player, n) + (n == "skill_effect_duration_+%" ? GolemStat(player, "offering_duration_+%")
                + GolemStat(player, kind + "_offering_duration_+%") : 0), n => GolemStat(gem.Stats, n));
        int increase = GolemStat(player, "offering_spells_effect_+%")
            + GolemStat(player, kind + "_offering_effect_+%") + GolemStat(gem.Stats, "offering_spells_effect_+%");
        double scale = Math.Max(0, 1 + increase / 100d);
        Dictionary<string, int> stats = [], baseStats = [];
        void Add(string to, string from, int bonus = 0)
        {
            baseStats[to] = GolemStat(gem.Stats, from) + bonus;
            stats[to] = ActorScale.Saturate((long)Math.Floor(baseStats[to] * scale));
        }
        ushort buff;
        if (effect == "FleshOffering")
        {
            buff = 295;
            Add("attack_speed_+%", "attack_speed_+%_granted_from_skill", GolemStat(player, "flesh_offering_attack_speed_+%"));
            Add("cast_speed_+%", "cast_speed_+%_granted_from_skill"); Add("base_movement_velocity_+%", "base_movement_velocity_+%");
        }
        else if (effect == "BoneOffering")
        {
            buff = 296;
            Add("monster_base_block_%", "monster_base_block_%", GolemStat(player, "bone_offering_block_chance_+%"));
            Add("base_spell_block_%", "base_spell_block_%");
            Add("life_regeneration_rate_per_minute_%_if_blocked_recently", "minion_life_regeneration_rate_per_minute_%_if_blocked_recently");
        }
        else
        {
            buff = 663;
            Add("critical_strike_chance_+%", "spirit_offering_critical_strike_chance_+%");
            Add("base_critical_strike_multiplier_+", "spirit_offering_critical_strike_multiplier_+");
        }
        foreach (var fighter in UtilityFighters(conn))
            if (fighter.UtilityBuffs.Remove(buff, out var previous))
                conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(fighter.Id, previous), "Offering renewed"));
        var all = conn.Summons.Offerings;
        all.RemoveAll(o => o.Effect == effect || now >= o.Until);
        int limit = GolemStat(player, "allow_multiple_offerings") != 0 ? 3 : GolemStat(player, "allow_2_offerings") != 0 ? 2 : 1;
        while (all.Count >= limit) all.RemoveAt(0);
        all.Add(new(effect, now + duration, buff, source, stats, baseStats, increase));
        MinionUtilityTick(conn, now);
        return true;
    }

    static Dictionary<string, int> MinionOfferingStats(ClientState conn, double now)
    {
        var stats = new Dictionary<string, int>();
        lock (conn.Monsters)
            foreach (var offering in conn.Summons.Offerings.Where(o => o.Until > now))
                foreach (var (key, value) in offering.Stats) stats[key] = stats.GetValueOrDefault(key) + value;
        return stats;
    }

    internal static void MinionUtilityTick(ClientState conn, double now)
    {
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.Paused) return;
        if (conn.PlayerLife == 0) conn.Summons.Offerings.Clear();
        conn.Summons.Offerings.RemoveAll(o => now >= o.Until);
        var player = CharacterPlayerStats(conn);
        int limit = GolemStat(player, "allow_multiple_offerings") != 0 ? 3 : GolemStat(player, "allow_2_offerings") != 0 ? 2 : 1;
        while (conn.Summons.Offerings.Count > limit) conn.Summons.Offerings.RemoveAt(0);
        SelfOfferingTick(conn, player, now);
        foreach (var fighter in UtilityFighters(conn))
        {
            var active = conn.Summons.Offerings;
            foreach (var (row, instance) in fighter.UtilityBuffs.ToArray())
                if (!active.Any(o => o.Buff == row))
                { conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(fighter.Id, instance), "Offering expired")); fighter.UtilityBuffs.Remove(row); }
            foreach (var offering in active)
                if (!fighter.UtilityBuffs.ContainsKey(offering.Buff))
                {
                    ushort instance = conn.NextBuff++; fighter.UtilityBuffs[offering.Buff] = instance;
                    conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(fighter.Id, instance, offering.Buff, PlayerId, offering.Source,
                        u68: BitConverter.SingleToUInt32Bits((float)(offering.Until - now))), "Offering buff"));
                }
            var stats = MinionOfferingStats(conn, now);
            if (fighter.Utility.Count == stats.Count && stats.All(p => fighter.Utility.GetValueOrDefault(p.Key) == p.Value)) continue;
            fighter.Utility = stats; fighter.InputHash = 0;
            RefreshGolemLocked(conn, fighter, player);
        }
    }

    static void AddSelfOfferingStats(ClientState conn, Dictionary<uint, int> stats)
    {
        lock (conn.Monsters)
            if (conn.PlayerLife > 0)
                foreach (var (key, value) in conn.Summons.PlayerOfferingStats)
                    stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + value);
    }

    static void SelfOfferingTick(ClientState conn, IReadOnlyDictionary<uint, int> player, double now)
    {
        var state = conn.Summons;
        var active = GolemStat(player, "offerings_also_buff_you") != 0 ? state.Offerings.ToArray() : [];
        foreach (var (row, sent) in state.PlayerOfferingBuffs.ToArray())
            if (!active.Any(o => ReferenceEquals(o, sent.Offering)))
            {
                conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, sent.Instance), "Self offering ended"));
                state.PlayerOfferingBuffs.Remove(row);
            }
        foreach (var offering in active)
            if (!state.PlayerOfferingBuffs.ContainsKey(offering.Buff))
            {
                ushort instance = conn.NextBuff++;
                state.PlayerOfferingBuffs[offering.Buff] = (offering, instance);
                conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(PlayerId, instance, offering.Buff, PlayerId, offering.Source,
                    u68: BitConverter.SingleToUInt32Bits((float)(offering.Until - now))), "Self offering buff"));
            }
        Dictionary<uint, int> next = [];
        foreach (var offering in active)
        {
            double scale = Math.Max(0, 1 + (offering.EffectIncrease + GolemStat(player, "self_offering_effect_+%")) / 100d);
            foreach (var (name, value) in offering.BaseStats)
            {
                string stat = name;
                if (stat == "life_regeneration_rate_per_minute_%_if_blocked_recently")
                {
                    if (!conn.RecentEvents.Happened(StatConditions.Blocked, now)) continue;
                    stat = "life_regeneration_rate_per_minute_%";
                }
                GolemAdd(next, stat, ActorScale.Saturate((long)Math.Floor(value * scale)));
            }
        }
        if (next.Count == state.PlayerOfferingStats.Count
            && next.All(p => state.PlayerOfferingStats.GetValueOrDefault(p.Key) == p.Value)) return;
        state.PlayerOfferingStats = next;
        state.OfferingRevision++;
        conn.Pools = PoolsFor(conn, conn.Pools.Level);
        conn.ApplyMovementSpeed();
        conn.Minions.StatRevision++;
    }
}
