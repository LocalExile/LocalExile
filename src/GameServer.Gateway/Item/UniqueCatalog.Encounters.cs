using GameServer.Core.Protocol.Instance;

public sealed partial class UniqueCatalog
{
    /// <summary>A pool the item draws <paramref name="Count"/> different rows from, each by its weight (null or a missing
    /// entry: 1).</summary>
    public sealed record RollGroup(int Count, string[] Options, int? HighLevel = null, int? HighLevelCount = null,
        int[]? Weights = null);

    public sealed partial record Definition
    {
        public int? EncounterItemLevelBonus { get; init; }
    }

    bool RollEncounterModifiers(Definition definition, Random random, List<ItemMod> mods)
    {
        foreach (var pool in definition.RollGroups ?? [])
        {
            int count = pool.HighLevel is int level && definition.ItemLevel >= level
                ? pool.HighLevelCount ?? pool.Count : pool.Count;
            // A row the item already has is not drawn again: pools may overlap (Precursor's Emblem's Two-Stone rings
            // draw one mod of each charge and a third of either).
            var options = pool.Options
                .Select((id, i) => (Id: id, Weight: pool.Weights is { } w && i < w.Length ? w[i] : 1))
                .Where(o => o.Weight > 0 && (items.Mods.ById(o.Id) is not { } row || !mods.Any(mod => mod.Hash == row.Hash)))
                .ToList();
            if (count < 0 || options.Count < count) return false;
            for (int i = 0; i < count; i++)
            {
                // Weighted, without replacement: Voices' 1-passive row has weight 5 of 1000 (poedb).
                int roll = random.Next(options.Sum(o => o.Weight)), pick = 0;
                while (roll >= options[pick].Weight) roll -= options[pick++].Weight;
                var chosen = options[pick];
                options.RemoveAt(pick);
                if (items.Mods.ById(chosen.Id) is not { } mod) return false;
                mods.Add(mod.Roll(ModSection.Explicit, random));
            }
        }
        return true;
    }
}
