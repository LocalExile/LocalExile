using GameServer.Core.Protocol.Instance;

public sealed partial class UniqueCatalog
{
    public sealed record RollGroup(int Count, string[] Options, int? HighLevel = null, int? HighLevelCount = null);

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
            var options = pool.Options.ToArray();
            if (count < 0 || options.Length < count) return false;
            for (int i = 0; i < count; i++)
            {
                int choice = random.Next(i, options.Length);
                (options[i], options[choice]) = (options[choice], options[i]);
                if (items.Mods.ById(options[i]) is not { } mod) return false;
                mods.Add(mod.Roll(ModSection.Explicit, random));
            }
        }
        return true;
    }
}
