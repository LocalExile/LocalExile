using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string VaalArcEffect = "VaalArc";
    const string ArcSurgingEffect = "ArcAltX";
    const ushort LuckyArcBuffRow = 1152;

    static bool PlacedArcPayload(string? effect) => effect is "Arc" or "ArcAltY";
    static bool ArcProxyEffect(string? effect) => PlacedArcPayload(effect) || effect == ArcSurgingEffect;

    static int ArcExtraRolls(Player player, string? effect, IReadOnlyDictionary<uint, int> caster) =>
        effect is "Arc" or ArcSurgingEffect or "ArcAltY" or VaalArcEffect
            ? Math.Max(caster.GetValueOrDefault(Stat.ArcExtraDamageRolls),
                player.Area.Buffs.Has(player.Id, LuckyArcBuffRow, player.Id) ? 1 : 0) : 0;

    static void ReleaseArcTotem(Player player, ProjectileSession session, TotemState.Totem totem, uint first,
        ushort sequence, Func<bool> current) => ScheduleArcProxyContact(player, session, totem.SkillId, totem.Effect,
            current, () => ReleaseArcProxy(player, session, totem.SkillId, totem.Id, totem.At, totem.BodySize,
                TotemTargetRange(player, totem.SkillId), first, sequence, totem: true));

    const double PlacedArcTargetRange = 60;

    static KeyValuePair<uint, SpawnedMonster> PlacedSpellTarget(Player player, (int X, int Y) origin,
        double radius, double now) =>
        player.Area.MonstersNear(origin, radius)
            .Where(p => ArcProxyTarget(player, p.Key, p.Value) && Dist(p.Value.Walk.Position(now), origin) <= radius)
            .OrderBy(p => Dist(p.Value.Walk.Position(now), origin)).ThenBy(p => p.Key).FirstOrDefault();

    static void ReleasePlacedArc(Player player, ProjectileSession session, PlacedSkills.Placed placed,
        KeyValuePair<uint, SpawnedMonster> triggeringEnemy, double damageMore, WorldRecordBuilder bytes, Func<bool> current)
    {
        double now = ProjectileWorldTime(player);
        var target = triggeringEnemy.Value is not null ? triggeringEnemy : PlacedSpellTarget(player, placed.At, PlacedArcTargetRange, now);
        if (target.Value is null || !MonsterPresent(player, target.Key, target.Value)) return;
        var at = target.Value.Walk.Position(now);
        bytes.World(placed.Id, InstanceProtocol.ActionStarted(placed.Id, placed.At.X, placed.At.Y,
            at.X, at.Y, 0x4000, 0xffff, player.MonsterOrderFlags, targetObjectId: target.Key));
        if (SkillsFor(player).EffectId(placed.Effect) == ArcSurgingEffect)
            ScheduleArcProxyContact(player, session, placed.Skill, placed.Effect, current,
                () => ReleaseSurgingPlacement(player, session, placed, target.Key, damageMore));
        else ReleaseArcProxy(player, session, placed.Skill, placed.Id, placed.At, 0, PlacedArcTargetRange,
            target.Key, 0xffff, totem: false, mine: placed.Mine, damageMore: damageMore);
    }

    sealed record PlacedArcSplit(ProjectileSession Session, uint Primary, IReadOnlySet<uint> Targets);

    static void ReleaseSurgingPlacement(Player player, ProjectileSession session, PlacedSkills.Placed placed,
        uint first, double damageMore)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        if (!player.Area.Monsters.TryGetValue(first, out var primary) || !ArcProxyTarget(player, first, primary)) return;
        var at = primary.Walk.Position(now);
        if (Dist(at, placed.At) > PlacedArcTargetRange) return;
        int range = placed.Own.GetValueOrDefault(Stat.ArcChainDistance);
        var caster = placed.Offense?.Caster ?? CharacterPlayerStats(player);
        var targets = ArcSplitTargets(player, first, at, now, range, ArcSplitCount(placed.Own, caster)).ToHashSet();
        targets.Add(first);
        PlacedAreaDamageLocked(player, placed, at, range, now, damageMore, currentSkill: true, spellDamage: true,
            arcSplit: new(session, first, targets));
    }

    static void ScheduleArcProxyContact(Player player, ProjectileSession session, ushort skill, int effect,
        Func<bool> current, Action release)
    {
        int delay = SkillsFor(player).EffectId(effect) == ArcSurgingEffect
            ? SupportedSkill(player, skill).Stats.GetValueOrDefault(Stat.ArcAdditionalDelayMs) : 0;
        if (delay <= 0) release();
        else session.ScheduleContact(session.Runtime.Now + ProjectileRuntime.Time(delay / 1000d),
            () => player.PlayerLife > 0 && !player.Paused && current(), release);
    }

    static void ScheduleTriggeredArc(Player player, ProjectileSession session,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        (int X, int Y)? brandOrigin, Func<bool> current, uint triggerTarget)
    {
        var sacrifice = new SacrificeUse(player, gem.Id, gem.Effect, gem.Gem, triggered: true);
        var critical = CaptureSkillCriticalRoll(player, gem.Id);
        ScheduleArcContact(player, session, gem, aim, brandOrigin, current, triggerTarget, session.Runtime.Now,
            0xffff, critical, sacrifice);
    }

    static void ScheduleArcContact(Player player, ProjectileSession session,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        (int X, int Y)? brandOrigin, Func<bool> current, uint triggerTarget, long due, ushort sequence,
        SkillCriticalRoll? critical, SacrificeUse sacrifice, double damageMore = 1, int beamChains = 0, double hitAndAilmentMore = 1)
    {
        session.Runtime.Schedule(due, () =>
        {
            bool Current() => session.Active && current() && sacrifice.Current;
            if (!Current()) return;
            double now = ProjectileWorldTime(player);
            string? effect = SkillsFor(player).EffectId(gem.Effect);
            var source = SupportedSkill(player, gem.Id);
            var caster = CharacterPlayerStats(player);
            if (effect == VaalArcEffect)
            {
                double duration = SkillDurationModifiers.Seconds(source.Stats.GetValueOrDefault(Stat.BaseSkillEffectDuration),
                    key => caster.GetValueOrDefault(key), key => source.Stats.GetValueOrDefault(key),
                    DurationKinds(player, gem.Effect) | SkillDurationModifiers.Kinds.Buff);
                if (duration > 0) player.Area.Buffs.Set(player.Id, new(LuckyArcBuffRow, player.Id, gem.Id,
                    Values: [1], U68: Buff.Seconds(duration)));
            }
            var origin = brandOrigin ?? player.Walk.Position(now);
            uint first = triggerTarget != 0 && player.Area.Monsters.TryGetValue(triggerTarget, out var enemy)
                && ArcProxyTarget(player, triggerTarget, enemy) ? triggerTarget : player.Area.MonstersNear(aim, PlacedArcTargetRange)
                .Where(p => ArcProxyTarget(player, p.Key, p.Value))
                .Select(p => (p.Key, Distance: Dist(p.Value.Walk.Position(now), aim)))
                .Where(p => p.Distance <= PlacedArcTargetRange)
                .OrderBy(p => p.Distance).ThenBy(p => p.Key).Select(p => p.Key).FirstOrDefault();
            if (first == 0) return;
            var stats = MergeSkillStats(caster, source.Stats);
            int Get(uint key) => stats.GetValueOrDefault(key);
            int range = Get(Stat.ArcChainDistance);
            int chains = Math.Max(0, Get(Stat.NumberOfChains)) + beamChains;
            if (effect == VaalArcEffect) VaalArcChainTargets(player, first, origin, now, range, chains, Land);
            else ArcChainTargets(player, first, origin, now, range, chains, effect == "ArcAltY", Land);

            void Land(uint victim, int remaining, (int X, int Y) from)
            {
                var at = player.Area.Monsters[victim].Walk.Position(now);
                double chainMore = Math.Max(0, 1 + remaining * (Get(Stat.ArcDamagePlusPctFinalForEachRemainingChain) / 100d));
                var task = AreaSkillHitForUse(null, player, gem.Id, gem, from, at, immediate: true, sequence: sequence,
                    radiusOverride: 1, damageMore: damageMore, criticalRoll: critical, sacrifice: sacrifice,
                    triggeredCurrent: Current, scheduledSession: session, targetFilter: (id, _) => id == victim,
                    hitAndAilmentMore: hitAndAilmentMore * chainMore);
                if (!task.IsCompleted) throw new InvalidOperationException("Scheduled Arc hit must not yield.");
                task.GetAwaiter().GetResult();
            }
        });
    }

    static void ScheduleVaalArc(Player player, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) aim, uint target, ushort sequence, int castMs, double acceptedAt, VaalUseReservation paid,
        Func<bool> current)
    {
        using (ProjectileMutation(player, advance: false))
        {
            var session = ProjectilesFor(player);
            var source = SupportedSkill(player, gem.Id);
            var sacrifice = new SacrificeUse(player, gem.Id, gem.Effect, gem.Gem);
            double contact = acceptedAt + castMs / 1000d
                * Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject);
            ScheduleArcContact(player, session, gem, aim, null, () => current() && PlayerStateAllowsAction(player)
                && player.ActionInterruptionGeneration == paid.RecoveryGeneration
                && SameTriggerSource(source, SupportedSkill(player, gem.Id)), target,
                Math.Max(session.Runtime.Now, ProjectileRuntime.Time(contact)), sequence, paid.CriticalRoll, sacrifice,
                beamChains: paid.Beidat?.Chains ?? 0, hitAndAilmentMore: paid.PactDamageMore);
        }
    }

    static bool ArcProxyTarget(Player player, uint id, SpawnedMonster m) =>
        m is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, Asleep: false, Invulnerable: false, WorldHidden: false }
        && !MonsterAsleep(player, id);

    static void ReleaseArcProxy(Player player, ProjectileSession session, ushort skillId, uint emitter,
        (int X, int Y) origin, int bodySize, double targetRange, uint first, ushort sequence,
        bool totem, bool mine = false, double damageMore = 1)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        if (!player.Area.Monsters.TryGetValue(first, out var initial) || !ArcProxyTarget(player, first, initial)
            || Dist(initial.Walk.Position(now), origin) > targetRange) return;

        var skill = SupportedSkill(player, skillId);
        var caster = TotemCasterStats(CharacterPlayerStats(player));
        var blind = BlindHitInput.Capture(caster, skill.Stats);
        var stats = MergeSkillStats(caster, skill.Stats);
        int Get(uint name) => GolemStat(stats, name);
        var inputs = SupportedHitInputsFor(player, skillId, area: false, totem: totem);
        if (inputs is null) return;
        var hit = new SkillDamageContext(SkillHitKind.Spell, caster).Hit(SupportedHitFrom(inputs, suppress: false));
        var criticalRoll = CaptureSkillCriticalRoll(player, skillId);
        var penetration = Penetration.Of(caster, Stats, false, skill.Stats);
        var resistance = HitResistMods(caster, skill.Stats);
        int range = Get(Stat.ArcChainDistance);
        int chains = Math.Max(0, Get(Stat.NumberOfChains));
        string? effect = SkillsFor(player).EffectId(skill.Effect);
        int extraRolls = ArcExtraRolls(player, effect, caster);
        void Land(uint id, int remaining, (int X, int Y) from)
        {
            var m = player.Area.Monsters[id];
            if (m.Invulnerable || MonsterBlocks(m, AreaRng(player), spell: true)) return;
            double more = damageMore * Math.Max(0, 1 + remaining * (Get(Stat.ArcDamagePlusPctFinalForEachRemainingChain) / 100d));
            var rolled = WithConditionalSupportDamageLocked(player, skill.Stats, false, m,
                GameEvents.WithOutgoingDamage(player, caster, m, hit.Scale(more).Roll(AreaRng(player), extraRolls)), now);
            int chance = CriticalChanceOn(player, id, SupportedCriticalChance(player, skillId),
                SupportedCriticalChanceVsBlinded(player, skillId, false), SupportedCriticalChanceVsGround(player, skillId, false));
            bool crit = RollCriticalStrike(player, chance, skill.Stats, attack: false, skillRoll: criticalRoll, playerStats: caster);
            var (damage, _, mitigated) = MonsterHitDamage(m, rolled, crit, false, now, resistance, penetration,
                HitRollsFor(caster).InvertsResistance(AreaRng(player)), AreaRng(player), trapOrMine: !totem);
            if (crit) damage = DamageModel.CriticalDamage(damage,
                CriticalMultiplierOn(m, SupportedCriticalMultiplier(player, skillId), now, caster, skill.Stats));
            int chaosDamage = MonsterHitChaosDamage(m, mitigated, spell: true, now, damage,
                totem ? 0 : BearTrapDamageTaken(m, now));
            if (ApplyTypedMonsterHitLocked(player, m, damage, chaosDamage, allyAt: from) is not { } accepted) return;
            if (mine) LeechPlacedMineHitLocked(player, m, mitigated, spell: true, damage, now, caster, id);
            if (!m.Dead && damage > 0)
                AilmentsOnHitLocked(player, id, m, mitigated, true, crit, 100, caster, skill.Stats, now, AreaRng(player),
                    blind: blind, hitOrigin: from, attackerSize: bodySize, attackerHasReversedStat: false, playerHit: false);
            WriteArcProxyHit(session, emitter, sequence, id, m, from, now, mitigated, damage, crit, accepted.Life);
            if (accepted.StageStarts)
            {
                CommitProjectileBossStage(session, id, m, session.Runtime.Now);
                accepted = accepted with { StageStarts = false };
            }
            OwnedKillLocked(m, accepted.Life);
            CommitAcceptedHitLocked(player, id, m, accepted, damage, now, totem ? "Arc totem" : "placed Arc", playerKill: false);
        }

        if (effect == ArcSurgingEffect)
        {
            var at = initial.Walk.Position(now);
            var splitTargets = ArcSplitTargets(player, first, at, now, range, ArcSplitCount(skill.Stats, caster)).ToArray();
            Land(first, 0, origin);
            foreach (uint target in splitTargets) Land(target, 0, at);
        }
        else ArcChainTargets(player, first, origin, now, range, chains, effect == "ArcAltY", Land);
    }

    static void WriteArcProxyHit(ProjectileSession session, uint emitter, ushort sequence, uint id, SpawnedMonster monster,
        (int X, int Y) from, double now, DamagePacket mitigated, int damage, bool critical, uint life)
    {
        var at = monster.Walk.Position(now);
        byte code = HitCode(mitigated, damage, monster.MaxLife, critical || life == 0);
        ushort flags = (ushort)(0x4200 | HitTypeFlag(code) | (critical ? 0x0020 : 0));
        byte kind = 3, angle = HitAngle(at.X - from.X, at.Y - from.Y);
        ConsumeNativeKnockbackLocked(monster, ref flags, ref kind, ref angle, out ushort u3c, out ushort u3e);
        session.World(emitter, InstanceProtocol.ActionHit(emitter, sequence, id, flags: flags,
            kind: kind, code: code, angle: angle, u3c: u3c, u3e: u3e));
    }

    static int ArcSplitCount(IReadOnlyDictionary<uint, int> skill, IReadOnlyDictionary<uint, int> caster) =>
        (int)Math.Clamp((long)skill.GetValueOrDefault(Stat.ProjectileNumberToSplit)
            + caster.GetValueOrDefault(Stat.ProjectileNumberToSplit)
            + skill.GetValueOrDefault(Stat.NumberOfChains) + caster.GetValueOrDefault(Stat.NumberOfChains), 0, 256);

    static IEnumerable<uint> ArcSplitTargets(Player player, uint primary, (int X, int Y) at, double now, int range, int splits) =>
        player.Area.MonstersNear(at, range).Where(p => p.Key != primary && ArcProxyTarget(player, p.Key, p.Value))
            .Select(p => (Id: p.Key, Distance: Dist(p.Value.Walk.Position(now), at)))
            .Where(p => p.Distance <= range).OrderBy(p => p.Distance).ThenBy(p => p.Id).Take(splits).Select(p => p.Id);

    static void ArcChainTargets(Player player, uint first, (int X, int Y) origin, double now, int range, int chains,
        bool canOscillate, Action<uint, int, (int X, int Y)> land)
    {
        var mainTargets = new HashSet<uint>();
        uint current = first, previous = 0;
        for (int remaining = chains; ; remaining--)
        {
            var at = player.Area.Monsters[current].Walk.Position(now);
            mainTargets.Add(current);
            land(current, remaining, origin);
            if (remaining == 0) break;
            var nearby = player.Area.MonstersNear(at, range).Where(p => p.Key != current && (canOscillate || p.Key != previous)
                    && ArcProxyTarget(player, p.Key, p.Value))
                .Select(p => (Id: p.Key, Distance: Dist(p.Value.Walk.Position(now), at)))
                .Where(p => p.Distance <= range).OrderBy(p => p.Distance).ThenBy(p => p.Id).ToList();
            if (nearby.Count == 0) break;
            uint next = nearby[0].Id;
            if (nearby.Skip(1).FirstOrDefault(p => !mainTargets.Contains(p.Id)) is { Id: > 0 } secondary)
                land(secondary.Id, remaining - 1, at);
            origin = at;
            previous = current;
            current = next;
        }
    }

    static void VaalArcChainTargets(Player player, uint first, (int X, int Y) origin, double now, int range, int chains,
        Action<uint, int, (int X, int Y)> land)
    {
        var targeted = new HashSet<uint> { first };
        var pending = new Queue<(uint Id, int Remaining, (int X, int Y) From)>();
        pending.Enqueue((first, chains, origin));
        while (pending.TryDequeue(out var branch))
        {
            if (!player.Area.Monsters.TryGetValue(branch.Id, out var monster) || !ArcProxyTarget(player, branch.Id, monster)) continue;
            var at = monster.Walk.Position(now);
            land(branch.Id, branch.Remaining, branch.From);
            if (branch.Remaining == 0) continue;
            var next = player.Area.MonstersNear(at, range)
                .Where(p => !targeted.Contains(p.Key) && ArcProxyTarget(player, p.Key, p.Value))
                .Select(p => (Id: p.Key, Distance: Dist(p.Value.Walk.Position(now), at)))
                .Where(p => p.Distance <= range).OrderBy(p => p.Distance).ThenBy(p => p.Id).Take(2);
            foreach (var target in next)
            {
                targeted.Add(target.Id);
                pending.Enqueue((target.Id, branch.Remaining - 1, at));
            }
        }
    }
}
