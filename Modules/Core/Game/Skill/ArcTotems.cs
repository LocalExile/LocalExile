using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static void ReleaseArcTotem(Player player, ProjectileSession session, TotemState.Totem totem, uint first, ushort sequence)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        bool Eligible(uint id, SpawnedMonster m) => m is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, Asleep: false }
            && !MonsterAsleep(player, id);
        if (!player.Area.Monsters.TryGetValue(first, out var initial) || !Eligible(first, initial)
            || Dist(initial.Walk.Position(now), totem.At) > TotemTargetRange(player, totem.SkillId)) return;

        var skill = SupportedSkill(player, totem.SkillId);
        var caster = TotemCasterStats(CharacterPlayerStats(player));
        var blind = BlindHitInput.Capture(caster, skill.Stats);
        var stats = MergeSkillStats(caster, skill.Stats);
        int Get(uint name) => GolemStat(stats, name);
        var inputs = SupportedHitInputsFor(player, totem.SkillId, area: false, totem: true);
        if (inputs is null) return;
        var hit = new SkillDamageContext(SkillHitKind.Spell, caster).Hit(SupportedHitFrom(inputs, suppress: false));
        var criticalRoll = CaptureSkillCriticalRoll(player, totem.SkillId);
        var penetration = Penetration.Of(caster, Stats, false, skill.Stats);
        var resistance = HitResistMods(caster, skill.Stats);
        int range = Get(Stat.ArcChainDistance);
        int chains = Math.Max(0, Get(Stat.NumberOfChains));
        var mainTargets = new HashSet<uint>();
        uint current = first, previous = 0;

        void Land(uint id, int remaining)
        {
            var m = player.Area.Monsters[id];
            if (m.Invulnerable || MonsterBlocks(m, AreaRng(player), spell: true)) return;
            double more = Math.Max(0, 1 + remaining * (Get(Stat.ArcDamagePlusPctFinalForEachRemainingChain) / 100d));
            var rolled = WithConditionalSupportDamageLocked(player, skill.Stats, false, m,
                GameEvents.WithOutgoingDamage(player, caster, m, hit.Scale(more).Roll(AreaRng(player))), now);
            int chance = CriticalChanceOn(player, id, SupportedCriticalChance(player, totem.SkillId),
                SupportedCriticalChanceVsBlinded(player, totem.SkillId, false), SupportedCriticalChanceVsGround(player, totem.SkillId, false));
            bool crit = RollCriticalStrike(player, chance, skill.Stats, attack: false, skillRoll: criticalRoll, playerStats: caster);
            var (damage, _, mitigated) = MonsterHitDamage(m, rolled, crit, false, now, resistance, penetration,
                GamblerFor(caster).InvertsResistance(AreaRng(player)));
            if (crit) damage = DamageModel.CriticalDamage(damage, CriticalMultiplierOn(m, SupportedCriticalMultiplier(player, totem.SkillId)));
            if (ApplyMonsterHitLocked(player, m, damage) is not { } accepted) return;
            session.World(totem.Id, InstanceProtocol.ActionHit(totem.Id, sequence, id, flags: (ushort)(crit ? 0x4221 : 0x4201),
                kind: 3, code: HitCode(hit)));
            if (!m.Dead && damage > 0)
                AilmentsOnHitLocked(player, id, m, mitigated, true, crit, 100, caster, skill.Stats, now, AreaRng(player),
                    blind: blind, who: CombatActors.Totem);
            OwnedKillLocked(m, accepted.Life);
            CommitAcceptedHitLocked(player, id, m, accepted, damage, now, "Arc totem", playerKill: false);
        }

        for (int remaining = chains; ; remaining--)
        {
            var at = player.Area.Monsters[current].Walk.Position(now);
            mainTargets.Add(current);
            Land(current, remaining);
            if (remaining == 0) break;
            var nearby = player.Area.Monsters.Where(p => p.Key != current && p.Key != previous && Eligible(p.Key, p.Value))
                .Select(p => (Id: p.Key, Distance: Dist(p.Value.Walk.Position(now), at)))
                .Where(p => p.Distance <= range).OrderBy(p => p.Distance).ThenBy(p => p.Id).ToList();
            if (nearby.Count == 0) break;
            uint next = nearby[0].Id;
            if (nearby.Skip(1).FirstOrDefault(p => !mainTargets.Contains(p.Id)) is { Id: > 0 } secondary)
                Land(secondary.Id, remaining - 1);
            previous = current;
            current = next;
        }
    }
}
