using System.Drawing;

namespace LocalExileConnect;

/// <summary>The form a player sees: the host's address, the three checks, and the three actions. All the work is in
/// <see cref="Client"/>.</summary>
internal sealed class MainForm : Form
{
    readonly TextBox _host = new() { Text = "", Width = 260 };
    readonly NumericUpDown _loginPort = Port(Client.DefaultLoginPort);
    readonly NumericUpDown _gatewayPort = Port(Client.DefaultGatewayPort);
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 190 };
    readonly Label _dir = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    readonly Button _check = new() { Text = "测试连接", AutoSize = true };
    readonly Button _set = new() { Text = "配置客户端", AutoSize = true };
    readonly Button _restore = new() { Text = "还原原版", AutoSize = true };
    readonly Button _launch = new() { Text = "启动游戏", AutoSize = true };

    static NumericUpDown Port(int value) => new() { Minimum = 1, Maximum = 65535, Value = value, Width = 74 };

    public MainForm()
    {
        Text = "LocalExile 联机助手";
        MinimumSize = new Size(560, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        var hostRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        hostRow.Controls.Add(new Label { Text = "服务器地址（IP 或域名）", AutoSize = true, Margin = new Padding(3, 9, 3, 3) });
        hostRow.Controls.Add(_host);
        hostRow.Controls.Add(new Label { Text = "登录端口", AutoSize = true, Margin = new Padding(10, 9, 3, 3) });
        hostRow.Controls.Add(_loginPort);
        hostRow.Controls.Add(new Label { Text = "实例端口", AutoSize = true, Margin = new Padding(10, 9, 3, 3) });
        hostRow.Controls.Add(_gatewayPort);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        buttons.Controls.Add(_check);
        buttons.Controls.Add(_set);
        buttons.Controls.Add(_restore);
        buttons.Controls.Add(_launch);

        _log.Dock = DockStyle.Fill;
        _dir.Dock = DockStyle.Bottom;

        var pick = new Button { Text = "选择游戏目录", AutoSize = true };
        var pickRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false };
        pickRow.Controls.Add(pick);
        pickRow.Controls.Add(_dir);

        // Fill first, then the docked rows: the log takes what is left.
        Controls.Add(_log);
        Controls.Add(pickRow);
        Controls.Add(buttons);
        Controls.Add(hostRow);

        _check.Click += (_, _) => Run(() => Client.Check(Host(), (ushort)_loginPort.Value, 0, (ushort)_gatewayPort.Value));
        _set.Click += (_, _) =>
        {
            string hostPort = $"{Host()}:{(int)_loginPort.Value}";
            Run(() => [Client.Set(Dir(), hostPort)]);
        };
        _restore.Click += (_, _) => Run(() => [Client.Restore(Dir())]);
        _launch.Click += (_, _) => Run(() => [Client.Launch(Dir())]);
        pick.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择客户端文件夹（其中有 Content.ggpk）" };
            if (dialog.ShowDialog(this) == DialogResult.OK) ShowDir(dialog.SelectedPath);
        };

        ShowDir(AppContext.BaseDirectory);
        Say("填好服务器地址后：先「测试连接」，再「配置客户端」，然后「启动游戏」。");
        Say("第一次配置会同时备份原文件，随时可以「还原原版」。");
    }

    string _gameDir = "";
    string Dir() => _gameDir;
    string Host() => _host.Text.Trim();

    void ShowDir(string dir)
    {
        _gameDir = dir;
        bool ok = File.Exists(Path.Combine(dir, "Content.ggpk"));
        _dir.Text = ok ? $"客户端目录：{dir}" : $"客户端目录：{dir}（这里没有 Content.ggpk）";
        _dir.ForeColor = ok ? SystemColors.GrayText : Color.Firebrick;
    }

    void Say(string text) => _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");

    void Run(Func<string> action) => Run(() => new[] { action() });

    void Run(Func<IReadOnlyList<string>> action)
    {
        SetBusy(true);
        try { foreach (var line in action()) Say(line); }
        catch (Exception ex) { Say($"出错：{ex.Message}"); }
        finally { SetBusy(false); }
    }

    void SetBusy(bool busy)
    {
        foreach (Control c in new Control[] { _check, _set, _restore, _launch }) c.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        if (!busy) return;
        Application.DoEvents();
    }
}