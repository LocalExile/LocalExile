using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static void ReleaseArcTotem(ClientState conn, ProjectileSession session, TotemState.Totem totem, uint first, ushort sequence)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        bool Eligible(uint id, SpawnedMonster m) => m is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, Asleep: false }
            && !conn.AsleepMonsters.Contains(id);
        if (!conn.Monsters.TryGetValue(first, out var initial) || !Eligible(first, initial)
            || Dist(initial.Walk.Position(now), totem.At) > TotemTargetRange(conn, totem.SkillId)) return;

        var skill = SupportedSkill(conn, totem.SkillId);
        var caster = TotemCasterStats(CharacterPlayerStats(conn));
        var stats = MergeSkillStats(caster, skill.Stats);
        int Get(string name) => GolemStat(stats, name);
        var inputs = SupportedHitInputsFor(conn, totem.SkillId, area: false, totem: true);
        if (inputs is null) return;
        var hit = new SkillDamageContext(SkillHitKind.Spell, caster).Hit(SupportedHitFrom(inputs, suppress: false));
        var criticalRoll = CaptureSkillCriticalRoll(conn, totem.SkillId);
        var penetration = Penetration.Of(caster, Stats, false, skill.Stats);
        var resistance = HitResistMods(caster, skill.Stats);
        int range = Get("arc_chain_distance");
        int chains = Math.Max(0, Get("number_of_chains"));
        var mainTargets = new HashSet<uint>();
        uint current = first, previous = 0;

        void Land(uint id, int remaining)
        {
            var m = conn.Monsters[id];
            if (m.Invulnerable || MonsterBlocks(m, Random.Shared, spell: true)) return;
            double more = Math.Max(0, 1 + remaining * (Get("arc_damage_+%_final_for_each_remaining_chain") / 100d));
            var rolled = WithConditionalSupportDamageLocked(conn, skill.Stats, false, m,
                GameEvents.WithOutgoingDamage(conn, caster, m, hit.Scale(more).Roll(Random.Shared)), now);
            int chance = CriticalChanceOn(conn, id, SupportedCriticalChance(conn, totem.SkillId),
                SupportedCriticalChanceVsBlinded(conn, totem.SkillId, false), SupportedCriticalChanceVsProfane(conn, totem.SkillId, false));
            bool crit = RollCriticalStrike(conn, chance, skill.Stats, attack: false, skillRoll: criticalRoll, player: caster);
            var (damage, _, mitigated) = MonsterHitDamage(m, rolled, crit, false, now, resistance, penetration,
                GamblerFor(caster).InvertsResistance(Random.Shared));
            if (crit) damage = DamageModel.CriticalDamage(damage, CriticalMultiplierOn(m, SupportedCriticalMultiplier(conn, totem.SkillId)));
            if (ApplyMonsterHitLocked(conn, m, damage) is not { } accepted) return;
            session.World(totem.Id, InstanceProtocol.ActionHit(totem.Id, sequence, id, flags: (ushort)(crit ? 0x4221 : 0x4201),
                kind: 3, code: HitCode(hit)));
            if (!m.Dead && damage > 0)
                AilmentsOnHitLocked(conn, m, mitigated, true, crit, 100, caster, skill.Stats, now, Random.Shared);
            CommitAcceptedHitLocked(conn, id, m, accepted, damage, now, "Arc totem", playerKill: false);
        }

        for (int remaining = chains; ; remaining--)
        {
            var at = conn.Monsters[current].Walk.Position(now);
            mainTargets.Add(current);
            Land(current, remaining);
            if (remaining == 0) break;
            var nearby = conn.Monsters.Where(p => p.Key != current && p.Key != previous && Eligible(p.Key, p.Value))
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
