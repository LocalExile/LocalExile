/// <summary>The mastery effects that count allocated masteries (PassiveSkillMasteryEffects Attributes3-5 and Life1):
/// "+5 to Strength per Allocated Mastery Passive Skill" and "10% more Maximum Life if you have at least 6 Life
/// Masteries allocated". Every allocated mastery node has its effect chosen, so allocated mastery nodes are counted;
/// a runegrafted one too, since the Runegraft replaces the node's effect, not the allocated mastery.</summary>
public static class MasteryCounts
{
    const int LifeMasteriesNeeded = 6;

    static readonly (uint PerMastery, uint Attribute)[] PerMasteryAttributes =
    [
        (Stat.AdditionalStrengthPerAllocatedMastery, Stat.AdditionalStrength),
        (Stat.AdditionalDexterityPerAllocatedMastery, Stat.AdditionalDexterity),
        (Stat.AdditionalIntelligencePerAllocatedMastery, Stat.AdditionalIntelligence),
    ];

    /// <summary>Adds the per-mastery attributes to the passive sum and drops the Life Mastery's more Life while fewer
    /// than six Life masteries are allocated. Returns the attributes added, for the wide attribute view.</summary>
    public static List<(uint Attribute, long Value)> Apply(Dictionary<uint, int> passives, IEnumerable<ushort> nodes,
        PassiveTree tree)
    {
        var added = new List<(uint, long)>();
        bool lifeMore = passives.ContainsKey(Stat.LifeMasteryCountMaximumLifePlusPctFinal);
        if (!lifeMore && !PerMasteryAttributes.Any(rule => passives.ContainsKey(rule.PerMastery))) return added;
        var masteries = nodes.Where(n => tree.Find(n) is { IsMastery: true }).ToArray();
        foreach (var (perMastery, attribute) in PerMasteryAttributes)
            if (passives.GetValueOrDefault(perMastery) is int value and not 0)
            {
                long total = (long)value * masteries.Length;
                passives[attribute] = ActorScale.Saturate(passives.GetValueOrDefault(attribute) + total);
                added.Add((attribute, total));
            }
        // A Life mastery is one whose group offers this effect.
        if (lifeMore && masteries.Count(n => tree.EffectsFor(n)
                .Any(e => e.Stats.Any(s => s.Key == Stat.LifeMasteryCountMaximumLifePlusPctFinal))) < LifeMasteriesNeeded)
            passives.Remove(Stat.LifeMasteryCountMaximumLifePlusPctFinal);
        return added;
    }
}
