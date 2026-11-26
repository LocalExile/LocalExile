using GameServer.Data.Terrain.Generation;

/// <summary>The map's special shrine counts reserve rooms from the generator's existing shrine budget.</summary>
public static class MapShrines
{
    public static List<ShrinePlacement.Placed> Plan(IReadOnlyList<PlacedTileObject> rooms,
        Func<int, int, bool> walkable, uint seed, MapMods? mods)
    {
        var placed = ShrinePlacement.Plan(rooms, walkable, seed,
            greedChancePct: mods?.Get(Stat.MapShrinesChanceToBeGreedShrinePct) ?? 0);
        int evolving = Math.Clamp(mods?.Get(Stat.MapNumExtraEvolvingShrines) ?? 0, 0, placed.Count);
        int apparitions = Math.Clamp(mods?.Get(Stat.MapNumExtraApparitionShrines) ?? 0, 0, placed.Count - evolving);
        if (evolving + apparitions == 0) return placed;
        var random = new Random(unchecked((int)(seed ^ 0x53485249)));
        // Reserve cells without favouring the entrance or the generator's traversal order. Never add another room.
        int[] slots = Enumerable.Range(0, placed.Count).ToArray();
        random.Shuffle(slots);
        for (int i = 0; i < evolving + apparitions; i++)
        {
            string id = i < evolving ? "UpgradeOnKill" : ShrineCatalog.ApparitionIds[random.Next(ShrineCatalog.ApparitionIds.Length)];
            if (ShrineCatalog.Find(id) is { } shrine) placed[slots[i]] = placed[slots[i]] with { Shrine = shrine };
        }
        return placed;
    }
}
