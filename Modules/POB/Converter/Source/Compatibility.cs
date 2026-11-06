using System.Diagnostics;
using System.Text.Json;
using GameServer.Core.Imports;

static class Compatibility
{
    internal sealed record Finding(string Source, string Effect, string Status, string Reason, string Action = "kept");
    internal sealed record Report(List<Finding> Findings, List<string> Warnings);
    static JsonDocument Profile()
    {
        using var stream = typeof(Compatibility).Assembly.GetManifestResourceStream("LocalExile.Export.compatibility.json")
            ?? typeof(Compatibility).Assembly.GetManifestResourceStream("evryBuild.Export.compatibility.json")!;
        return JsonDocument.Parse(stream);
    }
    public static string Identify(string? data)
    {
        if (data is null) return "unknown";
        string root = Path.GetDirectoryName(data)!;
        foreach (string relative in new[] { "src/GameServer.Gateway/bin/Debug/net10.0/GameServer.Gateway.dll", "src/GameServer.Gateway/bin/Release/net10.0/GameServer.Gateway.dll", "GameServer.Gateway.dll" })
        {
            string path = Path.Combine(root, relative);
            if (File.Exists(path)) return FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "unknown";
        }
        return "unknown";
    }
    public static Report Check(PathOfBuildingBuild build, PathOfBuildingCharacters.Progress progress,
        PathOfBuildingItems.Result equipment, ItemCatalog catalog, string data, string version)
    {
        using var profile = Profile(); var root = profile.RootElement;
        bool recognised = version.EndsWith("+" + root.GetProperty("upstreamCommit").GetString(), StringComparison.Ordinal);
        var findings = new List<Finding>();
        void Stat(string source, string stat)
        {
            bool known = root.GetProperty("stats").TryGetProperty(stat, out var rule);
            string status = known ? rule.ValueKind == JsonValueKind.String ? rule.GetString()! : rule.GetProperty("status").GetString()! : "unknown";
            string detail = known && rule.ValueKind == JsonValueKind.Object ? rule.GetProperty("reason").GetString()!
                : "Covered by the bundled reference profile; locally modified builds may differ.";
            findings.Add(new(source, stat, recognised ? status : "unknown", status == "unknown"
                ? "No verified player-mechanic rule exists for this effect. Its presence in game data does not prove implementation."
                : recognised ? detail
                : $"Installed build is unverified. Reference profile {root.GetProperty("upstreamCommit").GetString()![..8]}: {status}. {detail}"));
        }
        using var effects = File.Exists(Path.Combine(data, "tables/GrantedEffects.json"))
            ? JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables/GrantedEffects.json"))) : null;
        foreach (var (row, placements) in equipment.Inventories)
            foreach (var placement in placements)
            {
                var item = placement.Item;
                var basis = catalog.All.FirstOrDefault(t => t.Hash == item.Type);
                string slot = row switch { 1 => "Body Armour", 2 => "Weapon 1", 3 => "Weapon 2", 4 => "Helmet", 5 => "Amulet",
                    6 => "Ring 1", 7 => "Ring 2", 8 => "Gloves", 9 => "Boots", 10 => "Belt", 11 => $"Flask {placement.X + 1}",
                    14 => "Weapon 1 Swap", 15 => "Weapon 2 Swap", _ => $"Inventory {row}" };
                string label = $"{slot}: {basis?.Name ?? "item"}";
                foreach (var mod in item.Mods ?? [])
                    if (catalog.Mods.ById(mod.Id) is { } native)
                        foreach (var stat in native.Stats) Stat($"{label} ({mod.Id})", stat.Id);
                foreach (var socket in item.Sockets ?? [])
                    if (socket.Gem is { } gem && catalog.All.FirstOrDefault(t => t.Hash == gem.Type) is { } type && catalog.Gem(type) is { } g)
                    {
                        var variant = g.Variant(gem.GemVariant);
                        foreach (int effect in new[] { variant.Effect, variant.Effect2 }.Where(e => e >= 0).Distinct())
                        {
                            string id = effects?.RootElement[effect].GetProperty("Id").GetString()
                                ?? throw new ArgumentException("GrantedEffects.json is required for gem compatibility checks.");
                            bool known = root.GetProperty("skills").TryGetProperty(id, out var skill);
                            findings.Add(new($"gem: {variant.Name}", id, known && recognised ? skill.GetProperty("status").GetString()! : "unknown",
                                known ? (recognised ? "" : "Reference profile only; installed build is unverified. ") + skill.GetProperty("reason").GetString()
                                : "Native gem is loadable. Its complete gameplay behaviour has not been verified by this compatibility profile."));
                        }
                    }
            }
        using var stats = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables/Stats.json")));
        var names = stats.RootElement.EnumerateArray().Select(s => s.GetProperty("Id").GetString()!).ToArray();
        var tree = new PassiveTree(data);
        foreach (ushort id in progress.Passives)
            if (tree.Find(id) is { } node)
                foreach (var (key, _) in node.Stats) if (key > 0 && key <= names.Length) Stat($"passive {id}: {node.Name}", names[key - 1]);
        foreach (var (node, effect) in progress.MasteryEffects)
            if (tree.FindEffect(effect) is { } mastery)
                foreach (var (key, _) in mastery.Stats) if (key > 0 && key <= names.Length) Stat($"mastery {node}/{effect}", names[key - 1]);
        var warnings = new List<string> { $"Compatibility uses an offline reference profile. Detected server: {version}. Known, partial and unknown effects are retained when their native save encoding is valid.",
            "The database does not advertise active leagues. Confirm that the entered league is offered by the running server." };
        findings = findings.Distinct().OrderBy(f => f.Status switch { "not implemented" => 0, "partial" => 1, "unknown" => 2, _ => 3 }).ToList();
        int partial = findings.Count(f => f.Status == "partial"), missing = findings.Count(f => f.Status == "not implemented"), unknown = findings.Count(f => f.Status == "unknown");
        warnings.Add($"Gameplay checks: {partial} partial, {missing} unimplemented and {unknown} unknown effects. None were removed solely for missing gameplay support.");
        return new(findings, warnings);
    }
}
