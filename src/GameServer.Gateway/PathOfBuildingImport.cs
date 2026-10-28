using GameServer.Core.Imports;

/// <summary>The shared preparation path for offline and HTTP imports. No database or gameplay locks are held here.</summary>
static class PathOfBuildingImport
{
    public sealed record Prepared(PathOfBuildingBuild Build, PathOfBuildingCharacters.Progress Progress,
        PathOfBuildingItems.Result Equipment, Summary Summary)
    {
        public object Response(bool preview, string? name, string league) => new
        {
            ok = true, preview, character = name, @class = Build.Class.ToString(), level = Build.Level, league,
            passives = Progress.Passives.Count, masteries = Progress.MasteryEffects.Count,
            items = Summary.Items, activeItems = Summary.ActiveItems, inactiveWeaponItems = Summary.InactiveWeaponItems,
            skippedItems = Summary.SkippedItems, gems = Summary.Gems, activeGems = Summary.ActiveGems,
            inactiveWeaponGems = Summary.InactiveWeaponGems, skippedGems = Summary.SkippedGems,
            partial = Summary.Partial, warnings = Summary.Warnings, warningCount = Summary.WarningCount,
            warningsTruncated = Summary.WarningsTruncated,
            next = preview ? "Review the counts and warnings. Forge Character saves a new character; preview does not reserve a name or slot."
                : "Log out to the login screen and log in again, then select this character and Play."
        };
    }

    public sealed record Summary(int Items, int ActiveItems, int InactiveWeaponItems, int SkippedItems,
        int Gems, int ActiveGems, int InactiveWeaponGems, int SkippedGems, bool Partial,
        string[] Warnings, int WarningCount, bool WarningsTruncated);

    public static Prepared Prepare(string code, PathOfBuildingServerConfig config,
        Func<IReadOnlyDictionary<uint, uint[]>>? graphProvider = null)
    {
        var build = PathOfBuilding.Parse(code);
        var progress = PathOfBuildingCharacters.Prepare(build, config.DataFolder, graphProvider ??
            (() => PathOfBuildingPassiveGraph.Load(config.ClientGgpk, config.OozWorker, config.NodeExecutable)));
        var equipment = PathOfBuildingItems.Materialize(build, new ItemCatalog(config.DataFolder), config.DataFolder);
        return new(build, progress, equipment, Describe(build, progress, equipment));
    }

    internal static Summary Describe(PathOfBuildingBuild build, PathOfBuildingCharacters.Progress progress,
        PathOfBuildingItems.Result equipment)
    {
        int items = 0, inactive = 0, gems = 0, inactiveGems = 0;
        foreach (var (row, placements) in equipment.Inventories)
        {
            int count = placements.Sum(p => p.Item.Sockets?.Count(s => s.Gem is not null) ?? 0);
            items += placements.Count;
            gems += count;
            // Materialize normalizes the selected weapon set into rows 2/3; rows 14/15 are always inactive.
            if (row is 14 or 15) { inactive += placements.Count; inactiveGems += count; }
        }
        // Surface missing equipment/skills before per-modifier detail so a large build cannot bury lost gear.
        var all = progress.Warnings.Concat(equipment.Warnings).Distinct()
            .OrderBy(w => w.StartsWith("Skipped item ", StringComparison.Ordinal) || w.StartsWith("Skipped skill group", StringComparison.Ordinal)
                || w.StartsWith("Skipped gem ", StringComparison.Ordinal) ? 0 : w.StartsWith("Skipped", StringComparison.Ordinal) ? 1 : 2)
            .ToArray();
        var shown = new List<string>();
        int budget = 6000;
        bool truncated = false;
        foreach (string warning in all)
        {
            // This also bounds default JSON escaping (6 bytes/code unit). The response stays below evryBuild's
            // 128 KiB transport limit and its 32768-character result control, including non-ASCII build text.
            if (shown.Count == 40 || budget == 0) { truncated = true; break; }
            int length = Math.Min(warning.Length, Math.Min(256, budget));
            string text = warning[..length];
            if (length < warning.Length) { text += "…"; truncated = true; }
            shown.Add(text);
            budget -= length;
        }
        return new(items, items - inactive, inactive, Math.Max(0, build.Slots.Count(s => s.ItemId != 0) - items),
            gems, gems - inactiveGems, inactiveGems,
            Math.Max(0, build.Skills.Where(g => g.Enabled).Sum(g => g.Gems.Count(x => x.Enabled)) - gems),
            progress.Warnings.Any(w => w.StartsWith("Skipped", StringComparison.Ordinal)) || equipment.Warnings.Count > 0,
            shown.ToArray(), all.Length, truncated);
    }
}
