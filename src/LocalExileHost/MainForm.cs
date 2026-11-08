using System.Diagnostics;

namespace LocalExileHost;

/// <summary>
/// The host window: the address other machines will use, then 开服. It runs the documented
/// <c>scripts/dev/live.ps1</c> with <c>-Lan</c> (plus the public address/ports) and streams its output here, so the
/// long build, a held port or a failed start is readable instead of being hidden in a console that scrolls away.
/// </summary>
internal sealed class MainForm : Form
{
    readonly TextBox _address = new() { Width = 220 };
    readonly TextBox _instanceAddress = new() { Width = 180 };
    readonly NumericUpDown _loginPort = Port(0);
    readonly NumericUpDown _instancePort = Port(0);
    readonly CheckBox _withClient = new() { Text = "同时启动我的游戏客户端", Checked = true, AutoSize = true };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F) };
    readonly Label _status = new() { AutoSize = true };
    readonly Label _repoLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    readonly Button _start = new() { Text = "开服", AutoSize = true };
    readonly Button _stop = new() { Text = "停服", AutoSize = true };
    readonly Button _probe = new() { Text = "端口自检", AutoSize = true };

    string _repo = "";
    Process? _running;

    static NumericUpDown Port(int value) => new() { Minimum = 0, Maximum = 65535, Value = value, Width = 74 };

    public MainForm()
    {
        Text = "LocalExile 开服器";
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        row1.Controls.Add(new Label { Text = "登录地址（IP 或域名）", AutoSize = true, Margin = new Padding(3, 9, 3, 3) });
        row1.Controls.Add(_address);
        row1.Controls.Add(new Label { Text = "公网登录端口（0=与本地相同）", AutoSize = true, Margin = new Padding(10, 9, 3, 3) });
        row1.Controls.Add(_loginPort);

        var row1b = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        row1b.Controls.Add(new Label { Text = "实例地址（两条隧道同节点就留空）", AutoSize = true, Margin = new Padding(3, 9, 3, 3) });
        row1b.Controls.Add(_instanceAddress);
        row1b.Controls.Add(new Label { Text = "公网实例端口（0=与本地相同）", AutoSize = true, Margin = new Padding(10, 9, 3, 3) });
        row1b.Controls.Add(_instancePort);

        var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        row2.Controls.Add(_start);
        row2.Controls.Add(_stop);
        row2.Controls.Add(_probe);
        row2.Controls.Add(_withClient);

        var pickRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false };
        var pick = new Button { Text = "选择仓库目录", AutoSize = true };
        pickRow.Controls.Add(pick);
        pickRow.Controls.Add(_repoLabel);

        _status.Dock = DockStyle.Bottom;

        Controls.Add(_log);
        Controls.Add(pickRow);
        Controls.Add(_status);
        Controls.Add(row2);
        Controls.Add(row1b);
        Controls.Add(row1);

        pick.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择 LocalExile 仓库目录（其中有 scripts\\dev\\live.ps1）" };
            if (dialog.ShowDialog(this) == DialogResult.OK) ShowRepo(dialog.SelectedPath);
        };
        _start.Click += (_, _) => Start();
        _stop.Click += (_, _) => Run("停服", ["-Stop"]);
        _probe.Click += (_, _) => Probe();

        _running = null;
        ShowRepo(FindRepo());
        Say("填好玩家会用的地址，点「开服」。本机测试可以先填局域网地址；有内网穿透就填穿透给的地址和公网端口。");
    }

    /// <summary>The repository this exe belongs to: beside it, or up the tree (dist\host\ → repo).</summary>
    static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "dev", "live.ps1"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    void ShowRepo(string dir)
    {
        _repo = dir;
        bool ok = File.Exists(Path.Combine(dir, "scripts", "dev", "live.ps1"));
        _repoLabel.Text = ok ? $"仓库目录：{dir}" : $"仓库目录：{dir}（这里没有 scripts\\dev\\live.ps1）";
        _repoLabel.ForeColor = ok ? SystemColors.GrayText : Color.Firebrick;
        _start.Enabled = ok;
        _stop.Enabled = ok;
    }

    void Start()
    {
        if (_running is { HasExited: false }) { Say("已经在运行了。"); return; }
        string address = _address.Text.Trim();
        if (address.Length == 0) { Say("先填玩家会用的地址（局域网 IP、穿透域名都可以）。"); return; }

        var args = new List<string> { "-NoExtract", "-Lan", "-PublicAddress", address };
        string instanceAddress = _instanceAddress.Text.Trim();
        if (instanceAddress.Length > 0) { args.Add("-PublicInstanceAddress"); args.Add(instanceAddress); }
        if (_loginPort.Value > 0) { args.Add("-PublicLoginPort"); args.Add(((int)_loginPort.Value).ToString()); }
        if (_instancePort.Value > 0) { args.Add("-PublicPort"); args.Add(((int)_instancePort.Value).ToString()); }
        if (!_withClient.Checked) args.Add("-NoClient");
        Run("开服", [.. args]);
    }

    void Run(string what, string[] liveArgs)
    {
        if (_repo.Length == 0) return;
        var info = new ProcessStartInfo("powershell")
        {
            WorkingDirectory = _repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // live.ps1 builds the servers with `dotnet`; a window started from Explorer has no DOTNET_ROOT, so the .NET 6
        // host without an SDK would answer "Download a .NET SDK" and the script would stop at its first build.
        string? root = WorkingDotnet();
        if (root is not null)
        {
            info.Environment["DOTNET_ROOT"] = root;
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            info.Environment["PATH"] = root + Path.PathSeparator + path;
            Say($"使用 dotnet：{root}");
        }
        else Say("! 没有找到带 SDK 的 .NET：如果开服失败并提示 sdk-not-found，请先安装 .NET 10 SDK。");
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(Path.Combine(_repo, "scripts", "dev", "live.ps1"));
        foreach (var a in liveArgs) info.ArgumentList.Add(a);

        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Say(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) Say("! " + line); };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => BeginInvoke(() =>
        {
            Say($"{what}：脚本退出（代码 {process.ExitCode}）。");
            _status.Text = "未运行";
            _start.Enabled = true;
            _stop.Enabled = true;
        });
        Say($"--- {what}：live.ps1 {string.Join(' ', liveArgs)}");
        _status.Text = $"{what}中…";
        _start.Enabled = false;
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _running = process;
            _stop.Enabled = true;
        }
        catch (Exception ex)
        {
            Say($"启动失败：{ex.Message}");
            _status.Text = "未运行";
            _start.Enabled = true;
        }
    }

    /// <summary>The directory of a <c>dotnet</c> that actually has an SDK: DOTNET_ROOT first, then the per-user install the
