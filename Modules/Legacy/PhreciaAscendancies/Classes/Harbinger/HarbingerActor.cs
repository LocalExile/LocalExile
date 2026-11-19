using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

/// <summary>The native profile for an immortal companion. The common spawn executor owns object ids,
/// type registration, collision/walk, atGate acceptance and the player-minion registry.</summary>
internal static class HarbingerActor
{
    internal static ushort SkillId(HarbingerCompanionCatalog.Companion source, SkillCatalog skills, int effect)
    {
        int index = effect == source.WarpEffect ? 0
            : 1 + source.Buffs.Select(b => b.Effect).ToList().IndexOf(effect);
        if (index == 0 && effect != source.WarpEffect)
            throw new ArgumentException("Effect is not a companion skill", nameof(effect));
        return skills.SkillIds().ElementAt(index);
    }

    internal static IReadOnlyList<GrantedSkill> Skills(HarbingerCompanionCatalog.Companion source, SkillCatalog skills) =>
        new[] { source.WarpEffect }.Concat(source.Buffs.Select(b => b.Effect))
            .Select(effect => skills.FindByRow(SkillId(source, skills, effect), effect, 1,
                [.. skills.EffectStats(effect, 1), (Stat.SkillIsNonPathing, 1)]))
            .OfType<GrantedSkill>().ToArray();

    internal static IReadOnlyList<(uint Key, int Value)> Stats(HarbingerCompanionCatalog.Companion source,
        Func<string, uint?> key)
    {
        var stats = source.MinionStats.ToDictionary();
        stats[Stat.MaximumLife] = 1;
        foreach (uint flag in new[] { Stat.BaseCannotBeDamaged, Stat.BaseCannotBeStunned,
            Stat.UntargetableByMonsterAi, Stat.IsPlayerMinion, Stat.IsSemanticMinion })
            stats[flag] = 1;
        return [.. stats.Select(s => (s.Key, s.Value))];
    }

    internal static IReadOnlyDictionary<uint, int> Counts(IEnumerable<HarbingerCompanionCatalog.Companion> sources,
        IReadOnlyList<HarbingerRoster.Member> members) => sources.Where(s => s.CountStat.HasValue)
            .ToDictionary(s => s.CountStat!.Value, s => members.Any(m => m.Source.Node == s.Node) ? 1 : 0);
}
