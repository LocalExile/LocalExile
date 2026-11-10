// DevLauncher: a small Windows front end for the developer workflow (Agents/GettingStarted.md steps 4-10).
// One window that does, in order:
//   1. setup check  (dotnet, Client/Game.exe, data/login-keys.json, Client/Game.patched.exe, data/client-constants.json)
//   2. keygen       (GameServer.Auth keygen Client/Game.exe)         - only when the keys are missing
//   3. patch        (GameServer.Auth patch Client/Game.exe)          - only when Game.patched.exe is missing
//   4. gateway list (GameServer.Patch gateway --login 127.0.0.1:<port>) - only when the GGPK's list is not local yet
//   5. account      (register + create_character through GameServer.DevClient piped commands)
//   6. play         (scripts/dev/live.ps1; its output streams into the log panel)
// Every command runs in the repository with the same environment the docs use. Nothing here duplicates live.ps1:
// the launcher prepares what it refuses without, and then hands over.

using System.Diagnostics;
using System.Text;

namespace DevLauncher;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    readonly TextBox _log;
    readonly Button _check, _prepare, _account, _play, _stop;
    readonly TextBox _accountName, _password, _character;
    readonly CheckBox _hotReload;
    readonly ToolStripStatusLabel _status = new("就绪");
    Process? _live;
    CancellationTokenSource? _cancel;

    static string Repo
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GameServer.slnx"))) dir = dir.Parent!;
            return dir?.FullName ?? AppContext.BaseDirectory;
        }
    }

    static string Dotnet => File.Exists(Repo + @"\..\dotnet-sdk\dotnet.exe")
        ? Path.GetFullPath(Repo + @"\..\dotnet-sdk\dotnet.exe") : "dotnet";

    public MainForm()
    {
        Text = "LocalExile Dev Launcher";
        Size = new Size(860, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 88, Padding = new Padding(8), WrapContents = true };
        _check = Btn("1. 检查环境", () => Run("check"));
        _prepare = Btn("2. 一键准备 (keygen/补丁/网关/数据)", () => Run("prepare"));
        _account = Btn("3. 注册账号+角色", () => Run("account"));
        _play = Btn("4. 启动游戏 (live.ps1)", () => Run("play"));
        _stop = Btn("停止", () => Run("stop"));
        top.Controls.AddRange([_check, _prepare, _account, _play, _stop]);

        var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8) };
        _accountName = new TextBox { Width = 140, PlaceholderText = "账号名" };
        _password = new TextBox { Width = 140, PlaceholderText = "密码", UseSystemPasswordChar = true };
        _character = new TextBox { Width = 140, PlaceholderText = "角色名 (witch)" };
        _hotReload = new CheckBox { Text = "热重载", Checked = true, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
        row.Controls.AddRange([new Label { Text = "账号:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _accountName,
            new Label { Text = "密码:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _password,
            new Label { Text = "角色:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, _character, _hotReload]);

        _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
            ReadOnly = true, BackColor = Color.FromArgb(24, 24, 28), ForeColor = Color.FromArgb(220, 220, 225),
            Font = new Font("Consolas", 9f), WordWrap = false };

        var status = new StatusStrip();
        status.Items.Add(_status);

        Controls.Add(_log);
        Controls.Add(row);
        Controls.Add(top);
        Controls.Add(status);

        _status.Text = $"仓库: {Repo}";

        void SetBusy(bool busy)
        {
            foreach (var b in new[] { _check, _prepare, _account, _play }) b!.Enabled = !busy;
            if (_stop is { } stop) stop.Enabled = true;
        }

        async void Run(string what)
        {
            SetBusy(true);
            _cancel = new CancellationTokenSource();
            try
            {
                switch (what)
                {
                    case "check": await CheckAsync(); break;
                    case "prepare": await PrepareAsync(); break;
                    case "account": await AccountAsync(); break;
                    case "play": await PlayAsync(); break;
                    case "stop": StopLive(); break;
                }
            }
            catch (OperationCanceledException) { Log("[launcher] 已取消"); }
            catch (Exception ex) { Log("[launcher] 错误: " + ex.Message); }
            finally
            {
                SetBusy(false);
                _status.Text = "就绪";
            }
        }
    }

    static Button Btn(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6) };
        b.Click += (_, _) => onClick();
        return b;
    }

    void Log(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(line)); return; }
        _log.AppendText(line + Environment.NewLine);
    }

    // One external command with its output streamed into the log panel. Throws when the exit code is not 0
    // (unless tolerate: the patched-client and gateway steps report their own state).
    // The portable toolchain this checkout was set up with (dotnet SDK, Node, Python) joins the child's PATH,
    // so live.ps1 and extract-data.ps1 find node/python/dotnet without a system-wide install.
    async Task CmdAsync(string fileName, string args, bool tolerate = false, bool shell = false)
    {
        Log($"> {fileName} {args}");
        var psi = new ProcessStartInfo
        {
            FileName = shell ? "powershell.exe" : fileName,
            Arguments = shell ? $"-NoProfile -ExecutionPolicy Bypass -Command \"{args.Replace("\"", "`\"")}\"" : args,
            WorkingDirectory = Repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string dir in PortableToolDirs())
        {
            // Prepend: a system-wide runtime-only dotnet (C:\Program Files\dotnet) must not shadow
            // the portable SDK, or `dotnet build` fails with "No .NET SDKs were found".
            string existing = psi.EnvironmentVariables["PATH"] ?? "";
            psi.EnvironmentVariables["PATH"] = string.IsNullOrEmpty(existing) ? dir : dir + ";" + existing;
        }
        // dotnet watch (live.ps1's hot reload) and MSBuild must not linger as background workers after a stop.
        psi.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动进程");
        var token = _cancel!.Token;
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(token);
        }
        finally
        {
            if (token.IsCancellationRequested && !p.HasExited) { try { p.Kill(true); } catch { } }
        }
        if (!tolerate && p.ExitCode != 0) throw new InvalidOperationException($"命令退出码 {p.ExitCode}");
    }

    // The setup state the docs' steps 4-7 need, with one line each.
    async Task CheckAsync()
    {
        string client = Path.Combine(Repo, "Client", "Game.exe");
        string patched = Path.Combine(Repo, "Client", "Game.patched.exe");
        string keys = Path.Combine(Repo, "data", "login-keys.json");
        string constants = Path.Combine(Repo, "data", "client-constants.json");
        string ggpk = Path.Combine(Repo, "Client", "Content.ggpk");

        Log($"dotnet        : {File.Exists(Dotnet) || WhereExists("dotnet")}");
        Log($"Client/Game.exe        : {File.Exists(client)}{(File.Exists(client) ? "" : "  (需要把 PathOfExile.exe 复制为 Game.exe)")}");
        Log($"Client/Content.ggpk    : {File.Exists(ggpk)}");
        Log($"data/login-keys.json   : {File.Exists(keys)}{(File.Exists(keys) ? "" : "  (步骤 4: keygen)")}");
        Log($"Client/Game.patched.exe: {File.Exists(patched)}{(File.Exists(patched) ? "" : "  (步骤 5: patch)")}");
        Log($"data/client-constants  : {File.Exists(constants)}{(File.Exists(constants) ? "" : "  (步骤 7: extract client-constants)")}");
        Log($"data/tables            : {Directory.Exists(Path.Combine(Repo, "data", "tables"))}");
        Log("检查完成。缺什么就点「一键准备」.");
    }

    static bool WhereExists(string exe)
    {
        try { return Process.Start(new ProcessStartInfo("where", exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }) is { } p && p.WaitForExit(5000) && p.StandardOutput.ReadToEnd().Length > 0; }
        catch { return false; }
    }

    // The portable tools installed next to the repository (this checkout was set up without a system-wide
    // dotnet/node/python): the folders that exist join every child's PATH.
    static IEnumerable<string> PortableToolDirs()
    {
        var workspace = Directory.GetParent(Repo)?.FullName;
        if (workspace is null) yield break;
        foreach (var dir in new[]
        {
            Path.Combine(workspace, "dotnet-sdk"),
            Path.Combine(workspace, "node", "node-v24.4.1-win-x64"),
            Path.Combine(workspace, "python"),
        })
            if (Directory.Exists(dir)) yield return dir;
    }

    async Task PrepareAsync()
    {
        string keys = Path.Combine(Repo, "data", "login-keys.json");
        string patched = Path.Combine(Repo, "Client", "Game.patched.exe");
        string client = Path.Combine(Repo, "Client", "Game.exe");

        if (!File.Exists(keys))
        {
            Log("— 步骤 4: 生成登录密钥 —");
            await CmdAsync(Dotnet, $"run --project src/GameServer.Auth -- keygen Client/Game.exe");
        }
        else Log("密钥已存在，跳过 keygen");

        if (!File.Exists(patched) && File.Exists(client))
        {
            Log("— 步骤 5: 补丁客户端 —");
            await CmdAsync(Dotnet, $"run --project src/GameServer.Auth -- patch Client/Game.exe Client/Game.patched.exe");
        }
        else Log(patched.EndsWith("x") && File.Exists(patched) ? "已补丁，跳过" : "缺 Game.exe，跳过补丁");

        Log("— 步骤 6: 网关列表指向本机 —");
        await CmdAsync(Dotnet, "run --project src/GameServer.Patch -- gateway --login 127.0.0.1:20493");

        Log("— 步骤 7: 提取游戏数据 —");
        await CmdAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File scripts/dev/extract-data.ps1", tolerate: true);
        Log("准备完成。");
    }

    async Task AccountAsync()
    {
        string name = string.IsNullOrWhiteSpace(_accountName.Text) ? "devplayer" : _accountName.Text.Trim();
        string pass = string.IsNullOrWhiteSpace(_password.Text) ? "dev1234" : _password.Text.Trim();
        string character = string.IsNullOrWhiteSpace(_character.Text) ? "MyWitch" : _character.Text.Trim();

        Log("— 步骤 8: 启动 Host 并注册 —");
        var psi = new ProcessStartInfo(Dotnet, "run --project src/GameServer.Host")
        {
            WorkingDirectory = Repo, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var host = Process.Start(psi) ?? throw new InvalidOperationException("Host 启动失败");
        await Task.Delay(3500); // Host listens on 127.0.0.1:7000; the docs start it in its own terminal first.

        try
        {
            string script = string.Join('\n',
                $"register name={name} password={pass}",
                $"login name={name} password={pass}",
                $"create_character name={character} class=witch",
                "characters");
            await CmdAsync(Dotnet, $"run --project src/GameServer.DevClient", tolerate: true, shell: false);
            // The DevClient is interactive; pipe the commands instead:
            var devPsi = new ProcessStartInfo(Dotnet, "run --project src/GameServer.DevClient")
            {
                WorkingDirectory = Repo, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var dev = Process.Start(devPsi) ?? throw new InvalidOperationException("DevClient 启动失败");
            await dev.StandardInput.WriteLineAsync(script);
            await dev.StandardInput.WriteLineAsync("");
            string output = await dev.StandardOutput.ReadToEndAsync();
            Log(output);
            bool ok = output.Contains("\"ok\":true");
            Log(ok ? "账号/角色就绪。" : "注册输出里有非 ok 行，看上面日志。");
            if (!dev.HasExited) { try { dev.Kill(true); } catch { } }
        }
        finally
        {
            if (!host.HasExited) { try { host.Kill(true); } catch { } }
        }
    }

    async Task PlayAsync()
    {
        string args = _hotReload.Checked ? "" : " -NoHotReload";
        Log($"— 步骤 10: live.ps1{args} —");
        Log("（脚本会自动构建并启动三个服务器，然后打开客户端；日志流式显示在下面）");
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File scripts/dev/live.ps1{args}")
        {
            WorkingDirectory = Repo, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        // Same toolchain PATH as CmdAsync: extract-data.ps1 inside live.ps1 needs node and python.
        foreach (string dir in PortableToolDirs())
        {
            // Prepend: a system-wide runtime-only dotnet (C:\Program Files\dotnet) must not shadow
            // the portable SDK, or `dotnet build` fails with "No .NET SDKs were found".
            string existing = psi.EnvironmentVariables["PATH"] ?? "";
            psi.EnvironmentVariables["PATH"] = string.IsNullOrEmpty(existing) ? dir : dir + ";" + existing;
        }
        psi.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        // A client of another build fails the constants check loudly; the launcher keeps the last valid
        // constants file (written once by Tools/ConstantsGen or by hand), so extraction may skip that entry.
        psi.EnvironmentVariables["Game_EXTRACT_SKIP_IF_PRESENT"] = "client-constants";
        _live = Process.Start(psi);
        _live!.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        _live.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        _live.BeginOutputReadLine();
        _live.BeginErrorReadLine();
        await _live.WaitForExitAsync(_cancel!.Token);
        Log($"[launcher] live.ps1 退出码 {_live.ExitCode}");
    }

    void StopLive()
    {
        if (_live is { HasExited: false }) { try { _live.Kill(true); Log("[launcher] 已停止 live.ps1"); } catch { } }
        _cancel?.Cancel();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopLive();
        base.OnFormClosing(e);
    }
}
