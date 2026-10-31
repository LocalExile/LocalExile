using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace LocalExile;

/// <summary>Client\ in the install: a copy of the player's client, or a junction to it. The tools and servers all use that
/// path, as they do in a checkout. Game.exe and Game.patched.exe are added beside the client's own files.</summary>
static class ClientInstall
{
    const string Folder = "Client";
    static string Ggpk => Path.Combine(Folder, "Content.ggpk");
    static string GameExe => Path.Combine(Folder, "Game.exe");
    static string PatchedExe => Path.Combine(Folder, "Game.patched.exe");
    const string Keys = @"data\login-keys.json";

    /// <summary>The folder's 3.29.3b executable, or why it has none.</summary>
    static string? FindExecutable(string folder, out string problem)
    {
        problem = "";
        if (!File.Exists(Path.Combine(folder, "Content.ggpk")))
        {
            problem = $"{folder} has no Content.ggpk. LocalExile needs the standalone client layout; Steam installs do not work";
            return null;
        }
        // A checkout's PathOfExile.exe can be the small launcher, with the game in Game.exe. The check reads the executable.
        foreach (string name in new[] { "PathOfExile.exe", "Game.exe" })
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            try
            {
                byte[] image = File.ReadAllBytes(path);
                _ = new ClientImage(image);
                HncSender.LoadMask(image);
                return path;
            }
            catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException or IOException or InvalidOperationException) { }
        }
        problem = $"{folder} does not hold the Path of Exile 3.29.3b executable; other versions do not work";
        return null;
    }

    public static void Check(string folder, Output log)
    {
        string? exe = FindExecutable(folder, out string problem);
        if (exe is null) throw new LauncherException(ExitCode.Client, problem);
        log.Value("client", $"{exe} is the 3.29.3b client");
    }

    /// <summary>Client\ holds the checked client when this returns.</summary>
    public static void Ensure(Options options, Output log)
    {
        log.Step("client-check", "checking the client ...");
        if (Directory.Exists(Folder))
        {
            string? exe = FindExecutable(Folder, out string problem);
            if (exe is null) throw new LauncherException(ExitCode.Client, $"{problem} (remove the Client folder to choose another)");
            EnsureGameExe(exe);
            log.Info($"using {Path.GetFullPath(Folder)}");
            return;
        }

        string source = options.Client ?? Ask(log);
        string? found = FindExecutable(source, out string why);
        if (found is null) throw new LauncherException(ExitCode.Client, why);
        log.Info($"{found} is the 3.29.3b client");

        string mode = options.ClientMode ?? AskMode(source, log);
        if (mode == "link") Link(source, log);
        else Copy(source, log);
        EnsureGameExe(Path.Combine(Folder, Path.GetFileName(found)));
    }

    static string Ask(Output log)
    {
        string? guess = Guesses().FirstOrDefault(g => FindExecutable(g, out _) is not null);
        if (log.Json) throw log.NeedsInput("--client", "which folder holds the Path of Exile 3.29.3b client?", guess);
        if (guess is not null)
        {
            Console.Write($"Found the 3.29.3b client in {guess}. Use it? [Y/n] ");
            if ((Console.ReadLine() ?? "").Trim().ToLowerInvariant() is "" or "y" or "yes") return guess;
        }
        Console.Write("Folder of your Path of Exile 3.29.3b client: ");
        string answer = (Console.ReadLine() ?? "").Trim().Trim('"');
        if (answer.Length == 0) throw new LauncherException(ExitCode.NeedsInput, "no client folder given");
        return answer;
    }

    static IEnumerable<string> Guesses()
    {
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
        Console.WriteLine($"and a newer launcher cannot bring 3.29.3b back, so a separate copy is safer. It needs {needed >> 30} GB; {free >> 30} GB are free here.");
        Console.Write("[C]opy the client, or [L]ink to it and use that folder itself? [C/l] ");
        return (Console.ReadLine() ?? "").Trim().ToLowerInvariant() is "l" or "link" ? "link" : "copy";
    }

    static long Size(string folder) => new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    static void Link(string source, Output log)
    {
        log.Step("client-link", $"linking Client to {source} ...");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", Path.GetFullPath(Folder), Path.GetFullPath(source) }) start.ArgumentList.Add(argument);
        using var mklink = System.Diagnostics.Process.Start(start)!;
        mklink.WaitForExit();
        if (mklink.ExitCode != 0) throw new LauncherException(ExitCode.Client, $"could not link Client: {mklink.StandardError.ReadToEnd().Trim()}");
    }

    /// <summary>Into a temporary folder that becomes Client\ only when complete, so a stopped copy is resumed and never used.</summary>
    static void Copy(string source, Output log)
    {
        string temp = Folder + ".partial";
        var files = new DirectoryInfo(source).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        long total = files.Sum(f => f.Length), done = 0;
        long free = new DriveInfo(Path.GetFullPath(".")).AvailableFreeSpace;
        if (free < total) throw new LauncherException(ExitCode.Client, $"copying the client needs {total >> 30} GB and {free >> 30} GB are free; free some space, or use --client-mode link");
        log.Step("client-copy", $"copying the client ({total >> 30} GB); this can take several minutes ...");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var report = TimeSpan.FromSeconds(10);
        byte[] buffer = new byte[4 << 20];
        foreach (var file in files)
        {
            string target = Path.Combine(temp, Path.GetRelativePath(source, file.FullName));
            var existing = new FileInfo(target);
            if (existing.Exists && existing.Length == file.Length && existing.LastWriteTimeUtc == file.LastWriteTimeUtc) { done += file.Length; continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var from = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan))
            using (var to = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1))
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
        if (File.Exists(PatchedExe) && File.GetLastWriteTimeUtc(PatchedExe) >= File.GetLastWriteTimeUtc(Keys)) return;
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
