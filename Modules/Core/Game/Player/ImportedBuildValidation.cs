using GameServer.Core.Accounts;

public static partial class Game
{
    public static void ValidateImportedBuild(Character character, CharacterSaves.Save input)
    {
        var save = ImportedProgress.Normalize(input);
        var ordinary = save.Passives ?? [];
        var ascendancy = save.MainAscendancy ?? new();
        byte cls = (byte)character.Class;
        AscendancyRules.Validate(cls, ascendancy);
        if (save.Descendancy is byte desc && desc != 0)
            Descendancies.Validate(desc, save.DescendancyNodes ?? [], AscendancyRules.SpentPoints(cls, ascendancy),
                ascendancy.EarnedPoints, DescendancyCatalog.BonusPoints(desc));
        var jewels = (save.Inventories.GetValueOrDefault(PlayerInventories.PassiveJewels) ?? [])
            .Select(p => (p.X, Item: CharacterSaves.FromSaved(p.Item, Items))).ToArray();
        var bypass = AllocationBypassNodes(ordinary.ToHashSet(), jewels, Items.Mods, Passives);
        OrdinaryPassives.Validate(cls, ascendancy.Selection?.Family ?? AscendancyFamily.Normal, ordinary, ascendancy.Allocated,
            bypass.Keys.ToHashSet(), bypass);
        var tattoos = save.Tattoos ?? [];
        int limit = Items.Limits.Get("tattoo_total", SystemSettings.Default.TattooTotalLimit);
        if (limit > 0 && tattoos.Values.Count(name => TattooCatalogue.Find(name)?.IsRunegraft != true) > limit)
            throw new InvalidDataException($"The import exceeds the {limit}-tattoo limit.");
        foreach (var (node, name) in tattoos)
        {
            if (Passives.Find(node) is not { } passive || TattooCatalogue.Find(name) is not { } entry)
                throw new InvalidDataException($"Unknown imported passive override '{name}' on {node}.");
            if (!TattooCatalogue.TargetMatches(passive, entry.Target))
                throw new InvalidDataException($"Imported '{name}' replaces {entry.Target}, not {passive.Name}.");
            if (TattooCatalogue.TattooLimitRefusal(tattoos, node, entry, Items.Limits) is { } refusal)
                throw new InvalidDataException(refusal);
            if (!entry.GrantsSkill && !entry.Forbidden && TattooStats.For(name) is null)
                throw new InvalidDataException($"Imported passive override '{name}' has no installed server effect.");
        }
    }
}
