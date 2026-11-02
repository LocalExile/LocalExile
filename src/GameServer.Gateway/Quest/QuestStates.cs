using System.Text.Json;

/// <summary>
/// The quest log as the client derives it from the flags (Agents/docs/protocol/12-quests.md §1): QuestStates rows per quest,
/// the first by Order (ascending) whose FlagsPresent are all set and FlagsMissing all clear; Order 0 is "Quest Complete".
/// Also CharacterStartQuestState (the presets: <c>Act1Complete</c> is what a finished Act 1 has) and SessionQuestFlags (flags
/// that last only while the player stays: they are not saved). The server does not send the log; this mirrors it for the
/// oracle tests (<see cref="QuestSimulator"/>).
/// </summary>
sealed class QuestStates
{
    public sealed record State(int Order, IReadOnlyList<string> Present, IReadOnlyList<string> Missing, string Text, string Message)
    {
        public bool Matches(Func<string, bool> has) => Present.All(has) && !Missing.Any(has);
    }

    /// <summary>A CharacterStartQuestState row: the quests it completes and the flags it adds.</summary>
    public sealed record Preset(string Id, IReadOnlyList<string> Quests, IReadOnlyList<string> Flags);

    readonly Dictionary<string, List<State>> byQuest = new(StringComparer.Ordinal);
    readonly Dictionary<string, (int Act, string Name)> quests = new(StringComparer.Ordinal);
    readonly Dictionary<string, Preset> presets = new(StringComparer.Ordinal);
    readonly HashSet<string> session = new(StringComparer.Ordinal);

    public IReadOnlyList<State> Of(string quest) => byQuest.GetValueOrDefault(quest) ?? [];
    public string? Name(string quest) => quests.TryGetValue(quest, out var q) ? q.Name : null;
    public IEnumerable<string> QuestsOfAct(int act) => quests.Where(q => q.Value.Act == act).Select(q => q.Key);
    public Preset? FindPreset(string id) => presets.GetValueOrDefault(id);
    public IReadOnlyCollection<Preset> Presets => presets.Values;

    /// <summary>Whether a flag is a session flag (SessionQuestFlags.dat, e.g. a1q6-FairgravesAggro): never saved.</summary>
    public bool IsSession(string flag) => session.Contains(flag);

    /// <summary>The state the quest log shows, or null when no row matches (the quest has not started).</summary>
    public State? Current(string quest, Func<string, bool> has) => Of(quest).OrderBy(s => s.Order).FirstOrDefault(s => s.Matches(has));

    /// <summary>Whether the quest is complete: its Order 0 row matches, or the row shown is another ending with the same flags
    /// (a9q2's Order 0 is the feather given to Tasuni, missing IrashaTakeFeather; Order 1, the same book used, Irasha's), or
    /// with the same text and only some of an Order 0 row's flags (a7q1's Order 0 also needs a helped bandit's
    /// a2q1-QuestRewardTaken; Order 3, the book used alone, is the ending of a character that killed all three).</summary>
    public bool Complete(string quest, Func<string, bool> has) =>
        Of(quest).Any(s => s.Order == 0 && s.Matches(has))
        || Current(quest, has) is { } shown && Of(quest).Any(s => s.Order == 0 && (s.Present.Order().SequenceEqual(shown.Present.Order())
            || s.Text == shown.Text && shown.Present.All(s.Present.Contains)));

    public static readonly Lazy<QuestStates> Default = new(() => Load());

    public static QuestStates Load(string folder = "data")
    {
        var result = new QuestStates();
        string Table(string name) => Path.Combine(folder, "tables", name + ".json");
        if (!File.Exists(Table("QuestFlags")) || !File.Exists(Table("Quest"))) return result;
        string[] flags, ids;
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(Table("QuestFlags"))))
            flags = [.. doc.RootElement.EnumerateArray().Select(f => f.GetProperty("Id").GetString() ?? "")];
        string? Flag(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.GetInt32() is >= 0 and var i && i < flags.Length ? flags[i] : null;
        string[] Flags(JsonElement r, string name) => r.TryGetProperty(name, out var v) ? [.. v.EnumerateArray().Select(Flag).OfType<string>()] : [];
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(Table("Quest"))))
        {
            ids = [.. doc.RootElement.EnumerateArray().Select(q => q.GetProperty("Id").GetString() ?? "")];
            foreach (var q in doc.RootElement.EnumerateArray())
                result.quests.TryAdd(q.GetProperty("Id").GetString() ?? "", (q.GetProperty("Act").GetInt32(), q.GetProperty("Name").GetString() ?? ""));
        }
        if (File.Exists(Table("QuestStates")))
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(Table("QuestStates"))))
                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    if (s.GetProperty("Quest") is not { ValueKind: JsonValueKind.Number } qv || qv.GetInt32() is not (>= 0 and var q) || q >= ids.Length) continue;
                    if (!result.byQuest.TryGetValue(ids[q], out var list)) result.byQuest[ids[q]] = list = [];
                    list.Add(new(s.GetProperty("Order").GetInt32(), Flags(s, "FlagsPresent"), Flags(s, "FlagsMissing"),
                        s.GetProperty("Text").GetString() ?? "", s.TryGetProperty("Message", out var m) ? m.GetString() ?? "" : ""));
                }
        if (File.Exists(Table("CharacterStartQuestState")))
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(Table("CharacterStartQuestState"))))
                foreach (var p in doc.RootElement.EnumerateArray())
                {
                    string id = p.GetProperty("Id").GetString() ?? "";
                    result.presets.TryAdd(id, new(id,
                        [.. p.GetProperty("QuestKeys").EnumerateArray().Select(k => k.GetInt32()).Where(k => k >= 0 && k < ids.Length).Select(k => ids[k])],
                        Flags(p, "QuestFlags")));
                }
        if (File.Exists(Table("SessionQuestFlags")))
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(Table("SessionQuestFlags"))))
                foreach (var s in doc.RootElement.EnumerateArray())
                    if (Flag(s.GetProperty("QuestFlag")) is { } f) result.session.Add(f);
        return result;
    }
}
