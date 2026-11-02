using System.Globalization;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// What the campaign driver's dev commands (scripts/dev/campaign, Agents/docs/dev/campaign-automation.md) decide without a
/// connection: /state's reply lines, the monsters /kill picks and the quest flags of /quest_preset. Rules.Campaign.cs runs
/// the commands.
/// </summary>
static class CampaignState
{
    /// <summary>The most characters of /state's JSON per chat line: longer replies continue on "STATE+ " lines.</summary>
    public const int LineLength = 900;

    public const string Prefix = "STATE ", ContinuationPrefix = "STATE+ ";

    /// <summary>/state's reply: "STATE " and the JSON's first <paramref name="length"/> characters, then the rest on
    /// "STATE+ " lines, in order (the driver joins them). Splits never cut a UTF-16 surrogate pair (but at length 1).</summary>
    public static IReadOnlyList<string> Lines(string json, int length = LineLength)
    {
        var lines = new List<string>();
        int at = 0;
        do
        {
            int take = Math.Min(length, json.Length - at);
            // Never end a line between a surrogate pair; a line always takes at least one character (a length of 1, or
            // a lone high surrogate, still moves on).
            if (take < json.Length - at && char.IsHighSurrogate(json[at + take - 1])) take = Math.Max(1, take - 1);
            lines.Add((lines.Count == 0 ? Prefix : ContinuationPrefix) + json.Substring(at, take));
            at += take;
        } while (at < json.Length);
        return lines;
    }

    /// <summary>
    /// The living hostile monsters a /kill target names, as /tp names things: <c>all</c>; <c>boss</c> (the unique monsters
    /// of the area, its bosses, and those with the big boss life bar or a boss stage); <c>near [radius]</c> (default <see cref="NearRadius"/> cells around the player); an object
    /// id (0x hex or decimal); else a name: the monsters whose name, MonsterVarieties Id or variety name is the given one,
    /// else contains it (case ignored), every one of them unless a trailing number picks the n-th. Ordered by distance
    /// from the player. Empty with <paramref name="error"/> when nothing matches.
    /// </summary>
    public static List<(uint Id, SpawnedMonster Monster)> KillTargets(IReadOnlyDictionary<uint, SpawnedMonster> monsters,
        (int X, int Y) player, double now, IReadOnlyList<string> args, out string? error)
    {
        error = null;
        if (args.Count == 0) { error = "Usage: /kill <id> | <name> [n] | boss | near [radius] | all"; return []; }
        double Distance(SpawnedMonster m) { var (x, y) = m.Walk.Position(now); return Math.Sqrt((double)(x - player.X) * (x - player.X) + (double)(y - player.Y) * (y - player.Y)); }
        var living = monsters.Where(kv => kv.Value.Hostile && !kv.Value.Dead && kv.Value.Life > 0)
            .OrderBy(kv => Distance(kv.Value)).Select(kv => (kv.Key, kv.Value)).ToList();
        string first = args[0].ToLowerInvariant();
        List<(uint, SpawnedMonster)> picked;
        if (first == "all" && args.Count == 1) picked = living;
        else if (first == "boss" && args.Count == 1) picked = [.. living.Where(m => IsBoss(m.Value))];
        else if (first == "near" && args.Count <= 2)
        {
            double radius = args.Count == 2 ? double.Parse(args[1], CultureInfo.InvariantCulture) : NearRadius;
            picked = [.. living.Where(m => Distance(m.Value) <= radius)];
        }
        else if (args.Count == 1 && Id(args[0]) is { } id && monsters.ContainsKey(id))
            picked = [.. living.Where(m => m.Key == id)];
        else
        {
            var words = args.ToList();
            int? index = null;
            if (words.Count > 1 && int.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1)
            {
                index = n;
                words.RemoveAt(words.Count - 1);
            }
            string name = string.Join(' ', words);
            bool Exact(SpawnedMonster m) => Names(m).Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            bool Contains(SpawnedMonster m) => Names(m).Any(s => s.Contains(name, StringComparison.OrdinalIgnoreCase));
            var matches = living.Where(m => Exact(m.Value)).ToList();
            if (matches.Count == 0) matches = [.. living.Where(m => Contains(m.Value))];
            picked = index is { } i ? i <= matches.Count ? [matches[i - 1]] : [] : matches;
            if (picked.Count == 0)
                error = index is not null && matches.Count > 0 ? $"Only {matches.Count} living monster(s) called \"{name}\" here."
                    : $"No living monster called \"{name}\" here. /mobs lists them.";
            return picked;
        }
        if (picked.Count == 0) error = $"No living monster matches \"{string.Join(' ', args)}\" here.";
        return picked;

        static IEnumerable<string> Names(SpawnedMonster m) =>
            m.Variety is { } v ? [m.Label, v.Id, v.Name] : [m.Label];
    }

    /// <summary>/kill near's default radius in cells.</summary>
    public const double NearRadius = 40;

    /// <summary>A boss: a unique monster (the campaign's act and quest bosses are), or one with the big life bar or a stage.</summary>
    public static bool IsBoss(SpawnedMonster m) => m.Rarity == MonsterRarity.Unique || m.Variety?.BossHealthBar == true || m.Stage is not null;

