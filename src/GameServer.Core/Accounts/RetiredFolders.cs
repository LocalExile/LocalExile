namespace GameServer.Core.Accounts;

/// <summary>
/// The folders a release no longer ships but an install updated by unzipping the new release over the old one still has:
/// left there, their old modules load (before the new ones of the same id) and the mods that replace them refuse to (a
/// module of that id). LocalExile.exe moves them aside first thing (run, setup). A git checkout needs none of this:
/// git removes them.
/// </summary>
public static class RetiredFolders
{
    /// <summary>Every module or league folder a release shipped (v0.1.0-beta to v0.6.0-beta) that LocalExile has no more: what
    /// became mods in LocalExile-Mods, in its 0.4-0.6 place and its earlier one, and the old places of Ruthless and Mercy.</summary>
    public static readonly string[] Paths =
    [
        "Leagues/Phrecia", "Leagues/Survivors",
        "Modules/Legacy/Idols", "Modules/Legacy/PhreciaAscendancies", "Modules/Legacy/Content/HarvestHeartOfTheGrove",
        "Modules/Custom/NessaDevelopmentTabs", "Modules/Custom/PlayerBots", "Modules/Custom/Survivors",
        "Modules/NessaDevelopmentTabs", "Modules/PhreciaAscendancies", "Modules/PlayerBots", "Modules/Survivors",
        "Modules/Ruthless", "Modules/Core/Content/Mercy",
    ];

    /// <summary>Where the PlayerBots module kept the bots' state (0.4-0.6, then 0.2-0.3).</summary>
    static readonly string[] BotStates = ["Modules/Custom/PlayerBots/state/bots.json", "Modules/PlayerBots/state/bots.json"];

    /// <summary>Moves each of <see cref="Paths"/> under <paramref name="root"/> to <c>backups/retired-&lt;stamp&gt;/</c>, the
    /// bots' state first to where the playerbots mod reads it. A folder that cannot be moved stays, said.</summary>
    public static void MoveAside(string root, DateTime now, Action<string> info, Action<string> warn)
    {
        // The playerbots mod takes its state from data/mods once; with none it deletes every bot account. While an old module
        // of that id is there the mod cannot run (ModHost refuses it), so the old state is the newest there is.
        var states = BotStates.Select(p => Path.Combine(root, p)).Where(File.Exists).ToList();
        string kept = Path.Combine(root, "data", "mods", "playerbots", "bots.json");
        bool live = states.Any(s => File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(s)!)!, "module.json")));
        if (states.Count > 0 && (live || !File.Exists(kept)))
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                File.Copy(states.MaxBy(File.GetLastWriteTimeUtc)!, kept, overwrite: true);
                info($"the player bots' state is now {Path.GetRelativePath(root, kept)}, for the playerbots mod");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warn($"the player bots' state could not be copied to {Path.GetRelativePath(root, kept)} ({e.Message}): the old modules stay where they are");
                return;
            }
        string backup = Path.Combine(root, "backups", $"retired-{now:yyyyMMdd-HHmmss}");
        foreach (string path in Paths)
        {
            string folder = Path.Combine(root, path), target = Path.Combine(backup, path);
            if (!Directory.Exists(folder)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(folder, target);
                info($"moved {Path.GetRelativePath(root, folder)} to {Path.GetRelativePath(root, target)}: LocalExile no longer has it");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warn($"{Path.GetRelativePath(root, folder)} is no longer part of LocalExile and could not be moved aside ({e.Message}): delete it, or its old modules load instead of the new ones");
            }
        }
        // Their emptied parents too; one that stays is harmless.
        foreach (string parent in (string[])["Modules/Legacy/Content", "Modules/Legacy", "Modules/Custom"])
            try
            {
                if (Path.Combine(root, parent) is var empty && Directory.Exists(empty) && !Directory.EnumerateFileSystemEntries(empty).Any())
                    Directory.Delete(empty);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
