using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using System.Security.Cryptography;

namespace LocalExile;

/// <summary>Client\ in the install: a copy of the player's client, or a junction to it. The tools and servers all use that
/// path, as they do in a checkout. Game.exe and Game.patched.exe are added beside the client's own files.</summary>
static class ClientInstall
{
    public const string RequiredVersion = "3.29.3c";
    const int MaxExecutableBytes = 128 << 20;
    const string Folder = "Client";
    static string Ggpk => Path.Combine(Folder, "Content.ggpk");
    static string GameExe => Path.Combine(Folder, "Game.exe");
    static string PatchedExe => Path.Combine(Folder, "Game.patched.exe");
    static readonly string Keys = Path.Combine("data", "login-keys.json");
    sealed record CheckedExecutable(string Path, string Sha256);

    /// <summary>The folder's 3.29.3c executable, or why it has none.</summary>
    static CheckedExecutable? FindExecutable(string folder, out string problem, bool reportReadErrors = false)
    {
        problem = "";
        if (!File.Exists(Path.Combine(folder, "Content.ggpk")))
        {
            problem = $"{folder} has no Content.ggpk. LocalExile needs the standalone client layout; Steam installs do not work";
            return null;
        }
        // A checkout's PathOfExile.exe can be the small launcher, with the game in Game.exe. The check reads the executable.
        Exception? readError = null;
        string? oversized = null;
        foreach (string name in new[] { "PathOfExile.exe", "Game.exe" })
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            try
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > MaxExecutableBytes) { oversized = path; continue; }
                byte[] image = new byte[(int)input.Length];
                input.ReadExactly(image);
                _ = new ClientImage(image);
                HncSender.LoadMask(image);
                return new(path, Convert.ToHexStringLower(SHA256.HashData(image)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { readError = e; }
            catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException or InvalidOperationException) { }
        }
        if (reportReadErrors && readError is not null)
            throw new LauncherException(ExitCode.Client, "The client executable could not be read. Close its updater and check access to this folder.",
                readError is UnauthorizedAccessException ? "access-denied" : "client-unreadable", $"{folder}: {readError.Message}");
        if (reportReadErrors && oversized is not null)
            throw new LauncherException(ExitCode.Unexpected,
                "A client executable exceeds the validator's 128 MiB limit. Keep this copy and check or update the LocalExile package.",
                "validation-limit", oversized);
        problem = $"{folder} does not hold the Path of Exile 3.29.3c executable; other versions do not work";
        return null;
    }

    static CheckedExecutable RequireExecutable(string folder)
    {
        var exe = FindExecutable(folder, out string problem, reportReadErrors: true);
        if (exe is null)
            throw new LauncherException(ExitCode.Client, problem +
                ". Keep this copy; opening the official PoE launcher may update it to an unsupported version.",
                File.Exists(Path.Combine(folder, "Content.ggpk")) ? "unsupported-executable" : "missing-client-file");
        return exe;
    }

    static ClientReadLease AcquireCheckedClient(string folder, out string executable)
    {
        var image = RequireExecutable(folder);
        var lease = ClientReadLease.Open(folder, image.Path);
        try
        {
            lease.AssertExecutable(image.Sha256);
            executable = image.Path;
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public static async Task CheckAsync(string folder, Output log, CancellationToken stop = default)
    {
        using var lease = AcquireCheckedClient(folder, out string exe);
        var result = await ClientArchiveCheck.RunAsync(folder, stop);
        lease.AssertUnchanged();
        log.Value("client", $"{exe} is the 3.29.3c client");
        log.Value("client-data", $"Index and sampled required tables are readable ({result.Files} files, {result.Bundles} bundles); this is not a full asset verification.");
        log.Value("client-index-sha256", result.IndexSha256);
    }

    /// <summary>Client\ holds the checked client when this returns.</summary>
    public static async Task EnsureAsync(Options options, Output log)
    {
        log.Step("client-check", "checking the client ...");
        if (Directory.Exists(Folder))
        {
            try
            {
                using var existing = AcquireCheckedClient(Folder, out string exe);
                await ClientArchiveCheck.RunAsync(Folder);
                existing.AssertUnchanged();
                EnsureGameExe(exe);
                log.Info($"using {Path.GetFullPath(Folder)}");
            }
            catch (LauncherException e)
            {
                string context = e.Code == ExitCode.Client
                    ? $"The existing Client folder failed its readiness check. {e.Message} It was kept. To try another game copy, set it up in a different LocalExile installation folder."
                    : $"The existing Client folder was kept. {e.Message}";
                throw new LauncherException(e.Code, context,
                    e.Reason, e.Details);
            }
            return;
        }
        if (File.Exists(Folder) || new DirectoryInfo(Folder).LinkTarget is not null)
            throw new LauncherException(ExitCode.Client, "The existing Client path cannot be used and was kept. Use another LocalExile installation folder to select a different client.",
                "client-unreadable");

        string source = options.Client ?? Ask(log);
        using var original = AcquireCheckedClient(source, out string found);
        var ready = await ClientArchiveCheck.RunAsync(source);
        original.AssertUnchanged();
        log.Info($"{found} is the 3.29.3c client");

        string mode = options.ClientMode ?? AskMode(source, log);
        if (mode == "link") Link(source, log);
        else await CopyAsync(source, log, original, ready);
        EnsureGameExe(Path.Combine(Folder, Path.GetFileName(found)));
    }

    static string Ask(Output log)
    {
        string? guess = Guesses().FirstOrDefault(g => FindExecutable(g, out _) is not null);
        if (log.Json) throw log.NeedsInput("--client", "which folder holds the Path of Exile 3.29.3c client?", guess);
        if (guess is not null)
        {
            Console.Write($"Found the 3.29.3c client in {guess}. Use it? [Y/n] ");
            if ((Console.ReadLine() ?? "").Trim().ToLowerInvariant() is "" or "y" or "yes") return guess;
        }
        Console.Write("Folder of your Path of Exile 3.29.3c client: ");
        string answer = (Console.ReadLine() ?? "").Trim().Trim('"');
        if (answer.Length == 0) throw new LauncherException(ExitCode.NeedsInput, "no client folder given");
        return answer;
    }

    static IEnumerable<string> Guesses()
    {
        if (!OperatingSystem.IsWindows())
        {
            string games = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games");
            if (!Directory.Exists(games)) yield break;
            foreach (string prefix in Directory.EnumerateDirectories(games))
            {
                yield return Path.Combine(prefix, "drive_c", "Path of Exile");
                yield return Path.Combine(prefix, "drive_c", "Program Files (x86)", "Grinding Gear Games", "Path of Exile");
            }
            yield break;
        }
        const string Ggg = @"Grinding Gear Games\Path of Exile";
        foreach (var special in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            yield return Path.Combine(Environment.GetFolderPath(special), Ggg);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            yield return Path.Combine(drive.Name, Ggg);
            yield return Path.Combine(drive.Name, "Games", "Path of Exile");
            yield return Path.Combine(drive.Name, "Path of Exile");
        }
    }

    static string AskMode(string source, Output log)
    {
        if (log.Json) throw log.NeedsInput("--client-mode", "copy the client (safer, needs the space) or link to it (changes its data)?", "copy");
        long needed = Size(source);
        long free = new DriveInfo(Path.GetFullPath(".")).AvailableFreeSpace;
        Console.WriteLine($"LocalExile changes the client's login list and adds two files beside it. A failed write could damage the client,");
        Console.WriteLine($"and a newer launcher cannot bring 3.29.3c back, so a separate copy is safer. It needs {needed >> 30} GB; {free >> 30} GB are free here.");
        Console.Write("[C]opy the client, or [L]ink to it and use that folder itself? [C/l] ");
        return (Console.ReadLine() ?? "").Trim().ToLowerInvariant() is "l" or "link" ? "link" : "copy";
    }

    static long Size(string folder) => new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    static void Link(string source, Output log)
    {
        log.Step("client-link", $"linking Client to {source} ...");
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.GetFullPath(Folder), Path.GetFullPath(source));
            return;
        }
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", Path.GetFullPath(Folder), Path.GetFullPath(source) }) start.ArgumentList.Add(argument);
        using var mklink = System.Diagnostics.Process.Start(start)!;
        mklink.WaitForExit();
        if (mklink.ExitCode != 0) throw new LauncherException(ExitCode.Client, $"could not link Client: {mklink.StandardError.ReadToEnd().Trim()}");
    }

    /// <summary>Into a temporary folder that becomes Client\ only when complete, so a stopped copy is resumed and never used.</summary>
    static async Task CopyAsync(string source, Output log, ClientReadLease original, ClientArchiveCheck ready)
    {
        string temp = Folder + ".partial";
        ClientCopyPaths.CheckRoots(source, temp);
        var files = new DirectoryInfo(source).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        long total = files.Sum(f => f.Length), done = 0;
        long remaining = files.Where(file => ClientCopyPaths.NeedsCopy(file,
            new FileInfo(Path.Combine(temp, Path.GetRelativePath(source, file.FullName))))).Sum(file => file.Length);
        long free = new DriveInfo(Path.GetFullPath(".")).AvailableFreeSpace;
        if (free < remaining) throw new LauncherException(ExitCode.Client, $"copying the remaining client files needs {remaining >> 30} GB and {free >> 30} GB are free; free some space, or use --client-mode link");
        log.Step("client-copy", $"copying the client ({total >> 30} GB); this can take several minutes ...");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var report = TimeSpan.FromSeconds(10);
        byte[] buffer = new byte[4 << 20];
        foreach (var file in files)
        {
            string target = Path.Combine(temp, Path.GetRelativePath(source, file.FullName));
            ClientCopyPaths.CheckTarget(temp, target);
            var existing = new FileInfo(target);
            if (!ClientCopyPaths.NeedsCopy(file, existing)) { done += file.Length; continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Remove only this staging entry, then create a new file: never truncate a late hard-link/symlink target.
            if (existing.Exists) File.Delete(target);
            using (var from = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            using (var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                int read;
                while ((read = from.Read(buffer)) > 0)
                {
                    to.Write(buffer, 0, read);
                    done += read;
                    if (clock.Elapsed < report) continue;
                    report = clock.Elapsed + TimeSpan.FromSeconds(10);
                    log.Progress("client-copy", $"copied {done >> 30} of {total >> 30} GB ({(total == 0 ? 100 : done * 100 / total)}%)", done, total);
                }
            }
            File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
        }
        log.Step("client-check", "checking the unfinished client copy before using it ...");
        try
        {
            ClientCopyPaths.CheckTree(temp);
            // Bind the candidate to the exact source executable that EnsureGameExe will use after promotion.
            string copiedExe = Path.Combine(temp, original.ExecutableName);
            using var candidate = ClientReadLease.Open(temp, copiedExe);
            var copied = await ClientArchiveCheck.RunAsync(temp);
            original.AssertUnchanged();
            candidate.AssertUnchanged();
            if (copied.IndexSha256 != ready.IndexSha256 || candidate.ExecutableSha256 != original.ExecutableSha256)
                throw new LauncherException(ExitCode.Client, "The copied index or executable differs from the checked source. The unfinished copy was kept.",
                    "client-changed");
        }
        catch (LauncherException e)
        {
            throw new LauncherException(e.Code, $"{e.Message} The unfinished copy remains in Client.partial and will not be used.",
                e.Reason, e.Details);
        }
        Directory.Move(temp, Folder);
    }

    static void EnsureGameExe(string exe)
    {
        if (Path.GetFileName(exe).Equals("Game.exe", StringComparison.OrdinalIgnoreCase)) return;
        File.Copy(exe, GameExe, overwrite: true);
    }

    public static void EnsureKeysAndPatch(Output log)
    {
        log.Step("keys", "checking your login keys ...");
        if (!File.Exists(Keys))
        {
            log.Info("generating your own login keys (kept in data\\; never share them)");
            Tools.RunOrThrow(Tools.Auth, ["keygen", GameExe], "auth-keygen", ExitCode.Client, log);
        }
        // Patched from these keys and this Game.exe: a client updated in place (a linked install) has a newer Game.exe, and
        // the patch keeps the size, so an older or differently sized Game.patched.exe is another build's.
        var patched = new FileInfo(PatchedExe);
        var game = new FileInfo(GameExe);
        if (patched.Exists && patched.Length == game.Length && patched.LastWriteTimeUtc >= game.LastWriteTimeUtc
            && patched.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(Keys)) return;
        log.Info("writing Client\\Game.patched.exe, the copy of the client that trusts your keys (antivirus may flag a modified executable)");
        Tools.RunOrThrow(Tools.Auth, ["patch", GameExe, PatchedExe], "auth-patch", ExitCode.Client, log);
        if (!File.Exists(PatchedExe)) throw new LauncherException(ExitCode.Client, "Client\\Game.patched.exe is missing after patching: allow it in your antivirus");
    }

    /// <summary>The client's own Log In goes to the login server in the GGPK's gateway list: written only when it does not
    /// already name this one, and the original is kept in data\gateway_list.original.txt.</summary>
    public static void SelectLocalLogin(Ports ports, Output log)
    {
        if (ports.Patch != Ports.DefaultPatch) return;
        log.Step("login-list", "checking the client's login list ...");
        var (code, lines) = Tools.Capture(Tools.Patch, ["gateway", Ggpk]);
        var list = lines.Where(l => l.Split(';').Length >= 3).ToList();
        if (code == 0 && list.Count > 0 && list.All(l => l.Split(';')[2] == $"127.0.0.1:{ports.Login}")) return;
        // Windows' file lock refuses this write while a client runs; elsewhere that has to be asked.
        if (!OperatingSystem.IsWindows()) ClientLaunch.AssertNoneRunning();
        log.Info("pointing the client's login list at your local server");
        Tools.RunOrThrow(Tools.Patch, ["gateway", Ggpk, "--login", $"127.0.0.1:{ports.Login}"], "login-list", ExitCode.Client, log,
            "could not change the client's login list; close every client and try again");
    }

    /// <summary>The Patch server answers with this hash; the client's data may have changed since it was saved.</summary>
    public static void RefreshRootHash(Output log)
    {
        var (code, _) = Tools.Capture(Tools.Patch, ["root-hash", Ggpk, "--save"]);
        if (code != 0) log.Warn("could not refresh the client data's hash; the saved one is used");
    }
}
