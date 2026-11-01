// LocalExileGUI: a Windows front end for LocalExile.exe. It lives beside LocalExile.exe and drives it through --json,
// so LocalExile can be updated by extracting a new release over the folder without touching this program.
// Built with the C# 5 compiler of .NET Framework 4.x (see build.ps1): no string interpolation, no ?. and no tuples.
// Its text is in Strings.cs (I18n.T).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("LocalExile GUI")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace LocalExileGui
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var settings = Settings.Load();
            I18n.Use(settings.Language);
            if (!File.Exists(Cli.ExePath))
            {
                MessageBox.Show(I18n.T("app.noExe"), "LocalExile GUI", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Application.Run(new MainForm(settings));
        }
    }

    /// <summary>LocalExileGUI.ini beside the program: key=value lines.</summary>
    sealed class Settings
    {
        static readonly string FilePath = Path.Combine(Cli.Root, "LocalExileGUI.ini");

        public string Client = "";
        public string Language = "";
        public bool UpdateCheck = true;
        public bool ServersOnly;

        public static Settings Load()
        {
            var s = new Settings();
            if (!File.Exists(FilePath)) return s;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (key == "client") s.Client = value;
                else if (key == "language") s.Language = value;
                else if (key == "updateCheck") s.UpdateCheck = value != "0";
                else if (key == "serversOnly") s.ServersOnly = value == "1";
            }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllLines(FilePath, new[]
                {
                    "client=" + Client,
                    "language=" + Language,
                    "updateCheck=" + (UpdateCheck ? "1" : "0"),
                    "serversOnly=" + (ServersOnly ? "1" : "0"),
                }, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    sealed class CommandResult
    {
        public int Code;
        public List<string> Lines = new List<string>();
    }

    static class Cli
    {
        public static readonly string Root = Path.GetDirectoryName(Application.ExecutablePath);
        public static readonly string ExePath = Path.Combine(Root, "LocalExile.exe");
        public static readonly string AuthPath = Path.Combine(Root, "app", "GameServer.Auth.exe");
        public static readonly string ModsDir = Path.Combine(Root, "Mods", "Binaries");
        public const string ModStateFile = "mod-state.txt";

        public static string ExitCodeText(int code)
        {
            return code >= 0 && code <= 8 ? I18n.T("exit." + code) : I18n.T("exit.unknown", code);
        }

        public static string StepText(string id, string fallback)
        {
            if (id == null) return fallback;
            if (id.StartsWith("server")) return I18n.T("step.servers");
            switch (id)
            {
                case "client-check":
                case "client-copy":
                case "extract":
                case "keys":
                case "account":
                case "login-list":
                case "client":
                    return I18n.T("step." + id);
                default:
                    return fallback;
            }
        }

        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') sb.Append('\\', backslashes * 2 + 1);
                else sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            return sb.Append('"').ToString();
        }

        public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args, bool stdin)
        {
            return new ProcessStartInfo(exe, string.Join(" ", args.Select(Quote)))
            {
                WorkingDirectory = Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
        }

        /// <summary>What "LocalExile.exe stop" does, minus this program: that command ends every process that runs from the
        /// install, and this program's tree contains the command, so it fails when started from here.</summary>
        public static List<string> StopInstall()
        {
            var stopped = new List<string>();
            int self = Process.GetCurrentProcess().Id;
            string prefix = Root.TrimEnd('\\') + "\\";
            string client = Path.Combine(Root, "Client") + "\\";
            var ours = new List<Process>();
            foreach (var p in Process.GetProcesses())
            {
                string path = null;
                try { if (p.Id != self) path = p.MainModule.FileName; }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                bool mine = path != null && (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(client, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(path), "Game.patched.exe", StringComparison.OrdinalIgnoreCase));
                if (mine) ours.Add(p); else p.Dispose();
            }
            // LocalExile.exe first: its kill-on-exit job ends the servers and the client with it.
            foreach (var p in ours.OrderBy(p => p.ProcessName.Equals("LocalExile", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                using (p)
                {
                    try
                    {
                        if (p.HasExited) continue;
                        p.Kill();
                        stopped.Add(p.ProcessName + " (" + p.Id + ")");
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            return stopped;
        }

        /// <summary>Runs a short command to its end and returns its exit code and every output line.</summary>
        public static Task<CommandResult> Capture(string exe, IEnumerable<string> args, string stdinLine)
        {
            var list = args.ToList();
            return Task.Run(() =>
            {
                var result = new CommandResult();
                using (var p = new Process { StartInfo = StartInfo(exe, list, stdinLine != null) })
                {
                    var gate = new object();
                    DataReceivedEventHandler add = (s, e) => { if (e.Data != null) lock (gate) result.Lines.Add(e.Data); };
                    p.OutputDataReceived += add;
                    p.ErrorDataReceived += add;
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (stdinLine != null)
                    {
                        p.StandardInput.WriteLine(stdinLine);
                        p.StandardInput.Close();
                    }
                    p.WaitForExit();
                    result.Code = p.ExitCode;
                }
                return result;
            });
        }
    }

    /// <summary>Runs LocalExile.exe with --json and reports each event on the UI thread.</summary>
    sealed class JsonRunner
    {
        readonly Control _ui;
        readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        Process _process;

        public event Action<Dictionary<string, object>> Event;
        public event Action<string> RawLine;
        public event Action<int> Exited;

        public JsonRunner(Control ui) { _ui = ui; }

        public bool Running { get { return _process != null; } }

        /// <summary>Ends LocalExile.exe; its servers, client and tools end with it (it puts them in a kill-on-exit job).</summary>
        public void Kill()
        {
            try { if (_process != null && !_process.HasExited) _process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        public void Start(IEnumerable<string> args, string stdinLine)
        {
            var list = args.ToList();
            list.Add("--json");
            var p = new Process { StartInfo = Cli.StartInfo(Cli.ExePath, list, stdinLine != null), EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) Post(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) Post(e.Data); };
            p.Exited += (s, e) =>
            {
                p.WaitForExit();
                int code = p.ExitCode;
                _ui.BeginInvoke(new Action(() =>
                {
                    _process = null;
                    p.Dispose();
                    if (Exited != null) Exited(code);
                }));
            };
            _process = p;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (stdinLine != null)
            {
                p.StandardInput.WriteLine(stdinLine);
                p.StandardInput.Close();
            }
        }

        void Post(string line)
        {
            Dictionary<string, object> ev = null;
            if (line.StartsWith("{"))
            {
                try { ev = _json.DeserializeObject(line) as Dictionary<string, object>; }
                catch (ArgumentException) { }
            }
            _ui.BeginInvoke(new Action(() =>
            {
                if (ev != null && ev.ContainsKey("event")) { if (Event != null) Event(ev); }
                else if (RawLine != null) RawLine(line);
            }));
        }
    }

    sealed class ModEntry
    {
        public string Id;
        public string Version;
        public bool Enabled;
    }

    static class Mods
    {
        /// <summary>The mod's description in Strings.cs ("mod.&lt;id&gt;"), empty for a mod it does not know.</summary>
        public static string Describe(string id)
        {
            string key = "mod." + id;
            string text = I18n.T(key);
            return text == key ? "" : text;
        }

        static Dictionary<string, bool> ReadChoices()
        {
            var choices = new Dictionary<string, bool>(StringComparer.Ordinal);
            string path = Path.Combine(Cli.ModsDir, Cli.ModStateFile);
            if (!File.Exists(path)) return choices;
            foreach (string line in File.ReadAllLines(path))
            {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && (parts[1] == "on" || parts[1] == "off")) choices[parts[0]] = parts[1] == "on";
            }
            return choices;
        }

        /// <summary>Each mod folder, enabled as the Gateway decides it: mod-state.txt first, then the manifest's "enabled".</summary>
        public static List<ModEntry> Load()
        {
            var result = new List<ModEntry>();
            if (!Directory.Exists(Cli.ModsDir)) return result;
            var choices = ReadChoices();
            foreach (string dir in Directory.GetDirectories(Cli.ModsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                string id = Path.GetFileName(dir);
                string version = "0";
                bool enabled = true;
                string manifest = Path.Combine(dir, "mod.json");
                if (File.Exists(manifest))
                {
                    string text = File.ReadAllText(manifest);
                    var v = Regex.Match(text, "\"version\"\\s*:\\s*\"([^\"]*)\"");
                    if (v.Success) version = v.Groups[1].Value;
                    var e = Regex.Match(text, "\"enabled\"\\s*:\\s*(true|false)");
                    if (e.Success) enabled = e.Groups[1].Value == "true";
                }
                bool choice;
                if (choices.TryGetValue(id, out choice)) enabled = choice;
                result.Add(new ModEntry { Id = id, Version = version, Enabled = enabled });
            }
            return result;
        }

        public static void Save(IEnumerable<ModEntry> mods)
        {
            var choices = ReadChoices();
            foreach (var m in mods) choices[m.Id] = m.Enabled;
            var lines = choices.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + " " + (c.Value ? "on" : "off"));
            File.WriteAllLines(Path.Combine(Cli.ModsDir, Cli.ModStateFile), lines, new UTF8Encoding(false));
        }
    }

    sealed class MainForm : Form
    {
        readonly JsonRunner _runner;
        readonly Settings _settings;
        string _runningCommand;
        bool _closeAfterStop, _stopRequested;

        // Shared status area.
        Label _status;
        ProgressBar _progress;
        RichTextBox _log;

        // Play tab.
        Button _start, _stop;
        CheckBox _updateCheck, _serversOnly;
        ComboBox _language;

        // Setup tab.
        TextBox _clientDir, _setupAccount, _setupPassword, _setupPassword2;
        RadioButton _modeCopy, _modeLink;
        Button _checkClient, _runSetup;

        // Mods tab.
        ListView _modList;

        // Account tab.
        Label _accountCount;
        TextBox _newAccount, _newPassword, _newPassword2, _pwAccount, _pwPassword, _pwPassword2;
        Button _createAccount, _changePassword;

        // Check tab.
        Button _fullCheck;

        public MainForm(Settings settings)
        {
            _settings = settings;
            Text = I18n.T("app.title");
            Font = new Font(I18n.UiFont, 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(760, 600);
            Size = new Size(900, 720);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Cli.ExePath); } catch { }

            _runner = new JsonRunner(this);
            _runner.Event += OnEvent;
            _runner.RawLine += line => Log(line, Color.DimGray);
            _runner.Exited += OnExited;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 330));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildPlayTab());
            tabs.TabPages.Add(BuildSetupTab());
            tabs.TabPages.Add(BuildModsTab());
            tabs.TabPages.Add(BuildAccountTab());
            tabs.TabPages.Add(BuildCheckTab());
            tabs.SelectedIndexChanged += (s, e) =>
            {
                if (tabs.SelectedIndex == 2) RefreshMods();
                if (tabs.SelectedIndex == 3) RefreshAccountCount();
            };
            root.Controls.Add(tabs, 0, 0);

            _status = new Label { Text = I18n.T("status.ready"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 8, 3, 4) };
            root.Controls.Add(_status, 0, 1);
            _progress = new ProgressBar { Dock = DockStyle.Fill, Height = 18, Minimum = 0, Maximum = 1000 };
            root.Controls.Add(_progress, 0, 2);
            _log = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, DetectUrls = true,
                Font = new Font("Consolas", 9f), WordWrap = true, BorderStyle = BorderStyle.FixedSingle,
            };
            _log.LinkClicked += (s, e) => OpenPath(e.LinkText);
            root.Controls.Add(_log, 0, 3);
            Controls.Add(root);

            _clientDir.Text = _settings.Client;
            _updateCheck.Checked = _settings.UpdateCheck;
            _serversOnly.Checked = _settings.ServersOnly;
            Shown += (s, e) => FirstLook();
        }

        // ---------------------------------------------------------------- layout helpers

        static TableLayoutPanel Grid(int columns)
        {
            return new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, AutoSize = true, Padding = new Padding(6) };
        }

        static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
        }

        static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 6, 3, 3), MaximumSize = new Size(820, 0) };
        }

        static Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(3, 3, 6, 3) };
            b.Click += click;
            return b;
        }

        static TextBox Input(bool password)
        {
            return new TextBox { Width = 260, UseSystemPasswordChar = password, Anchor = AnchorStyles.Left };
        }

        static FlowLayoutPanel Row(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            f.Controls.AddRange(controls);
            return f;
        }

        // ---------------------------------------------------------------- tabs

        TabPage BuildPlayTab()
        {
            var page = new TabPage(I18n.T("tab.play"));
            var g = Grid(1);
            _start = Btn(I18n.T("play.start"), (s, e) => StartRun());
            _start.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
            _start.Padding = new Padding(24, 6, 24, 6);
            _stop = Btn(I18n.T("play.stop"), (s, e) => StopAll());
            _stop.Font = new Font(Font.FontFamily, 12f);
            _stop.Padding = new Padding(16, 6, 16, 6);
            g.Controls.Add(Row(_start, _stop));
            _updateCheck = new CheckBox { Text = I18n.T("play.updateCheck"), AutoSize = true, Checked = true };
            _serversOnly = new CheckBox { Text = I18n.T("play.serversOnly"), AutoSize = true };
            g.Controls.Add(_updateCheck);
            g.Controls.Add(_serversOnly);
            g.Controls.Add(Hint(I18n.T("play.hint")));
            _language = BuildLanguagePicker();
            g.Controls.Add(Row(Btn(I18n.T("play.openLogs"), (s, e) => OpenPath(Path.Combine(Cli.Root, "logs"))),
                Btn(I18n.T("play.openRoot"), (s, e) => OpenPath(Cli.Root)),
                Btn(I18n.T("play.clearLog"), (s, e) => _log.Clear()),
                Caption(I18n.T("play.language")), _language));
            page.Controls.Add(g);
            return page;
        }

        ComboBox BuildLanguagePicker()
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Margin = new Padding(3, 4, 3, 3) };
            box.Items.Add(I18n.T("lang.auto"));
            foreach (string language in I18n.Languages) box.Items.Add(I18n.NativeName(language));
            int index = Array.IndexOf(I18n.Languages, _settings.Language);
            box.SelectedIndex = index + 1;
            box.SelectedIndexChanged += (s, e) =>
            {
                string chosen = box.SelectedIndex == 0 ? "" : I18n.Languages[box.SelectedIndex - 1];
                if (chosen == _settings.Language) return;
                _settings.Language = chosen;
                SaveSettings();
                if (_runner.Running)
                    MessageBox.Show(this, I18n.T("lang.later"), "LocalExile", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else if (MessageBox.Show(this, I18n.T("lang.restart"), "LocalExile", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Application.Restart();
            };
            return box;
        }

        TabPage BuildSetupTab()
        {
            var page = new TabPage(I18n.T("tab.setup"));
            var g = Grid(3);
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            g.Controls.Add(Caption(I18n.T("setup.clientDir")), 0, 0);
            _clientDir = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 3) };
            g.Controls.Add(_clientDir, 1, 0);
            g.Controls.Add(Btn(I18n.T("setup.browse"), (s, e) => BrowseClient()), 2, 0);

            g.Controls.Add(Caption(I18n.T("setup.mode")), 0, 1);
            _modeCopy = new RadioButton { Text = I18n.T("setup.copy"), AutoSize = true, Checked = true };
            _modeLink = new RadioButton { Text = I18n.T("setup.link"), AutoSize = true };
            var modes = Row(_modeCopy, _modeLink);
            modes.WrapContents = true;
            modes.Dock = DockStyle.Fill;
            g.Controls.Add(modes, 1, 1);
            g.SetColumnSpan(modes, 2);

            g.Controls.Add(Caption(I18n.T("field.account")), 0, 2);
            _setupAccount = Input(false);
            g.Controls.Add(_setupAccount, 1, 2);
            g.Controls.Add(Caption(I18n.T("field.password")), 0, 3);
            _setupPassword = Input(true);
            g.Controls.Add(_setupPassword, 1, 3);
            g.Controls.Add(Caption(I18n.T("field.password2")), 0, 4);
            _setupPassword2 = Input(true);
            g.Controls.Add(_setupPassword2, 1, 4);

            _checkClient = Btn(I18n.T("setup.check"), (s, e) => CheckClient());
            _runSetup = Btn(I18n.T("setup.run"), (s, e) => RunSetup());
            g.Controls.Add(Row(_checkClient, _runSetup), 1, 5);
            var hint = Hint(I18n.T("setup.hint"));
            g.Controls.Add(hint, 0, 6);
            g.SetColumnSpan(hint, 3);
            page.Controls.Add(g);
            return page;
        }

        TabPage BuildModsTab()
        {
            var page = new TabPage(I18n.T("tab.mods"));
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _modList = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, GridLines = true };
            _modList.Columns.Add("Mod", 160);
            _modList.Columns.Add(I18n.T("mods.version"), 70);
            _modList.Columns.Add(I18n.T("mods.effect"), 560);
            layout.Controls.Add(_modList, 0, 0);
            layout.Controls.Add(Row(Btn(I18n.T("mods.save"), (s, e) => SaveMods()), Btn(I18n.T("common.refresh"), (s, e) => RefreshMods()),
                Btn(I18n.T("mods.open"), (s, e) => OpenPath(Cli.ModsDir))), 0, 1);
            layout.Controls.Add(Hint(I18n.T("mods.hint")), 0, 2);
            page.Controls.Add(layout);
            return page;
        }

        TabPage BuildAccountTab()
        {
            var page = new TabPage(I18n.T("tab.account"));
            var g = Grid(4);
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _accountCount = Caption(I18n.T("account.count", "…"));
            var top = Row(_accountCount, Btn(I18n.T("common.refresh"), (s, e) => RefreshAccountCount()));
            g.Controls.Add(top, 0, 0);
            g.SetColumnSpan(top, 4);

            g.Controls.Add(new Label { Text = I18n.T("account.newTitle"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 10, 3, 3) }, 0, 1);
            g.Controls.Add(new Label { Text = I18n.T("account.passwd"), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 10, 3, 3) }, 2, 1);

            g.Controls.Add(Caption(I18n.T("field.account")), 0, 2);
            _newAccount = Input(false); g.Controls.Add(_newAccount, 1, 2);
            g.Controls.Add(Caption(I18n.T("field.account")), 2, 2);
            _pwAccount = Input(false); g.Controls.Add(_pwAccount, 3, 2);

            g.Controls.Add(Caption(I18n.T("field.password")), 0, 3);
            _newPassword = Input(true); g.Controls.Add(_newPassword, 1, 3);
            g.Controls.Add(Caption(I18n.T("field.newPassword")), 2, 3);
            _pwPassword = Input(true); g.Controls.Add(_pwPassword, 3, 3);

            g.Controls.Add(Caption(I18n.T("field.password2")), 0, 4);
            _newPassword2 = Input(true); g.Controls.Add(_newPassword2, 1, 4);
            g.Controls.Add(Caption(I18n.T("field.newPassword2")), 2, 4);
            _pwPassword2 = Input(true); g.Controls.Add(_pwPassword2, 3, 4);

            _createAccount = Btn(I18n.T("account.create"), (s, e) => CreateAccount());
            _changePassword = Btn(I18n.T("account.passwd"), (s, e) => ChangePassword());
            g.Controls.Add(_createAccount, 1, 5);
            g.Controls.Add(_changePassword, 3, 5);

            var hint = Hint(I18n.T("account.hint"));
            g.Controls.Add(hint, 0, 6);
            g.SetColumnSpan(hint, 4);
            page.Controls.Add(g);
            return page;
        }

        TabPage BuildCheckTab()
        {
            var page = new TabPage(I18n.T("tab.check"));
            var g = Grid(1);
            _fullCheck = Btn(I18n.T("check.run"), (s, e) => FullCheck());
            g.Controls.Add(_fullCheck);
            g.Controls.Add(Hint(I18n.T("check.hint")));
            g.Controls.Add(Hint(I18n.T("check.codes") + "\n" + string.Join("\n", Enumerable.Range(0, 9).Select(c => "  " + c + "  " + Cli.ExitCodeText(c)))));
            page.Controls.Add(g);
            return page;
        }

        // ---------------------------------------------------------------- logging and state

        void Log(string text, Color color)
        {
            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = color;
            _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + "\n");
            _log.SelectionColor = _log.ForeColor;
            _log.ScrollToCaret();
        }

        void SetStatus(string text) { _status.Text = text; }

        void SetBusy(string command)
        {
            _runningCommand = command;
            bool busy = command != null;
            _start.Enabled = !busy;
            _runSetup.Enabled = !busy;
            _checkClient.Enabled = !busy;
            _fullCheck.Enabled = !busy;
            _createAccount.Enabled = !busy || command == "run";
            _serversOnly.Enabled = !busy;
            _updateCheck.Enabled = !busy;
            if (!busy) _progress.Style = ProgressBarStyle.Blocks;
        }

        static string Str(Dictionary<string, object> ev, string key)
        {
            object v;
            return ev.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : null;
        }

        void OnEvent(Dictionary<string, object> ev)
        {
            string kind = Str(ev, "event");
            string message = Str(ev, "message") ?? "";
            switch (kind)
            {
                case "started":
                    Log("LocalExile " + Str(ev, "version"), Color.Gray);
                    break;
                case "step":
                    SetStatus(Cli.StepText(Str(ev, "id"), message) + " …");
                    _progress.Style = ProgressBarStyle.Marquee;
                    Log(message, Color.Navy);
                    break;
                case "progress":
                {
                    string current = Str(ev, "current"), total = Str(ev, "total");
                    long c, t;
                    if (current != null && total != null && long.TryParse(current, out c) && long.TryParse(total, out t) && t > 0)
                    {
                        _progress.Style = ProgressBarStyle.Blocks;
                        _progress.Value = (int)Math.Max(0, Math.Min(1000, c * 1000 / t));
                        SetStatus(Cli.StepText(Str(ev, "id"), "") + "  " + message);
                    }
                    Log("  " + message, Color.Black);
                    break;
                }
                case "info":
                    Log("  " + message, Color.Black);
                    break;
                case "warning":
                    Log(I18n.T("event.warning", message), Color.DarkOrange);
                    break;
                case "value":
                    Log(Str(ev, "name") + " = " + Str(ev, "value"), Color.Black);
                    break;
                case "needs-input":
                    Log(I18n.T("event.needsInput", message), Color.DarkOrange);
                    break;
                case "ready":
                    _progress.Style = ProgressBarStyle.Blocks;
                    _progress.Value = 1000;
                    if (Str(ev, "clientPid") != null)
                    {
                        SetStatus(I18n.T("event.clientStarting"));
                        Log(I18n.T("event.clientStarted"), Color.DarkGreen);
                    }
                    else
                    {
                        SetStatus(I18n.T("event.serversReady", Str(ev, "login"), Str(ev, "gateway")));
                        Log(I18n.T("event.serversReadyLog"), Color.DarkGreen);
                    }
                    break;
                case "done":
                    Log(message, Color.DarkGreen);
                    break;
                case "error":
                    Log(I18n.T("event.error", Str(ev, "kind"), message), Color.Firebrick);
                    break;
                default:
                    Log(kind + ": " + message, Color.Gray);
                    break;
            }
        }

        void OnExited(int code)
        {
            string command = _runningCommand;
            bool setup = command == "setup";
            SetBusy(null);
            _progress.Value = code == 0 ? 1000 : 0;
            string what = I18n.T(setup ? "job.setup" : "job.run");
            if (_stopRequested)
            {
                _stopRequested = false;
                _progress.Value = 0;
                SetStatus(I18n.T(setup ? "exit.interruptedStatus" : "exit.stopped"));
                Log(I18n.T(setup ? "exit.interruptedLog" : "exit.stoppedLog"), Color.DarkGreen);
            }
            else if (code == 0)
            {
                SetStatus(I18n.T(setup ? "exit.setupDone" : "exit.ended"));
                Log(I18n.T("exit.endedLog", what), Color.DarkGreen);
            }
            else
            {
                SetStatus(I18n.T("exit.failedStatus", what, Cli.ExitCodeText(code)));
                Log(I18n.T("exit.failedLog", what, code, Cli.ExitCodeText(code)), Color.Firebrick);
                if (code == 7) MessageBox.Show(this, Cli.ExitCodeText(code), "LocalExile", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else if (code == 3) MessageBox.Show(this, Cli.ExitCodeText(code) + "\n\n" + I18n.T("exit.chooseClient"), "LocalExile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            if (_closeAfterStop) Close();
        }

        // ---------------------------------------------------------------- actions

        void FirstLook()
        {
            Log(I18n.T("first.ready", Cli.Root), Color.Gray);
            bool setUp = Directory.Exists(Path.Combine(Cli.Root, "Client")) && File.Exists(Path.Combine(Cli.Root, "data", "extract-stamps.json"));
            if (!setUp)
            {
                Log(I18n.T("first.notSetUp"), Color.DarkOrange);
                SetStatus(I18n.T("first.notSetUpStatus"));
            }
        }

        void StartRun()
        {
            if (_runner.Running) return;
            var args = new List<string> { "run" };
            if (!_updateCheck.Checked) args.Add("--no-update-check");
            if (_serversOnly.Checked) args.Add("--no-client");
            _log.Clear();
            SetBusy("run");
            SetStatus(I18n.T("run.starting"));
            _progress.Value = 0;
            Log(I18n.T(_serversOnly.Checked ? "run.serversOnly" : "run.game"), Color.Navy);
            try { _runner.Start(args, null); }
            catch (Exception ex) { SetBusy(null); Log(I18n.T("run.cannotStart", ex.Message), Color.Firebrick); }
        }

        async void StopAll()
        {
            _stop.Enabled = false;
            SetStatus(I18n.T("stop.stopping"));
            Log(I18n.T("stop.log"), Color.Navy);
            bool hadRunner = _runner.Running;
            try
            {
                if (hadRunner)
                {
                    _stopRequested = true;
                    _runner.Kill();
                    await Task.Delay(1500);
                }
                var stopped = await Task.Run(() => Cli.StopInstall());
                foreach (var name in stopped) Log(I18n.T("stop.ended", name), Color.Black);
                if (!_runner.Running) SetStatus(I18n.T("exit.stopped"));
                if (!hadRunner && stopped.Count == 0) Log(I18n.T("stop.nothing"), Color.Black);
            }
            catch (Exception ex) { Log(I18n.T("stop.failed", ex.Message), Color.Firebrick); }
            finally { _stop.Enabled = true; }
        }

        void BrowseClient()
        {
            using (var dialog = new FolderBrowserDialog { Description = I18n.T("setup.browseTitle") })
            {
                if (Directory.Exists(_clientDir.Text)) dialog.SelectedPath = _clientDir.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) _clientDir.Text = dialog.SelectedPath;
            }
        }

        async void CheckClient()
        {
            string dir = _clientDir.Text.Trim();
            if (dir.Length == 0) { MessageBox.Show(this, I18n.T("setup.needDir"), "LocalExile"); return; }
            _checkClient.Enabled = false;
            try
            {
                var r = await Cli.Capture(Cli.ExePath, new[] { "check", "--client", dir }, null);
                foreach (var line in r.Lines) Log(line, r.Code == 0 ? Color.DarkGreen : Color.Firebrick);
                SetStatus(r.Code == 0 ? I18n.T("setup.clientOk") : Cli.ExitCodeText(r.Code));
                SaveSettings();
            }
            finally { _checkClient.Enabled = _runningCommand == null; }
        }

        async void RunSetup()
        {
            if (_runner.Running) return;
            string dir = _clientDir.Text.Trim();
            string name = _setupAccount.Text.Trim();
            string password = _setupPassword.Text;
            if (name.Length > 0 && !ValidateAccount(name, password, _setupPassword2.Text)) return;
            if (name.Length == 0 && await AccountCount() == 0)
            {
                MessageBox.Show(this, I18n.T("setup.needAccount"), "LocalExile", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool hasClient = Directory.Exists(Path.Combine(Cli.Root, "Client"));
            if (dir.Length == 0 && !hasClient) { MessageBox.Show(this, I18n.T("setup.needDir"), "LocalExile"); return; }
            if (_modeLink.Checked && MessageBox.Show(this, I18n.T("setup.linkWarn"), "LocalExile",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            var args = new List<string> { "setup", "--no-update-check" };
            if (dir.Length > 0) { args.Add("--client"); args.Add(dir); args.Add("--client-mode"); args.Add(_modeLink.Checked ? "link" : "copy"); }
            if (name.Length > 0) { args.Add("--account"); args.Add(name); args.Add("--password-stdin"); }
            SaveSettings();
            _log.Clear();
            SetBusy("setup");
            SetStatus(I18n.T("setup.running"));
            _progress.Value = 0;
            try { _runner.Start(args, name.Length > 0 ? password : null); }
            catch (Exception ex) { SetBusy(null); Log(I18n.T("run.cannotStart", ex.Message), Color.Firebrick); }
            _setupPassword.Clear();
            _setupPassword2.Clear();
        }

        bool ValidateAccount(string name, string password, string again)
        {
            string problem = null;
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_-]{3,32}$")) problem = I18n.T("account.badName");
            else if (password.Length < 4) problem = I18n.T("account.shortPassword");
            else if (password != again) problem = I18n.T("account.mismatch");
            if (problem == null) return true;
            MessageBox.Show(this, problem, "LocalExile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        async Task<int> AccountCount()
        {
            try
            {
                var r = await Cli.Capture(Cli.AuthPath, new[] { "accounts" }, null);
                int n;
                if (r.Code == 0 && r.Lines.Count > 0 && int.TryParse(r.Lines[r.Lines.Count - 1].Trim(), out n)) return n;
            }
            catch (Exception) { }
            return -1;
        }

        async void RefreshAccountCount()
        {
            int n = await AccountCount();
            _accountCount.Text = I18n.T("account.count", n < 0 ? I18n.T("account.readFailed") : n.ToString());
        }

        async void CreateAccount()
        {
            string name = _newAccount.Text.Trim();
            if (!ValidateAccount(name, _newPassword.Text, _newPassword2.Text)) return;
            _createAccount.Enabled = false;
            try
            {
                var r = await Cli.Capture(Cli.ExePath, new[] { "account", "create", name, "--password-stdin" }, _newPassword.Text);
                foreach (var line in r.Lines) Log(line, r.Code == 0 ? Color.DarkGreen : Color.Firebrick);
                if (r.Code == 0)
                {
                    SetStatus(I18n.T("account.created", name));
                    _newPassword.Clear(); _newPassword2.Clear();
                }
                else MessageBox.Show(this, I18n.T("account.createFailed", string.Join("\n", r.Lines)), "LocalExile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshAccountCount();
            }
            finally { _createAccount.Enabled = _runningCommand == null || _runningCommand == "run"; }
        }

        async void ChangePassword()
        {
            string name = _pwAccount.Text.Trim();
            if (!ValidateAccount(name, _pwPassword.Text, _pwPassword2.Text)) return;
            _changePassword.Enabled = false;
            try
            {
                var r = await Cli.Capture(Cli.AuthPath, new[] { "passwd", name, _pwPassword.Text }, null);
                if (r.Code == 0)
                {
                    Log(I18n.T("account.passwdDone", name), Color.DarkGreen);
                    SetStatus(I18n.T(_runningCommand == "run" ? "account.passwdRestart" : "account.passwdStatus"));
                    _pwPassword.Clear(); _pwPassword2.Clear();
                }
                else
                {
                    foreach (var line in r.Lines) Log(line, Color.Firebrick);
                    MessageBox.Show(this, I18n.T("account.passwdFailed", string.Join("\n", r.Lines)), "LocalExile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally { _changePassword.Enabled = true; }
        }

        void RefreshMods()
        {
            _modList.Items.Clear();
            try
            {
                foreach (var m in Mods.Load())
                {
                    var item = new ListViewItem(new[] { m.Id, m.Version, Mods.Describe(m.Id) }) { Checked = m.Enabled, Tag = m };
                    _modList.Items.Add(item);
                }
            }
            catch (Exception ex) { Log(I18n.T("mods.loadFailed", ex.Message), Color.Firebrick); }
        }

        void SaveMods()
        {
            try
            {
                var mods = _modList.Items.Cast<ListViewItem>().Select(i => new ModEntry { Id = i.Text, Enabled = i.Checked }).ToList();
                Mods.Save(mods);
                string on = string.Join(", ", mods.Where(m => m.Enabled).Select(m => m.Id));
                Log(I18n.T("mods.saved", on.Length > 0 ? on : I18n.T("common.none")), Color.DarkGreen);
                SetStatus(I18n.T(_runningCommand == "run" ? "mods.savedRunning" : "mods.savedStatus"));
            }
            catch (Exception ex) { Log(I18n.T("mods.saveFailed", ex.Message), Color.Firebrick); }
        }

        async void FullCheck()
        {
            _fullCheck.Enabled = false;
            _log.Clear();
            int problems = 0;
            Action<bool, string, string> report = (ok, what, fix) =>
            {
                if (ok) Log("✔ " + what, Color.DarkGreen);
                else { problems++; Log("✘ " + what + (fix != null ? "  —  " + fix : ""), Color.Firebrick); }
            };
            try
            {
                var version = await Cli.Capture(Cli.ExePath, new[] { "version" }, null);
                report(version.Code == 0, "LocalExile " + string.Join(" ", version.Lines), null);

                string client = Path.Combine(Cli.Root, "Client");
                if (Directory.Exists(client))
                {
                    var check = await Cli.Capture(Cli.ExePath, new[] { "check" }, null);
                    report(check.Code == 0, I18n.T("check.client", client, check.Code == 0 ? "3.29.3b" : Cli.ExitCodeText(check.Code)),
                        check.Code == 0 ? null : I18n.T("check.redoSetup"));
                }
                else report(false, I18n.T("check.noClient"), I18n.T("check.noClientFix"));

                string data = Path.Combine(Cli.Root, "data");
                report(File.Exists(Path.Combine(data, "extract-stamps.json")) && File.Exists(Path.Combine(data, "game-files.zip")),
                    I18n.T("check.data"), I18n.T("check.runSetup"));
                report(File.Exists(Path.Combine(data, "login-keys.json")), I18n.T("check.keys"), I18n.T("check.runSetup"));
                report(File.Exists(Path.Combine(client, "Game.patched.exe")), I18n.T("check.patched"), I18n.T("check.patchedFix"));

                int accounts = await AccountCount();
                report(accounts > 0, I18n.T("account.count", accounts < 0 ? I18n.T("account.readFailed") : accounts.ToString()), I18n.T("check.accountsFix"));

                string on = string.Join(", ", Mods.Load().Where(m => m.Enabled).Select(m => m.Id));
                Log(I18n.T("check.mods", on.Length > 0 ? on : I18n.T("common.none")), Color.Black);

                Log(problems == 0 ? I18n.T("check.allOk") : I18n.T("check.problems", problems), problems == 0 ? Color.DarkGreen : Color.Firebrick);
                SetStatus(problems == 0 ? I18n.T("check.okStatus") : I18n.T("check.problemsStatus", problems));
            }
            catch (Exception ex) { Log(I18n.T("check.failed", ex.Message), Color.Firebrick); }
            finally { _fullCheck.Enabled = _runningCommand == null; }
        }

        static void OpenPath(string path)
        {
            try
            {
                if (!path.StartsWith("http") && !File.Exists(path)) Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception) { }
        }

        void SaveSettings()
        {
            _settings.Client = _clientDir.Text.Trim();
            _settings.UpdateCheck = _updateCheck.Checked;
            _settings.ServersOnly = _serversOnly.Checked;
            _settings.Save();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            if (_runner.Running && !_closeAfterStop)
            {
                string question = I18n.T(_runningCommand == "setup" ? "close.setup" : "close.run");
                if (MessageBox.Show(this, question, "LocalExile", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _closeAfterStop = true;
                e.Cancel = true;
                StopAll();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
