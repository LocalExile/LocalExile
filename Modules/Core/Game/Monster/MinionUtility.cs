using GameServer.Core.Protocol.Instance;

static partial class Game
{
    internal static bool MinionUtility(string? effect) => effect is "ConvocationOld" or "Convocation"
        or "FleshOffering" or "BoneOffering" or "SpiritOffering" or "SignalPrey";

    static IEnumerable<GolemState.Golem> UtilityFighters(Player player) =>
        player.Golems.All.Values.Concat(player.Summons.Fighters.Values).Concat(player.Reservations.Fighters.Values)
            .Where(g => !g.Monster.Dead && g.Monster.Life > 0);

    internal static async Task<bool> CastMinionUtility(Player player, ushort source, (int X, int Y) aim,
        uint target, int recovery, Func<bool> current)
    {
        var gem = SupportedSkill(player, source);
        string? effect = SkillsFor(player).EffectId(gem.Effect);
        if (!MinionUtility(effect)) return false;
        bool recall = effect is "Convocation" or "ConvocationOld";
        double release = AreaTime(player) + (recall || effect == "SignalPrey" ? 0 : (CastMs(player, gem.Effect, source) ?? 1000) / 1000d);
        var lifetime = CombatLifetime;
        while (true)
        {
            if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || player.Closed
                || player.LeavingArea || player.PlayerLife == 0 || recovery != player.ActionInterruptionGeneration
                || !SameTriggerSource(gem, SupportedSkill(player, source))) return false;
            if (!player.Paused && AreaTime(player) >= release)
            {
                if (effect == "SignalPrey") return SignalPreyLocked(player, source, gem, aim, target, release);
                if (!recall) return StartOfferingLocked(player, source, gem, effect!, aim, target, release);
                RecallMinionsLocked(player, gem, release);
                return true;
            }
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
    }

