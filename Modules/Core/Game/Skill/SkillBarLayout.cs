

/// <summary>
/// Where new skills go on the skill bar (S→C 0x53, Agents/docs/protocol/11-skills.md §11). A saved bar maps slot →
/// GrantedEffects Id, "Action/&lt;Id&gt;" or "" (<see cref="CharacterSaves.Save.SkillBar"/>). Only empty slots take new
/// skills: the slots the player set stay as they are.
/// </summary>
public static class SkillBarLayout
{
    /// <summary>The left mouse button: the default attack's slot on a new bar.</summary>
    public const int AttackSlot = 0;

    /// <summary>The slots in the order new skills take them: the right mouse button (2), Q W E R T (3–7), the middle mouse
    /// button (1), then the rest.</summary>
    public static readonly int[] FillOrder = [2, 3, 4, 5, 6, 7, 1, 8, 9, 10, 11, 12];

    const string UnassignedKey = "Game.SkillBar.Unassigned";

    public static HashSet<string> Unassigned(CharacterSaves.Save? save) =>
        (save?.ModuleData?.GetValueOrDefault(UnassignedKey) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    public static void Bind(string character, byte slot, string entry)
    {
        Saves.Atomically(() =>
        {
            var save = CharacterSaves.Load(character);
            string? previous = save?.SkillBar?.GetValueOrDefault(slot);
            var unassigned = Unassigned(save);
            bool changed = unassigned.Remove(entry);
            if (!string.IsNullOrEmpty(previous) && previous != entry && !previous.StartsWith("Action/"))
                changed |= unassigned.Add(previous);
            if (changed) CharacterSaves.ChangeModuleData(character, UnassignedKey, _ => string.Join('|', unassigned.Order()));
            CharacterSaves.SetSkillBar(character, slot, entry);
            return true;
        });
    }

    /// <summary>
    /// The entries to add to <paramref name="bar"/>: the default attack on the left mouse button when that slot was never
    /// set, and each of <paramref name="skills"/> that is nowhere on the bar on the first empty slot of
    /// <see cref="FillOrder"/>. A slot is empty when it was never set, or holds a skill
    /// <paramref name="has"/> says the character no longer has (its gem left the equipment).
    /// </summary>
    public static List<(int Slot, string Entry)> Fill(IReadOnlyDictionary<int, string> bar, string attack, IEnumerable<string> skills,
        Func<string, bool> has)
    {
        var added = new List<(int Slot, string Entry)>();
        if (!bar.ContainsKey(AttackSlot)) added.Add((AttackSlot, attack));
        bool Empty(int slot) => added.All(a => a.Slot != slot) && (!bar.TryGetValue(slot, out var entry) || entry.Length > 0 && !has(entry));
        foreach (var skill in skills.Distinct().Where(s => !bar.Values.Contains(s)))
            if (FillOrder.FirstOrDefault(Empty, -1) is int slot and >= 0)
                added.Add((slot, skill));
        return added;
    }
}
