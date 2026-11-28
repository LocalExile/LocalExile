using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    /// <summary>A scripted ally's own hit, without owner skill, item, or minion scaling and on-hit triggers.</summary>
    public static bool ScriptedAllyHitLocked(Player player, uint sourceId, SpawnedMonster source, uint targetId,
        SpawnedMonster target, MonsterAttack attack, double now, Random random, bool projectile = false) =>
        ScriptedAllyHitLocked(player, sourceId, source, targetId, target, attack, now, random, projectile,
            baseCritPermyriad: -1, penetration: default);

    /// <summary>Extended scripted ally hit supporting weapon/skill base critical strike chance, elemental/chaos
    /// penetration, Resolute Technique (unavoidable/non-crit), physical reduction bypass, and ailment suppression.</summary>
    public static bool ScriptedAllyHitLocked(Player player, uint sourceId, SpawnedMonster source, uint targetId,
        SpawnedMonster target, MonsterAttack attack, double now, Random random, bool projectile,
        int baseCritPermyriad, Penetration.PerType penetration, bool cannotCrit = false,
        bool ignoreEvasion = false, bool ignorePhysicalReduction = false, bool cannotInflictAilments = false)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0
            || source.Dead || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(sourceId), source)
            || !MinionMayTarget(target)
            || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(targetId), target)) return false;
        if (projectile && MonsterAvoidsProjectile(target, random)) return false;
        if (!attack.Spell && !ignoreEvasion && !DamageModel.Hits(DamageModel.AccuracyHitChance(
            target.Effects.Evasion(Monsters.BaseEvasion(target.Level) ?? 0),
            source.Effects.Accuracy(Monsters.BaseAccuracy(source.Level) ?? 0)), random.Next(100))) return false;
        if (MonsterBlocks(target, random, attack.Spell)) return false;
        int crit = baseCritPermyriad >= 0 ? baseCritPermyriad
            : attack.Effect is int effect && attack.Spell ? SkillsFor(player).SpellCritChance(effect, source.Level) ?? 0
            : MonsterBaseCritChancePermyriad;
        var rolled = MonsterModHit(source, FriendlyHitAgainst(attack, target, now).Roll(random), target.Life, target.MaxLife, random, out bool critical,
            () => target.Effects.NoExtraCriticalDamage ? 100 : target.Effects[Stat.BaseSelfCriticalStrikeMultiplierMinusPct],
            now: now, cannotCrit: cannotCrit, baseCritPermyriad: crit);
        var mitigated = MitigateMonsterHit(target, rolled, now, penetration);
        int damage = MonsterTakesMitigated(target, mitigated, attack.Spell, now, ignorePhysicalReduction: ignorePhysicalReduction, random: random);
        if (ApplyTypedMonsterHitLocked(player, target, damage,
            MonsterHitChaosDamage(target, mitigated, attack.Spell, now, damage, ignorePhysicalReduction: ignorePhysicalReduction),
            allyAt: source.Walk.Position(now)) is not { } hit) return false;
        if (hit.StageStarts)
        {
            CommitBossStage(player, targetId, target, now);
            hit = hit with { StageStarts = false };
        }
        CommitMinionHitLocked(player, targetId, target, hit, damage, now, source.Label, source: source);
        if (!target.Dead && damage > 0 && !cannotInflictAilments)
            target.Ailments.Inflict(mitigated, critical, target.MaxLife, now, name => source.Effects[name],
                name => target.Effects[name], random);
        return true;
    }

    /// <summary>Applies a scripted ally's hex or mark onto a hostile monster using the standard curse pipeline.</summary>
    public static bool ScriptedAllyApplyCurseLocked(Player player, uint casterId, SpawnedMonster target, ushort skillId,
        string curseEffect, IReadOnlyDictionary<uint, int> curseStats, double now,
        IReadOnlyDictionary<uint, int> casterStats, bool aura = false) =>
        ApplyCurseDataLocked(player, target, skillId, curseEffect, curseStats, now, aura, casterStats, casterId: casterId);

    /// <summary>Shared hostile contacts include the player's ordinary hit defences and owned fighters.</summary>
    public static Task ScriptedEnemyContact(Player player, uint id, SpawnedMonster source, MonsterAttack attack,
        Func<(int X, int Y), bool> inside, Func<bool> current, double minionFactor = 1) =>
        ArenaHazards.Hit(player, id, source, attack, inside, current, minionFactor);
}