/// .NET installer makes when it cannot write Program Files, then Program Files. Null when none has one.</summary>
static string? WorkingDotnet()
{
    var candidates = new List<string>();
    if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
        candidates.Add(Path.Combine(root, "dotnet.exe"));
    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet.exe"));
    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));
    foreach (string exe in candidates.Where(File.Exists))
        if (HasSdk(exe)) return Path.GetDirectoryName(exe)!;
    return null;
}

static bool HasSdk(string exe)
{
    try
    {
        var info = new ProcessStartInfo(exe, "--list-sdks") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var probe = Process.Start(info);
        if (probe is null) return false;
        string text = probe.StandardOutput.ReadToEnd();
        return probe.WaitForExit(5000) && probe.ExitCode == 0 && text.Trim().Length > 0;
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { return false; }
}

void Probe()
    {
        foreach (var (port, what) in new[] { (20493, "登录"), (6112, "实例"), (12995, "补丁") })
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                var task = tcp.ConnectAsync("127.0.0.1", port);
                var ok = task.Wait(TimeSpan.FromSeconds(2));
                Say($"{what}端口 {port}：{(ok ? "在监听" : "没有响应")}");
            }
            catch (Exception ex) { Say($"{what}端口 {port}：{ex.GetBaseException().Message}"); }
        }
    }

    void Say(string text)
    {
        if (!_log.IsDisposed)
        {
            if (_log.InvokeRequired) _log.BeginInvoke(() => Say(text));
            else _log.AppendText($"{text}{Environment.NewLine}");
        }
        // Also on disk: a window that has been closed (or a report to someone else) still has the whole run.
        try
        {
            if (_repo.Length > 0)
            {
                Directory.CreateDirectory(Path.Combine(_repo, "logs"));
                File.AppendAllText(Path.Combine(_repo, "logs", "host-gui.log"), $"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* the window's copy is enough */ }
    }
}