    static void QueueUtilityEffect(Player player, (int X, int Y) at, string art, double seconds, string log,
        uint parent = 0, byte flags0 = EffectObject.FixedRotation)
    {
        uint effectId = player.TakeObjectId();
        var obj = new EffectObject(at.X, at.Y, art)
        {
            AnimatedFlags = 9,
            Flags0 = flags0,
            Parent = parent,
        };
        AddTemporarySkillEffect(player, effectId, seconds);
        player.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(effectId,
            InstanceEntities.ObjectTypeHash(EffectObject.TypePath), obj.Blob()), log, WorldId: effectId, CreatesObject: true));
        player.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(effectId), log + " wake", WorldId: effectId));
        player.Minions.OutputQueue.Add(new(InstanceProtocol.AnimationUpdate(effectId, 0, 0, 1), log + " play", WorldId: effectId));
    }

    internal static void RecallMinionsLocked(Player player, SupportedGem gem, double now)
    {
        var playerStats = CharacterPlayerStats(player);
        var playerPos = player.Walk.Position(now);
        QueueUtilityEffect(player, playerPos, "Metadata/Effects/Spells/convocation/convocation_player.ao",
            1.5, "Convocation caster effect", parent: player.Id);
        QueueUtilityEffect(player, playerPos, "Metadata/Effects/Spells/convocation/convocation.ao",
            1.5, "Convocation effect");
        double duration = SkillDurationModifiers.Seconds(GolemStat(gem.Stats, Stat.BaseSkillEffectDuration),
            n => GolemStat(playerStats, n), n => GolemStat(gem.Stats, n));
        double rate = GolemStat(gem.Stats, Stat.LifeRegenerationRatePerMinutePct)
            * Math.Max(0, 1 + GolemStat(playerStats, Stat.ConvocationBuffEffectPlusPct) / 100d);
        foreach (var minion in player.Minions.All)
        {
            var m = minion.Monster;
            if (m.Dead || m.Life == 0 || player.Summons.Definitions.GetValueOrDefault(minion.Id) is { Follows: false }
                || OwnedFighter(player, minion.Id) is { } whispering && HearsWhispers(whispering, now)) continue;
            var at = NextToPlayer(player);
            m.Walk = player.NewMonsterWalk(minion.Id, at.X, at.Y, Math.Max(m.Walk.Speed, 1), LivingCollision.Size(m), false);
            m.ActionGeneration++; m.LastTarget = null; m.BusyUntil = now;
            if (OwnedFighter(player, minion.Id) is { } fighter)
            {
                fighter.Pending = null; fighter.Target = 0; fighter.LastStep = now;
                fighter.Pulses.RemoveAll(p => p.Follows);
                fighter.RecallUntil = now + duration; fighter.RecallRate = rate;
            }
            player.Minions.OutputQueue.Add(new(InstanceProtocol.Teleport(minion.Id, at.X, at.Y), "Convocation recall", minion.Id, m));
        }
    }

    internal static bool StartOfferingLocked(Player player, ushort source, SupportedGem gem, string effect,
        (int X, int Y) aim, uint target, double now)
    {
        var playerStats = CharacterPlayerStats(player);
        bool livingZombies = GolemStat(playerStats, Stat.RaisedZombiesAreUsableAsCorpsesWhenAlive) > 0;
        GolemState.Golem? LivingZombie(SpawnedMonster corpse) => livingZombies && !corpse.Dead && corpse.Life > 0
            && player.Summons.Fighters.GetValueOrDefault(corpse.ObjectId) is { Definition.Kind: "Zombie" } zombie
            && ReferenceEquals(zombie.Monster, corpse) && GolemCurrent(player, zombie) ? zombie : null;
        bool Usable(SpawnedMonster m) => !m.CorpseConsumed && !m.Ailments.Shattered
            && (LivingZombie(m) is not null || m.Hostile && m.Dead
                && CorpseTypeAllowsUse(m, minionSkill: SkillsFor(player).HasType(gem.Effect, "Minion"),
                    cannotUseSpectreCorpses: GolemStat(gem.Stats, Stat.CannotUseSpectreCorpses) != 0));
        var first = player.Area.Monsters.GetValueOrDefault(target);
        if (first is null || !Usable(first)) first = player.Area.Monsters.Values.Where(Usable)
            .Where(m => Dist(m.Walk.Position(now), aim) <= 15).MinBy(m => Dist(m.Walk.Position(now), aim));
        if (first is null && GolemStat(playerStats, Stat.OfferingSkillsDoNotRequireCorpses) == 0) return false;
        var centre = first?.Walk.Position(now) ?? aim;
        var corpses = player.Area.Monsters.Where(p => Usable(p.Value) && Dist(p.Value.Walk.Position(now), centre) <= 15)
            .OrderBy(p => ReferenceEquals(p.Value, first) ? 0 : 1).ThenBy(p => Dist(p.Value.Walk.Position(now), centre)).Take(5).ToArray();
        string dir = effect == "FleshOffering" ? "offering_offensive" : effect == "BoneOffering" ? "offering_defensive" : "offering_spirit";
        string burstAo = $"Metadata/Effects/Spells/{dir}/goresplosion.ao";
        string castAo = $"Metadata/Effects/Spells/{dir}/offering_offensive_souls.ao";
        string auraAo = effect == "FleshOffering"
            ? $"Metadata/Effects/Spells/{dir}/offering_offensive_effect.ao"
            : $"Metadata/Effects/Spells/{dir}/rig.ao";
        foreach (var (id, corpse) in corpses)
        {
            var corpseAt = corpse.Walk.Position(now);
            corpse.CorpseConsumed = true;
            NoteCorpseConsumedLocked(player, corpse);
            player.RecentEvents.Record(StatConditions.ConsumedCorpse, now);
            if (LivingZombie(corpse) is { } zombie)
            {
                GolemDieLocked(player, zombie, now, emitLife: false);
                RemoveSummonLocked(player, zombie, now);
            }
            else if (!corpse.CreatedAsCorpse)
                player.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(id), "Offering corpse consumed"));
            QueueUtilityEffect(player, corpseAt, burstAo, 1.5, "Offering corpse burst");
        }
        QueueUtilityEffect(player, centre, castAo, 1.5, "Offering souls effect");
        QueueUtilityEffect(player, centre, auraAo, 1.5, "Offering area effect");
        int extra = Math.Max(0, corpses.Length - 1);
        string kind = effect == "FleshOffering" ? "flesh" : effect == "BoneOffering" ? "bone" : "spirit";
        double duration = SkillDurationModifiers.Seconds(GolemStat(gem.Stats, Stat.BaseSkillEffectDuration)
                + extra * GolemStat(gem.Stats, Stat.OfferingSkillEffectDurationPerCorpse),
            n => GolemStat(playerStats, n) + (n == Stat.SkillEffectDurationPlusPct ? GolemStat(playerStats, Stat.OfferingDurationPlusPct)
                + GolemStat(playerStats, Stat.Of(kind + "_offering_duration_+%")) : 0), n => GolemStat(gem.Stats, n));
        int increase = GolemStat(playerStats, Stat.OfferingSpellsEffectPlusPct)
            + GolemStat(playerStats, Stat.Of(kind + "_offering_effect_+%")) + GolemStat(gem.Stats, Stat.OfferingSpellsEffectPlusPct);
        double scale = Math.Max(0, 1 + increase / 100d);
        Dictionary<uint, int> stats = [], baseStats = [];
        void Add(uint to, uint from, int bonus = 0)
        {
            baseStats[to] = GolemStat(gem.Stats, from) + bonus;
            stats[to] = ActorScale.Saturate((long)Math.Floor(baseStats[to] * scale));
        }
        ushort buff;
        if (effect == "FleshOffering")
        {
            buff = 295;
            Add(Stat.AttackSpeedPlusPct, Stat.AttackSpeedPlusPctGrantedFromSkill, GolemStat(playerStats, Stat.FleshOfferingAttackSpeedPlusPct));
            Add(Stat.CastSpeedPlusPct, Stat.CastSpeedPlusPctGrantedFromSkill); Add(Stat.BaseMovementVelocityPlusPct, Stat.BaseMovementVelocityPlusPct);
        }
        else if (effect == "BoneOffering")
        {
            buff = 296;
            Add(Stat.MonsterBaseBlockPct, Stat.MonsterBaseBlockPct, GolemStat(playerStats, Stat.BoneOfferingBlockChancePlusPct));
            Add(Stat.BaseSpellBlockPct, Stat.BaseSpellBlockPct);
            Add(Stat.LifeRegenerationRatePerMinutePctIfBlockedRecently, Stat.MinionLifeRegenerationRatePerMinutePctIfBlockedRecently);
        }
        else
        {
            buff = 663;
            Add(Stat.CriticalStrikeChancePlusPct, Stat.SpiritOfferingCriticalStrikeChancePlusPct);
            Add(Stat.BaseCriticalStrikeMultiplierPlus, Stat.SpiritOfferingCriticalStrikeMultiplierPlus);
        }
        foreach (var fighter in UtilityFighters(player))
            if (fighter.UtilityBuffs.Remove(buff, out _)) player.Area.Buffs.Clear(fighter.Id, buff, player.Id);
        var all = player.Summons.Offerings;
        all.RemoveAll(o => o.Effect == effect || now >= o.Until);
        int limit = GolemStat(playerStats, Stat.AllowMultipleOfferings) != 0 ? 3 : GolemStat(playerStats, Stat.Allow2Offerings) != 0 ? 2 : 1;
        while (all.Count >= limit) all.RemoveAt(0);
        all.Add(new(effect, now + duration, buff, source, stats, baseStats, increase));
        MinionUtilityTick(player, now);
        return true;
    }

    static Dictionary<uint, int> MinionOfferingStats(Player player, double now)
    {
        var stats = new Dictionary<uint, int>();
        foreach (var offering in player.Summons.Offerings.Where(o => o.Until > now))
            foreach (var (key, value) in offering.Stats) stats[key] = stats.GetValueOrDefault(key) + value;
        return stats;
    }

    internal static void MinionUtilityTick(Player player, double now)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused) return;
        MinionTacticsTick(player, now);
        if (player.PlayerLife == 0) player.Summons.Offerings.Clear();
        player.Summons.Offerings.RemoveAll(o => now >= o.Until);
        // Read only when an offering or a fighter needs it: a full stat build each tick (~66 a second) was most of an idle
        // player's stat builds and allocation (live 2026-11-20).
        Dictionary<uint, int>? stats = null;
        Dictionary<uint, int> Player() => stats ??= new Dictionary<uint, int>(CharacterPlayerStats(player));
        if (player.Summons.Offerings.Count > 1)
        {
            int limit = GolemStat(Player(), Stat.AllowMultipleOfferings) != 0 ? 3 : GolemStat(Player(), Stat.Allow2Offerings) != 0 ? 2 : 1;
            while (player.Summons.Offerings.Count > limit) player.Summons.Offerings.RemoveAt(0);
        }
        SelfOfferingTick(player, Player, now);
        foreach (var fighter in UtilityFighters(player))
        {
            var active = player.Summons.Offerings;
            foreach (var (row, instance) in fighter.UtilityBuffs.ToArray())
                if (!active.Any(o => o.Buff == row))
                { player.Area.Buffs.Clear(fighter.Id, row, player.Id); fighter.UtilityBuffs.Remove(row); }
            foreach (var offering in active)
                if (!fighter.UtilityBuffs.ContainsKey(offering.Buff))
                {
                    fighter.UtilityBuffs[offering.Buff] = 1;
                    player.Area.Buffs.Set(fighter.Id, new(offering.Buff, player.Id, offering.Source, U68: Buff.Seconds(offering.Until - now)));
                }
            var offered = MinionOfferingStats(player, now);
            if (fighter.Utility.Count == offered.Count && offered.All(p => fighter.Utility.GetValueOrDefault(p.Key) == p.Value)) continue;
            fighter.Utility = offered; fighter.InputHash = 0;
            RefreshGolemLocked(player, fighter, Player());
        }
    }

    static void AddSelfOfferingStats(Player player, StatMap stats)
    {
        if (player.PlayerLife > 0)
            foreach (var (key, value) in player.Summons.PlayerOfferingStats)
                stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + value);
    }

    static void SelfOfferingTick(Player player, Func<IReadOnlyDictionary<uint, int>> stats, double now)
    {
        var state = player.Summons;
        if (state.Offerings.Count == 0 && state.PlayerOfferingBuffs.Count == 0 && state.PlayerOfferingStats.Count == 0) return;
        var playerStats = stats();
        var active = GolemStat(playerStats, Stat.OfferingsAlsoBuffYou) != 0 ? state.Offerings.ToArray() : [];
        foreach (var (row, sent) in state.PlayerOfferingBuffs.ToArray())
            if (!active.Any(o => ReferenceEquals(o, sent.Offering)))
            {
                player.Area.Buffs.Clear(player.Id, row, player.Id);
                state.PlayerOfferingBuffs.Remove(row);
            }
        foreach (var offering in active)
            if (!state.PlayerOfferingBuffs.ContainsKey(offering.Buff))
            {
                state.PlayerOfferingBuffs[offering.Buff] = (offering, 1);
                player.Area.Buffs.Set(player.Id, new(offering.Buff, player.Id, offering.Source, U68: Buff.Seconds(offering.Until - now)));
            }
        Dictionary<uint, int> next = [];
        foreach (var offering in active)
        {
            double scale = Math.Max(0, 1 + (offering.EffectIncrease + GolemStat(playerStats, Stat.SelfOfferingEffectPlusPct)) / 100d);
            foreach (var (name, value) in offering.BaseStats)
            {
                uint stat = name;
                if (stat == Stat.LifeRegenerationRatePerMinutePctIfBlockedRecently)
                {
                    if (!player.RecentEvents.Happened(StatConditions.Blocked, now)) continue;
                    stat = Stat.LifeRegenerationRatePerMinutePct;
                }
                GolemAdd(next, stat, ActorScale.Saturate((long)Math.Floor(value * scale)));
            }
        }
        if (next.Count == state.PlayerOfferingStats.Count
            && next.All(p => state.PlayerOfferingStats.GetValueOrDefault(p.Key) == p.Value)) return;
        state.PlayerOfferingStats = next;
        state.OfferingRevision++; MarkPlayerStats(player, PlayerStatStep.Offerings);
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        player.Minions.StatRevision++;
    }
}
