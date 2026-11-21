using GameServer.Core.Protocol.Instance;

internal static class VaalVesselUniques
{
    sealed record Reward(string Name, int MinimumLevel, UniqueCatalog.Definition? Equipment);

    static readonly RuleTable<GameData.Snapshot, Reward[]> Rewards = new();

    static Reward[] Load(GameData.Snapshot _)
    {
        var equipment = GameDataCatalog.Uniques.Where(d => !d.EncounterOnly).ToDictionary(d => (d.Name, d.BaseTypeId));
        var rewards = new List<Reward>();
        foreach (var row in GameData.Store!.LoadRows("vaal_vessel_unique_drops", "name"))
        {
            string name = (string)row["name"]!;
            UniqueCatalog.Definition? definition = null;
            // An operator can disable an equipment definition or reserve it for an encounter.
            if (row["base_type"] is string id && !equipment.TryGetValue((name, id), out definition)) continue;
            rewards.Add(new(name, Convert.ToInt32(row["drop_level"]), definition));
        }
        return [.. rewards];
    }

    internal static IEnumerable<ItemDrops.Drop> Roll(ItemCatalog items, ItemRolls rolls, UniqueCatalog uniques,
        ItemDrops drops, int level, int count, Random random)
    {
        var eligible = Rewards.GetValue(GameData.Current, Load).Where(r => r.MinimumLevel <= level);
        foreach (var reward in drops.GuaranteedUniques(eligible, r => r.Name, count, random))
        {
            var item = reward.Equipment is { } definition
                ? uniques.Create(definition with { ItemLevel = (byte)level }, random)!
                : uniques.NamedMap(reward.Name)! with { ItemLevel = (byte)level };
            var type = items.ByHash(item.TypeHash)!;
            var mod = rolls.CorruptionImplicit(type, level, random)
                ?? throw new InvalidOperationException($"No corruption implicit for {reward.Name} at item level {level}");
            var implicitMod = mod.Roll(ModSection.Implicit, random);
            var mods = item.Mods.ToList();
            var slots = Enumerable.Range(0, mods.Count).Where(index => mods[index].Section == ModSection.Implicit).ToArray();
            if (slots.Length == 0) mods.Insert(0, implicitMod);
            else mods[slots[random.Next(slots.Length)]] = implicitMod;
            yield return new(item with { Mods = mods, Corrupted = true, Identified = false }, type);
        }
    }
}
