namespace GameServer.Data;

/// <summary>Keep the base game's ordinary/Bloodline graph and substitute only the selected main ascendancy family.</summary>
public static class AdditionalAscendancyGraph
{
    public static PassiveSkillGraph Merge(PassiveSkillGraph ordinary, PassiveSkillGraph phrecia, IReadOnlySet<uint> mainAscendancyIds)
    {
        if (ordinary.Kind != phrecia.Kind || !ordinary.Orbits.SequenceEqual(phrecia.Orbits))
            throw new InvalidDataException("The ascendancy graphs use incompatible orbit layouts.");
        // This field is zero in both target graphs. Do not silently renumber an unknown future group association.
        if (ordinary.Groups.Concat(phrecia.Groups).Any(g => g.Association != 0))
            throw new InvalidDataException("A nonzero passive group association needs an explicit merge rule.");
        var keep = ordinary.Nodes.Keys.Where(id => !mainAscendancyIds.Contains(id))
            .Concat(phrecia.Nodes.Keys.Where(mainAscendancyIds.Contains)).ToHashSet();
        var groups = new List<PassiveSkillGraph.Group>();
        void Append(PassiveSkillGraph graph, bool ascendancy)
        {
            foreach (var group in graph.Groups)
            {
                var nodes = group.Nodes.Where(n => mainAscendancyIds.Contains(n.Id) == ascendancy)
                    .Select(n => n with { Connections = n.Connections.Where(keep.Contains).ToArray() }).ToArray();
                if (nodes.Length > 0) groups.Add(group with { Nodes = nodes });
            }
        }
        Append(ordinary, false);
        Append(phrecia, true);
        return new PassiveSkillGraph(ordinary.Kind, ordinary.Orbits, ordinary.Roots.Where(keep.Contains).ToArray(), groups);
    }
}
