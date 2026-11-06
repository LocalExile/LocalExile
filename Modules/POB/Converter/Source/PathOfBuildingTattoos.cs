// Adapted from LocalExile main ec522f18: TattooCatalogue, JourneyMakanuiCatalogue, TattoosRules and the Tattoos
// feature's apply checks. See NOTICE.md.
using System.Text.Json;
using GameServer.Core.Imports;

/// <summary>
/// Maps PoB's passive-tree overrides (tattoos) onto the native save's tattoo map. PoB saves one override per node
/// under the tattoo's display name: the client's item name for an ordinary tattoo, or the chosen keystone's name for
/// Journey Tattoo of Makanui. The server keys its save the same way (node -> item name, plus node -> keystone
/// PassiveSkillGraphId for Makanui) and derives the client's override hash and the replacement stats from the name.
/// Only tattoos the server's own apply packet would accept are written: catalogued, with a server effect, on a
/// retained passive of the right target set, within the 50-tattoo cap and the one-per-group limits. Everything else
/// is skipped with a warning, and the override's effect text is never read as stats.
/// </summary>
static class PathOfBuildingTattoos
{
    public sealed record Result(Dictionary<ushort, string> Tattoos, Dictionary<ushort, ushort> Keystones);

    /// <summary>The server's default tattoo_total_limit and tattoo_group_limit (a server may configure others).</summary>
    public const int TotalLimit = 50, GroupLimit = 1;
    public const string Makanui = "Journey Tattoo of Makanui";

