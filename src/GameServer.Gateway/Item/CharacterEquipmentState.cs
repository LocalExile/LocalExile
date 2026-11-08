using System.Collections.Concurrent;

// Item ids and native gem ids belong to one installed build, not just to a character name.
// Keep this state across rule reloads, but retire it after a successful build replacement.
static class CharacterEquipmentState
{
    internal static readonly ConcurrentDictionary<string, HashSet<uint>> Inactive = new(StringComparer.OrdinalIgnoreCase);

    internal static void Replaced(string name)
    {
        Inactive.TryRemove(name, out _);
        GemSkillIds.ByCharacter.TryRemove(name, out _);
    }
}
