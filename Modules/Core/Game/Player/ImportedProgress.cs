using System.Globalization;

// Imports can supply module-owned progress intent. Apply it once, against the latest quest state,
// through the same quest flags/reward tables as normal play, without replacing the other campaign progress.
public static class ImportedProgress
{
    public const string BanditKey = "Game.ImportedBanditChoice", PointsKey = "Game.ImportedPassiveExtra";
    static readonly string[] RewardFlags = ["a2q1-QuestRewardTaken", "a2q2-QuestRewardTaken", "a2q3-QuestRewardTaken", "a2q7-QuestRewardTaken"];
    static readonly string[] KillFlags = ["a2b-KilledDexBandit", "a2b-KilledIntBandit", "a2b-KilledStrBandit"];
    static readonly string[] AllyFlags = ["a2b-DexAlly", "a2b-IntAlly", "a2b-StrAlly"];

    public static CharacterSaves.Save Normalize(CharacterSaves.Save save)
    {
        if (save.ModuleData is not { } data || !data.ContainsKey(BanditKey) && !data.ContainsKey(PointsKey)) return save;
        var remaining = new Dictionary<string, string>(data);
        var flags = new HashSet<string>(save.QuestFlags ?? [], StringComparer.Ordinal);
        if (remaining.Remove(BanditKey, out string? bandit))
        {
            int choice = bandit switch { "Kraityn" => 0, "Alira" => 1, "Oak" => 2, "Eramir" => 3,
                _ => throw new InvalidDataException($"Unknown imported bandit choice '{bandit}'.") };
            flags.ExceptWith(RewardFlags); flags.ExceptWith(KillFlags); flags.ExceptWith(AllyFlags);
            flags.Add(RewardFlags[choice]);
            for (int i = 0; i < KillFlags.Length; i++)
                if (i == choice) flags.Add(AllyFlags[i]); else flags.Add(KillFlags[i]);
            flags.Add("a2b-Congratulated");
            if (choice == 3) flags.Add("a2q7-EramirCombined"); else flags.Remove("a2q7-EramirCombined");
        }
        ushort extra = save.ExtraPassivePoints ?? 0;
        if (remaining.Remove(PointsKey, out string? budget))
        {
            int needed = int.Parse(budget, NumberStyles.None, CultureInfo.InvariantCulture);
            if (needed is < 0 or > ushort.MaxValue) throw new InvalidDataException("Invalid imported passive point budget.");
            extra = checked((ushort)Math.Max(0, needed - QuestStaticRewards.Default.Value.PassivePoints(flags)));
        }
        return save with { QuestFlags = flags.ToList(), ExtraPassivePoints = extra, ModuleData = remaining.Count == 0 ? null : remaining };
    }

    public static void Restore(string character)
    {
        Saves.Atomically(() =>
        {
            if (CharacterSaves.Load(character) is not { } current) return;
            var next = Normalize(current);
            if (!ReferenceEquals(next, current)) CharacterSaves.Write(character, next);
        });
    }
}
