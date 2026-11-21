using System.Text.Json;
using GameServer.Core;

/// <summary>
/// The quest log as the client derives it from the flags (Agents/docs/protocol/12-quests.md §1): QuestStates rows per quest,
/// the first by Order (ascending) whose FlagsPresent are all set and FlagsMissing all clear; Order 0 is "Quest Complete".
/// Also CharacterStartQuestState (the presets: <c>Act1Complete</c> is what a finished Act 1 has) and SessionQuestFlags (flags
/// that last only while the player stays: they are not saved). The server does not send the log; this mirrors it for the
/// oracle tests (<see cref="QuestSimulator"/>).
/// </summary>
public sealed class QuestStates
{
    public sealed record State(int Order, IReadOnlyList<string> Present, IReadOnlyList<string> Missing, string Text, string Message)
    {
        /// <summary>League quest flags required for this state (<c>QuestStates.__col13</c>, 1-based).</summary>
        public IReadOnlyList<int> LeaguePresent { get; init; } = [];
        /// <summary>League quest flags that must be absent for this state (<c>QuestStates.__col14</c>, 1-based).</summary>
        public IReadOnlyList<int> LeagueMissing { get; init; } = [];
        /// <summary>WorldAreas rows where this state applies (<c>QuestStates.Area</c>); empty for any area.</summary>
        public IReadOnlyList<int> Areas { get; init; } = [];

        public bool Matches(Func<string, bool> has) => Matches(has, null, null);

        public bool Matches(Func<string, bool> has, Func<int, bool>? hasLeague, int? areaRow = null) =>
            Present.All(has) && !Missing.Any(has)
            && (hasLeague is null ? LeaguePresent.Count == 0 : LeaguePresent.All(hasLeague) && !LeagueMissing.Any(hasLeague))
            && (areaRow is null || Areas.Count == 0 || Areas.Contains(areaRow.Value));
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
    public State? Current(string quest, Func<string, bool> has) => Current(quest, has, null, null);

    /// <summary>The state the quest log shows with optional league quest flags and current WorldAreas row.</summary>
    public State? Current(string quest, Func<string, bool> has, Func<int, bool>? hasLeague, int? areaRow = null) =>
        Of(quest).OrderBy(s => s.Order).FirstOrDefault(s => s.Matches(has, hasLeague, areaRow));

    /// <summary>Whether the quest is complete: its Order 0 row matches, or the row shown is another ending with the same flags
    /// (a9q2's Order 0 is the feather given to Tasuni, missing IrashaTakeFeather; Order 1, the same book used, Irasha's), or
    /// with the same text and only some of an Order 0 row's flags (a7q1's Order 0 also needs a helped bandit's
    /// a2q1-QuestRewardTaken; Order 3, the book used alone, is the ending of a character that killed all three).</summary>
    public bool Complete(string quest, Func<string, bool> has) => Complete(quest, has, null, null);

    /// <summary>Whether the quest is complete with optional league quest flags and current WorldAreas row.</summary>
    public bool Complete(string quest, Func<string, bool> has, Func<int, bool>? hasLeague, int? areaRow = null) =>
        Of(quest).Any(s => s.Order == 0 && s.Matches(has, hasLeague, areaRow))
        || Current(quest, has, hasLeague, areaRow) is { } shown && Of(quest).Any(s => s.Order == 0
            && (s.Present.Order().SequenceEqual(shown.Present.Order()) && s.LeaguePresent.Order().SequenceEqual(shown.LeaguePresent.Order())
                || s.Text == shown.Text && shown.Present.All(s.Present.Contains) && shown.LeaguePresent.All(s.LeaguePresent.Contains)));

    public static readonly Lazy<QuestStates> Default = new(() => Load());

    public static QuestStates Load(string folder = "data")
    {
        var result = new QuestStates();
        if (!ClientTables.Has("QuestFlags") || !ClientTables.Has("Quest")) return result;
        string[] flags, ids;
        flags = [.. ClientTables.Table("QuestFlags").Select(f => f.Text("Id") ?? "")];
        string? Flag(int? e) => e is >= 0 and int i && i < flags.Length ? flags[i] : null;
        string[] Flags(ClientRow r, string name) => [.. r.Ints(name).Select(i => Flag(i)).OfType<string>()];
        var doc = ClientTables.Table("Quest");
        {
            ids = [.. doc.Select(q => q.Text("Id") ?? "")];
            foreach (var q in doc)
                result.quests.TryAdd(q.Text("Id") ?? "", (q.Int("Act"), q.Text("Name") ?? ""));
        }
        int[] Ints(ClientRow r, string name) => r.Has(name) && !r.IsNull(name) ? r.Ints(name) : [];
        if (ClientTables.Has("QuestStates"))
            foreach (var s in ClientTables.Table("QuestStates"))
            {
                if (s.IntOrNull("Quest") is not int qv || qv is not (>= 0 and var q) || q >= ids.Length) continue;
                if (!result.byQuest.TryGetValue(ids[q], out var list)) result.byQuest[ids[q]] = list = [];
                list.Add(new(s.Int("Order"), Flags(s, "FlagsPresent"), Flags(s, "FlagsMissing"),
                    s.Text("Text") ?? "", s.TextOrNull("Message") ?? "")
                {
                    LeaguePresent = Ints(s, "__col13"),
                    LeagueMissing = Ints(s, "__col14"),
                    Areas = Ints(s, "Area"),
                });
            }
        if (ClientTables.Has("CharacterStartQuestState"))
            foreach (var p in ClientTables.Table("CharacterStartQuestState"))
            {
                string id = p.Text("Id") ?? "";
                result.presets.TryAdd(id, new(id,
                    [.. p.Ints("QuestKeys").Where(k => k >= 0 && k < ids.Length).Select(k => ids[k])],
                    Flags(p, "QuestFlags")));
            }
        if (ClientTables.Has("SessionQuestFlags"))
            foreach (var s in ClientTables.Table("SessionQuestFlags"))
                if (Flag(s.IntOrNull("QuestFlag")) is { } f) result.session.Add(f);
        return result;
    }
}
