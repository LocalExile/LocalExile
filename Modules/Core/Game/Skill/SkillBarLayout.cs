

/// <summary>
/// Where new skills go on the skill bar (S→C 0x53, Agents/docs/protocol/11-skills.md §11). A saved bar maps slot →
/// GrantedEffects Id, "Action/&lt;Id&gt;" or "" (<see cref="CharacterSaves.Save.SkillBar"/>). Only empty slots take new
/// skills: the slots the player set stay as they are.
/// </summary>
static class SkillBarLayout
{
    /// <summary>The left mouse button: the default attack's slot on a new bar.</summary>
    public const int AttackSlot = 0;

    /// <summary>The slots in the order new skills take them: the right mouse button (2), Q W E R T (3–7), the middle mouse
    /// button (1), then the rest.</summary>
    public static readonly int[] FillOrder = [2, 3, 4, 5, 6, 7, 1, 8, 9, 10, 11, 12];

    /// <summary>
    /// The entries to add to <paramref name="bar"/>: the default attack on the left mouse button when that slot was never
    /// set, and each of <paramref name="skills"/> that is nowhere on the bar on the first empty slot of
    /// <see cref="FillOrder"/>. A slot is empty when it was never set, was cleared (""), or holds a skill
    /// <paramref name="has"/> says the character no longer has (its gem left the equipment).
    /// </summary>
    public static List<(int Slot, string Entry)> Fill(IReadOnlyDictionary<int, string> bar, string attack, IEnumerable<string> skills,
        Func<string, bool> has)
    {
        var added = new List<(int Slot, string Entry)>();
        if (!bar.ContainsKey(AttackSlot)) added.Add((AttackSlot, attack));
        bool Empty(int slot) => added.All(a => a.Slot != slot) && (!bar.TryGetValue(slot, out var entry) || entry == "" || !has(entry));
        foreach (var skill in skills.Distinct().Where(s => !bar.Values.Contains(s)))
            if (FillOrder.FirstOrDefault(Empty, -1) is int slot and >= 0)
                added.Add((slot, skill));
        return added;
    }
}
