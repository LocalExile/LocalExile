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
        foreach (var (node, name) in save.Tattoos ?? [])
        {
            if (Passives.Find(node) is not { } passive || TattooCatalogue.Find(name) is not { } entry)
                throw new InvalidDataException($"Unknown imported passive override '{name}' on {node}.");
            if (entry.IsRunegraft && !passive.IsMastery)
                throw new InvalidDataException($"Runegraft '{name}' must replace a mastery.");
            if (!entry.GrantsSkill && !entry.Forbidden && TattooStats.For(name) is null)
                throw new InvalidDataException($"Imported passive override '{name}' has no installed server effect.");
        }
    }
}
