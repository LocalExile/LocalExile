using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static bool PlacedArcPayload(string? effect) => effect is "Arc" or "ArcAltY";

    static void ReleaseArcTotem(Player player, ProjectileSession session, TotemState.Totem totem, uint first, ushort sequence) =>
        ReleaseArcProxy(player, session, totem.SkillId, totem.Id, totem.At, totem.BodySize,
            TotemTargetRange(player, totem.SkillId), first, sequence, totem: true);

    const double PlacedArcTargetRange = 60;

    static KeyValuePair<uint, SpawnedMonster> PlacedArcTarget(Player player, PlacedSkills.Placed placed,
        double radius, double now) =>
        player.Area.MonstersNear(placed.At, radius)
            .Where(p => ArcProxyTarget(player, p.Key, p.Value) && Dist(p.Value.Walk.Position(now), placed.At) <= radius)
            .OrderBy(p => Dist(p.Value.Walk.Position(now), placed.At)).ThenBy(p => p.Key).FirstOrDefault();

    static void ReleasePlacedArc(Player player, ProjectileSession session, PlacedSkills.Placed placed,
        KeyValuePair<uint, SpawnedMonster> triggeringEnemy, double damageMore, WorldRecordBuilder bytes)
    {
        double now = ProjectileWorldTime(player);
        var target = triggeringEnemy.Value is not null ? triggeringEnemy : PlacedArcTarget(player, placed, PlacedArcTargetRange, now);
        if (target.Value is null || !MonsterPresent(player, target.Key, target.Value)) return;
        var at = target.Value.Walk.Position(now);
        bytes.World(placed.Id, InstanceProtocol.ActionStarted(placed.Id, placed.At.X, placed.At.Y,
            at.X, at.Y, 0x4000, 0xffff, player.MonsterOrderFlags, targetObjectId: target.Key));
        ReleaseArcProxy(player, session, placed.Skill, placed.Id, placed.At, 0, PlacedArcTargetRange,
            target.Key, 0xffff, totem: false, mine: placed.Mine, damageMore: damageMore);
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
        var mainTargets = new HashSet<uint>();
        uint current = first, previous = 0;

        void Land(uint id, int remaining, (int X, int Y) from)
        {
            var m = player.Area.Monsters[id];
            if (m.Invulnerable || MonsterBlocks(m, AreaRng(player), spell: true)) return;
            double more = damageMore * Math.Max(0, 1 + remaining * (Get(Stat.ArcDamagePlusPctFinalForEachRemainingChain) / 100d));
            var rolled = WithConditionalSupportDamageLocked(player, skill.Stats, false, m,
                GameEvents.WithOutgoingDamage(player, caster, m, hit.Scale(more).Roll(AreaRng(player))), now);
            int chance = CriticalChanceOn(player, id, SupportedCriticalChance(player, skillId),
                SupportedCriticalChanceVsBlinded(player, skillId, false), SupportedCriticalChanceVsGround(player, skillId, false));
            bool crit = RollCriticalStrike(player, chance, skill.Stats, attack: false, skillRoll: criticalRoll, playerStats: caster);
            var (damage, _, mitigated) = MonsterHitDamage(m, rolled, crit, false, now, resistance, penetration,
                GamblerFor(caster).InvertsResistance(AreaRng(player)), AreaRng(player), trapOrMine: !totem);
            if (crit) damage = DamageModel.CriticalDamage(damage,
                CriticalMultiplierOn(m, SupportedCriticalMultiplier(player, skillId), now, caster, skill.Stats));
            int chaosDamage = MonsterHitChaosDamage(m, mitigated, spell: true, now, damage,
                totem ? 0 : BearTrapDamageTaken(m, now));
            if (ApplyTypedMonsterHitLocked(player, m, damage, chaosDamage, allyAt: from) is not { } accepted) return;
            if (mine) LeechPlacedMineHitLocked(player, m, mitigated, spell: true, damage, now, caster, id);
            var targetAt = m.Walk.Position(now);
            if (!m.Dead && damage > 0)
                AilmentsOnHitLocked(player, id, m, mitigated, true, crit, 100, caster, skill.Stats, now, AreaRng(player),
                    blind: blind, hitOrigin: from, attackerSize: bodySize, attackerHasReversedStat: false, playerHit: false);
            byte hitCode = HitCode(mitigated, damage, m.MaxLife, crit || accepted.Life == 0);
            ushort hitFlags = (ushort)(0x4200 | HitTypeFlag(hitCode) | (crit ? 0x0020 : 0));
            byte hitKind = 3, hitAngle = HitAngle(targetAt.X - from.X, targetAt.Y - from.Y);
            ConsumeNativeKnockbackLocked(m, ref hitFlags, ref hitKind, ref hitAngle, out ushort u3c, out ushort u3e);
            session.World(emitter, InstanceProtocol.ActionHit(emitter, sequence, id, flags: hitFlags,
                kind: hitKind, code: hitCode, angle: hitAngle, u3c: u3c, u3e: u3e));
            if (accepted.StageStarts)
            {
                CommitProjectileBossStage(session, id, m, session.Runtime.Now);
                accepted = accepted with { StageStarts = false };
            }
            OwnedKillLocked(m, accepted.Life);
            CommitAcceptedHitLocked(player, id, m, accepted, damage, now, totem ? "Arc totem" : "placed Arc", playerKill: false);
        }

        bool canOscillate = Get(Stat.ArcEnhancedBehaviour) != 0;
        for (int remaining = chains; ; remaining--)
        {
            var at = player.Area.Monsters[current].Walk.Position(now);
            mainTargets.Add(current);
            Land(current, remaining, origin);
            if (remaining == 0) break;
            var nearby = player.Area.MonstersNear(at, range).Where(p => p.Key != current && (canOscillate || p.Key != previous)
                    && ArcProxyTarget(player, p.Key, p.Value))
                .Select(p => (Id: p.Key, Distance: Dist(p.Value.Walk.Position(now), at)))
                .Where(p => p.Distance <= range).OrderBy(p => p.Distance).ThenBy(p => p.Id).ToList();
            if (nearby.Count == 0) break;
            uint next = nearby[0].Id;
            if (nearby.Skip(1).FirstOrDefault(p => !mainTargets.Contains(p.Id)) is { Id: > 0 } secondary)
                Land(secondary.Id, remaining - 1, at);
            origin = at;
            previous = current;
            current = next;
        }
    }
}