    /// <summary>
    /// The tattoos with a server effect (LocalExile TattooStats.Seed at ec522f18). The server refuses to apply any
    /// other tattoo ("has no server effect yet"), and a saved one would show on the client while the passive kept its
    /// ordinary stats, so the exporter skips them. Refresh from src/GameServer.Gateway/Item/TattooStats.cs.
    /// </summary>
    static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "Tattoo of the Ngamahu Firewalker", "Tattoo of the Arohongui Moonwarden", "Tattoo of the Valako Stormrider",
        "Tattoo of the Hinekora Storyteller", "Tattoo of the Ramako Fleetfoot", "Tattoo of the Rongokurai Warrior",
        "Tattoo of the Valako Shieldbearer", "Tattoo of the Ramako Shaman", "Tattoo of the Hinekora Warrior",
        "Honoured Tattoo of the Oak",
        "Tattoo of the Arohongui Warrior", "Tattoo of the Hinekora Deathwarden", "Tattoo of the Hinekora Shaman",
        "Tattoo of the Kitava Warrior", "Tattoo of the Ngamahu Shaman", "Tattoo of the Ngamahu Warrior",
        "Tattoo of the Ramako Archer", "Tattoo of the Ramako Scout", "Tattoo of the Ramako Sniper",
        "Tattoo of the Rongokurai Brute", "Tattoo of the Rongokurai Goliath", "Tattoo of the Rongokurai Turtle",
        "Tattoo of the Tawhoa Warrior", "Tattoo of the Tukohama Shaman", "Tattoo of the Tukohama Warmonger",
        "Tattoo of the Valako Scout", "Tattoo of the Valako Warrior",
        "Honoured Tattoo of the Dove", "Honoured Tattoo of the Makanga", "Honoured Tattoo of the Pillager",
        "Honoured Tattoo of the Sky", "Honoured Tattoo of the Tuatara", "Honoured Tattoo of the Turtle",
        "Tattoo of the Arohongui Makanga", "Tattoo of the Ngamahu Makanga", "Tattoo of the Tukohama Makanga",
        "Tattoo of the Valako Makanga",
        "Tattoo of the Hinekora Makanga", "Tattoo of the Kitava Makanga", "Tattoo of the Ramako Makanga",
        "Tattoo of the Rongokurai Makanga", "Tattoo of the Tasalio Makanga", "Tattoo of the Tawhoa Makanga",
        "Journey Tattoo of the Body", "Journey Tattoo of the Mind", "Journey Tattoo of the Soul",
        Makanui, "Ancestral Tattoo of Bloodlines",
    };

    /// <summary>A tattoo item: its target set as the client names it and its PassiveSkillOverrides.Limit group.</summary>
    sealed record Entry(string Name, string Target, int Limit, bool Forbidden);

    /// <summary>Tattoos by item name, Makanui's keystone pool by keystone name, and Stats.json ids by row.</summary>
    sealed record Catalogue(Dictionary<string, Entry> Entries, Dictionary<string, ushort> Keystones, string[] StatIds);

    /// <summary>
    /// The tattoos to save for the retained ordinary passives. <paramref name="passiveRows"/> is the extracted
    /// PassiveSkills table (its row order is what PassiveSkillOverrides.AllocatedPassiveSkill indexes). Overrides are
    /// processed in node order, so the cap and the group limits keep the lowest nodes.
    /// </summary>
    public static Result Prepare(IReadOnlyList<PathOfBuildingOverride> overrides, IReadOnlyCollection<ushort> retained,
        PassiveTree tree, JsonElement passiveRows, string folder, List<string> warnings)
    {
        var result = new Result([], []);
        if (overrides.Count == 0) return result;
        if (Load(folder, passiveRows) is not { } catalogue)
        {
            warnings.Add($"Skipped {overrides.Count} passive-tree override(s): the data folder has no tattoo tables (Stats, BaseItemTypes, " +
                "PassiveSkillTattoos, PassiveSkillTattooTargetSets, PassiveSkillOverrides); extract the game data again.");
            return result;
        }
        var kept = new HashSet<ushort>(retained);
        var rows = new Dictionary<uint, JsonElement>();
        foreach (var row in passiveRows.EnumerateArray())
            if (row.TryGetProperty("PassiveSkillGraphId", out var id) && id.TryGetUInt32(out uint graphId)) rows.TryAdd(graphId, row);
        foreach (var over in overrides.OrderBy(o => o.NodeId))
        {
            string label = over.Name.Length > 0 ? $"'{over.Name}'" : "an unnamed override";
            if (over.Name.StartsWith("Runegraft of ", StringComparison.Ordinal))
            {
                warnings.Add($"Skipped Runegraft {label} on passive {over.NodeId}: Runegrafts have no native mapping; the mastery keeps its selected effect.");
                continue;
            }
            if (over.NodeId > ushort.MaxValue || !kept.Contains((ushort)over.NodeId) || tree.Find((ushort)over.NodeId) is not { } passive
                || !rows.TryGetValue(over.NodeId, out var raw))
            {
                warnings.Add($"Skipped tattoo {label} on passive {over.NodeId}: the passive was not retained.");
                continue;
            }
            ushort node = (ushort)over.NodeId;
            string where = $"on passive {node} ({passive.Name})";
            ushort? keystone = null;
            var entry = catalogue.Entries.GetValueOrDefault(over.Name);
            if (entry is null && catalogue.Keystones.TryGetValue(over.Name, out ushort chosen))
            {
                // PoB stores Journey Tattoo of Makanui as the keystone it granted.
                entry = catalogue.Entries.GetValueOrDefault(Makanui);
                keystone = chosen;
            }
            if (entry is null)
            {
                warnings.Add(over.Name.Length > 0
                    ? $"Skipped unknown passive-tree override {label} {where}: not a 3.29 tattoo."
                    : $"Skipped an unnamed passive-tree override {where}: not a 3.29 tattoo.");
                continue;
            }
            string what = keystone is null ? $"tattoo '{entry.Name}'" : $"tattoo '{entry.Name}' ({over.Name})";
            // The same refusals as the server's apply packet, in its order; then its cap and group limits.
            string? refusal =
                entry.Forbidden ? "event-only Forbidden tattoos are not supported by the server"
                : entry.Name.StartsWith("Loyalty ", StringComparison.Ordinal) ? "Loyalty tattoos are not supported by the server yet"
                : entry.Name.StartsWith("Honoured Tattoo of the ", StringComparison.Ordinal) && entry.Target.StartsWith("Notable", StringComparison.Ordinal)
                    ? "Honoured attribute notables are not supported by the server yet"
                : !Supported.Contains(entry.Name) ? "the server has no effect for it yet; the passive keeps its ordinary effect"
                : !TargetMatches(passive, raw, entry.Target, catalogue.StatIds) ? $"it replaces {entry.Target} passives, not this one"
                : keystone == node ? "the chosen keystone is the tattooed keystone itself"
                : result.Tattoos.Count >= TotalLimit ? $"a character can have at most {TotalLimit} tattoos"
                : LimitName(entry.Limit) is { } group
                    && result.Tattoos.Values.Count(name => catalogue.Entries.GetValueOrDefault(name)?.Limit == entry.Limit) >= GroupLimit
                    ? $"only {(GroupLimit == 1 ? "one" : GroupLimit.ToString())} {group} can be applied"
                : null;
            if (refusal is not null)
            {
                warnings.Add($"Skipped {what} {where}: {refusal}.");
                continue;
            }
            result.Tattoos[node] = entry.Name;
            if (keystone is ushort graphId) result.Keystones[node] = graphId;
        }
        return result;
    }

    /// <summary>The server's target-set rule: a small attribute passive is one that is nothing else.</summary>
    static bool TargetMatches(PassiveTree.Node node, JsonElement raw, string target, string[] statIds)
    {
        bool Has(string stat, int value) => node.Stats.Any(s => s.Key >= 1 && s.Key <= statIds.Length && statIds[s.Key - 1] == stat && s.Value == value);
        bool notable = Flag(raw, "IsNotable"), keystone = Flag(raw, "IsKeystone"), mastery = node.MasteryGroup >= 0;
        bool small = !notable && !keystone && !node.IsAscendancy && !node.IsStart && !Flag(raw, "IsJewelSocket")
            && !mastery && !Flag(raw, "IsProxyPassive") && !Flag(raw, "IsExpansion");
        return target switch
        {
            "Small Strength" => small && Has("base_strength", 10),
            "Small Dexterity" => small && Has("base_dexterity", 10),
            "Small Intelligence" => small && Has("base_intelligence", 10),
            "Small Attribute" => small && (Has("base_strength", 10) || Has("base_dexterity", 10) || Has("base_intelligence", 10)),
            "Notable (+30 to Strength)" => notable && Has("base_strength", 30),
            "Notable (+30 to Dexterity)" => notable && Has("base_dexterity", 30),
            "Notable (+30 to Intelligence)" => notable && Has("base_intelligence", 30),
            "Keystone" => keystone,
            "Mastery" => mastery,
            "Ascendancy Notable" => node.IsAscendancy,
            _ => false,
        };
    }

    /// <summary>The client's limit group for a PassiveSkillOverrides.Limit id; null when only the 50 cap applies.
    /// The Limit 7-39 ids are Runegrafts, which never reach here.</summary>
    static string? LimitName(int limit) => limit switch
    {
        0 => "Loyalty Tattoo",
        4 => "Attribute Notable Tattoo",
        5 => "Ancestral Tattoo",
        6 => "Keystone Tattoo",
        40 => "Ascendancy Tattoo",
        _ => null,
    };

    static bool Flag(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>The server's catalogue: BaseItemTypes rows named "Tattoo", joined to PassiveSkillTattoos for the
    /// target set and to PassiveSkillOverrides for the limit group; the Type 15 override rows are Makanui's keystone
    /// pool. Null when the data folder predates the tattoo tables.</summary>
    static Catalogue? Load(string folder, JsonElement passiveRows)
    {
        string Table(string name) => Path.Combine(folder, "tables", name + ".json");
        if (new[] { "Stats", "BaseItemTypes", "PassiveSkillTattoos", "PassiveSkillTattooTargetSets", "PassiveSkillOverrides" }
            .Any(table => !File.Exists(Table(table)))) return null;
        string[] statIds;
        using (var stats = JsonDocument.Parse(File.ReadAllBytes(Table("Stats"))))
            statIds = [.. stats.RootElement.EnumerateArray().Select(s => s.GetProperty("Id").GetString() ?? "")];

        var limits = new List<int>();
        var keystones = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        int passiveCount = passiveRows.GetArrayLength();
        using (var overrides = JsonDocument.Parse(File.ReadAllBytes(Table("PassiveSkillOverrides"))))
            foreach (var row in overrides.RootElement.EnumerateArray())
            {
                limits.Add(row.TryGetProperty("Limit", out var limit) && limit.ValueKind == JsonValueKind.Number ? limit.GetInt32() : -1);
                if (row.TryGetProperty("Type", out var type) && type.ValueKind == JsonValueKind.Number && type.GetInt32() == 15
                    && row.TryGetProperty("AllocatedPassiveSkill", out var allocated) && allocated.ValueKind == JsonValueKind.Number
                    && allocated.GetInt32() is >= 0 and var passiveRow && passiveRow < passiveCount)
                {
                    var passive = passiveRows[passiveRow];
                    if (Flag(passive, "IsKeystone") && passive.TryGetProperty("PassiveSkillGraphId", out var id)
                        && id.TryGetUInt32(out uint graphId) && graphId is > 0 and <= ushort.MaxValue
                        && passive.TryGetProperty("Name", out var name) && name.GetString() is { Length: > 0 } keystoneName)
                        keystones.TryAdd(keystoneName, (ushort)graphId);
                }
            }

        var sets = new List<string>();
        using (var targetSets = JsonDocument.Parse(File.ReadAllBytes(Table("PassiveSkillTattooTargetSets"))))
            foreach (var row in targetSets.RootElement.EnumerateArray())
            {
                string name = row.GetProperty("Name").GetString() ?? "", qualifier = row.GetProperty("Qualifier").GetString() ?? "";
                sets.Add(qualifier.Length > 0 ? $"{name} ({qualifier})" : name);
            }

        var targets = new Dictionary<int, (int Set, int Override)>();
        using (var tattoos = JsonDocument.Parse(File.ReadAllBytes(Table("PassiveSkillTattoos"))))
            foreach (var row in tattoos.RootElement.EnumerateArray())
                targets[row.GetProperty("Tattoo").GetInt32()] = (row.GetProperty("Set").GetInt32(), row.GetProperty("Override").GetInt32());

        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        using (var bases = JsonDocument.Parse(File.ReadAllBytes(Table("BaseItemTypes"))))
        {
            int index = 0;
            foreach (var row in bases.RootElement.EnumerateArray())
            {
                string name = row.GetProperty("Name").GetString() ?? "";
                if (name.Contains("Tattoo", StringComparison.Ordinal) && targets.TryGetValue(index, out var target))
                    entries.TryAdd(name, new Entry(name,
                        target.Set >= 0 && target.Set < sets.Count ? sets[target.Set] : "unknown",
                        target.Override >= 0 && target.Override < limits.Count ? limits[target.Override] : -1,
                        name.StartsWith("Forbidden Tattoo", StringComparison.Ordinal)));
                index++;
            }
        }
        return new Catalogue(entries, keystones, statIds);
    }
}