    static uint? Id(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h) ? h : null
            : uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint d) ? d : null;

    /// <summary>A CharacterStartQuestState row: the quest flags a character starting at that point has.</summary>
    public sealed record Preset(string Id, IReadOnlyList<string> Flags);

    /// <summary>The CharacterStartQuestState presets in table order, as the quest engine loaded them (QuestStates).</summary>
    public static IReadOnlyList<Preset> LoadPresets() =>
        [.. QuestStates.Default.Value.Presets.Select(p => new Preset(p.Id, p.Flags))];

    /// <summary>The waypoints a preset gives (CharacterStartQuestState Areas, WorldAreas Ids: every one has a waypoint).
    /// The demo starts list where they begin (E3StartOfAct7: The Brine King's Reef, where Weylam sails to Act 7;
    /// EpilogueAtlasDemo: Karui Shores), the console starts every waypoint up to their point. Empty for an unknown preset.</summary>
    public static IReadOnlyList<string> PresetWaypoints(string id, string tables = "data/tables")
    {
        string presetsFile = Path.Combine(tables, "CharacterStartQuestState.json"), areasFile = Path.Combine(tables, "WorldAreas.json");
        if (!File.Exists(presetsFile) || !File.Exists(areasFile)) return [];
        using var areas = JsonDocument.Parse(File.ReadAllBytes(areasFile));
        string[] ids = [.. areas.RootElement.EnumerateArray().Select(r => r.TryGetProperty("Id", out var a) ? a.GetString() ?? "" : "")];
        using var presets = JsonDocument.Parse(File.ReadAllBytes(presetsFile));
        var row = presets.RootElement.EnumerateArray().FirstOrDefault(r => string.Equals(r.GetProperty("Id").GetString(), id, StringComparison.OrdinalIgnoreCase));
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("Areas", out var list)) return [];
        return [.. list.EnumerateArray().Select(a => a.GetInt32()).Where(i => i >= 0 && i < ids.Length).Select(i => ids[i])];
    }

    /// <summary>
    /// The flags /quest_preset adds for a preset (case ignored): an <c>ActNComplete…</c> preset brings the earlier acts'
    /// too (each act's first preset, e.g. Act3Complete adds Act1Complete, Act2CompleteEramir and Act3Complete): the table
    /// lists only the flags each act finishes with. With the game's own presets (<paramref name="presets"/> null) the
    /// presets' quests are completed too (<see cref="CompletionFlags"/>). Other presets (the demo starts) stand alone. Null
    /// with <paramref name="error"/> for an unknown preset.
    /// </summary>
    public static IReadOnlyList<string>? PresetFlags(string id, out string? error, IReadOnlyList<Preset>? presets = null)
    {
        bool completeQuests = presets is null;
        presets ??= LoadPresets();
        error = null;
        var preset = presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            error = presets.Count == 0 ? "No data/tables/CharacterStartQuestState.json (run scripts/dev/extract-data.ps1)."
                : $"No preset \"{id}\". Presets: {string.Join(", ", presets.Select(p => p.Id))}.";
            return null;
        }
        var used = new List<Preset>();
        if (ActOf(preset.Id) is int act)
            for (int earlier = 1; earlier < act; earlier++)
                if (presets.FirstOrDefault(p => ActOf(p.Id) == earlier) is { } before) used.Add(before);
        used.Add(preset);
        var flags = used.SelectMany(p => p.Flags).ToList();
        if (completeQuests)
            foreach (var p in used) flags.AddRange(CompletionFlags(QuestStates.Default.Value.FindPreset(p.Id)?.Quests ?? []));
        return [.. flags.Distinct()];
    }

    /// <summary>
    /// The flags that make these quests complete: each quest's QuestStates "complete" row (Order 0) FlagsPresent, and for a
    /// <c>Visited-&lt;area&gt;</c> among them that area's WorldAreas EndQuestFlags (the gate the visit needed). The presets list
    /// only the flags each act finishes with: Act3Complete has quest a3q10 (complete with Visited-4_town) but not
    /// a3q10-KilledDominus, the gate of every Act 4 area, so a preset character could not leave Act 3 without this.
    /// </summary>
    public static IReadOnlyList<string> CompletionFlags(IEnumerable<string> quests)
    {
        var states = QuestStates.Default.Value;
        var flags = new List<string>();
        foreach (string quest in quests)
            if (states.Of(quest).Where(s => s.Order == 0).OrderBy(s => s.Present.Count).FirstOrDefault() is { } done)
                foreach (string flag in done.Present)
                {
                    flags.Add(flag);
                    if (AreaGates.Value.TryGetValue(flag, out var gate)) flags.AddRange(gate);
                }
        return [.. flags.Distinct()];
    }

    // WorldAreas: an area's visit flag (StartQuestFlags) → its EndQuestFlags.
    static readonly Lazy<Dictionary<string, string[]>> AreaGates = new(AreaTable);

    static Dictionary<string, string[]> AreaTable()
    {
        var gates = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string areas = Path.Combine("data", "tables", "WorldAreas.json"), flagsFile = Path.Combine("data", "tables", "QuestFlags.json");
        if (!File.Exists(areas) || !File.Exists(flagsFile)) return gates;
        using var flags = JsonDocument.Parse(File.ReadAllBytes(flagsFile));
        string[] names = [.. flags.RootElement.EnumerateArray().Select(r => r.TryGetProperty("Id", out var id) ? id.GetString() ?? "" : "")];
        string[] Names(JsonElement area, string column) => area.TryGetProperty(column, out var list)
            ? [.. list.EnumerateArray().Select(e => e.GetInt32()).Where(i => i >= 0 && i < names.Length).Select(i => names[i])] : [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(areas));
        foreach (var area in doc.RootElement.EnumerateArray())
            foreach (string visit in Names(area, "StartQuestFlags").Where(f => f.StartsWith("Visited-", StringComparison.Ordinal)))
                gates.TryAdd(visit, Names(area, "EndQuestFlags"));
        return gates;
    }

    static int? ActOf(string presetId) =>
        System.Text.RegularExpressions.Regex.Match(presetId, @"^Act(\d+)Complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
}
