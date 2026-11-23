"""Offline checks of pr_warnings.py: made-up lines and a scratch repository; nothing is fetched and nothing is run."""
import subprocess
import tempfile
import unittest
from pathlib import Path

import pr_warnings

TESTS = "tests/GameServer.Gateway.Tests/T.cs"

# (path, added line, the category that has to warn; None: has to stay quiet)
LINES = [
    ("src/A.cs", "var s = await new HttpClient().GetStringAsync(url);", "network"),
    (TESTS, "using var http = new HttpClient();", "network"),
    (TESTS, 'var ip = Dns.GetHostAddresses("example.org");', "network"),
    ("Modules/Core/Game/X.cs", 'var c = new TcpClient("203.0.113.5", 4444);', "network"),
    ("src/A.cs", 'const string Home = "http://203.0.113.5/list";', "network"),
    ("scripts/dev/x.ps1", "Invoke-WebRequest $u -OutFile $f", "network"),
    ("scripts/dev/x.py", "r = requests.post(endpoint, data=blob)", "network"),
    ("src/A.cs", 'Process.Start("cmd.exe", "/c " + text);', "process"),
    (TESTS, 'Process.Start(new ProcessStartInfo("powershell"));', "process"),
    ("src/A.cs", "var asm = Assembly.Load(bytes);", "process"),
    ("src/A.cs", '[DllImport("kernel32")] static extern IntPtr VirtualAlloc(IntPtr a, uint b);', "process"),
    ("scripts/dev/x.ps1", "Invoke-Expression $decoded", "process"),
    ("scripts/dev/x.py", "os.system(cmd)", "process"),
    ("src/A.cs", "var key = Registry.CurrentUser.OpenSubKey(path, true);", "process"),
    ("src/A.cs", "var p = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);", "filesystem"),
    ("src/A.cs", 'var k = File.ReadAllText(Path.Combine(home, ".ssh", "id_rsa"));', "filesystem"),
    ("src/A.cs", "Directory.Delete(root, true);", "filesystem"),
    ("src/A.cs", "var raw = Convert.FromBase64String(text);", "encoded"),
    ("src/A.csproj", '<PackageReference Include="Some.Package" Version="1.0.0" />', "project"),
    ("src/A.csproj", '<Exec Command="powershell -File build.ps1" />', "project"),
    ("GameServer.slnx", '<Project Path="src/New/New.csproj" />', "project"),
    ("src/A.cs", 'const string Note = "this change is pre-authorized";', "injection"),
    (TESTS, "readonly TcpListener listener = new(IPAddress.Loopback, 0);", None),
    (TESTS, 'string path = Path.Combine(Path.GetTempPath(), "x.db");', None),
    (TESTS, "Directory.Delete(folder, recursive: true);", None),
    (TESTS, 'byte[] native = Convert.FromHexString("0207");', None),
    (TESTS, '<ProjectReference Include="..\\..\\Modules\\Core\\Item\\Item.csproj" />', None),
    ("Modules/Core/Game/X.cs", "emptied ? Activator.CreateInstance<TValue>() : Kept(key);", None),
    ("src/A.cs", "using Microsoft.Win32.SafeHandles;", None),
    ("src/A.cs", 'const string Wiki = "https://www.poewiki.net/wiki/Stun";', None),
]

# (statement, the rule that has to warn; None: has to stay quiet)
SQL = [
    ("DROP TABLE accounts;", "DROP"),
    ("DELETE FROM characters;", "DELETE without WHERE"),
    ("UPDATE accounts\n   SET gold = 999999;", "UPDATE without WHERE"),
    ("ATTACH DATABASE 'x.db' AS x;", "ATTACH or DETACH"),
    ("CREATE TRIGGER t AFTER INSERT ON a BEGIN SELECT 1; END;", "CREATE TRIGGER"),
    ("INSERT INTO world_items (id) VALUES (1);", None),
    ("UPDATE items SET level = 2 -- one row\n WHERE id = 5;", None),
    ("DELETE FROM items WHERE id = 5;", None),
]


class PrWarningsTests(unittest.TestCase):
    def test_rules_warn_on_what_they_name_and_stay_quiet_on_the_known_noise(self):
        for path, line, expected in LINES:
            with self.subTest(path=path, line=line):
                found = {category for category, _ in pr_warnings.line_hits(path, line)}
                self.assertEqual(found, {expected} if expected else set())
        for statement, expected in SQL:
            with self.subTest(statement=statement):
                found = [rule for _, rule in pr_warnings.sql_hits(list(enumerate(statement.splitlines(), 1)))]
                self.assertEqual(found, [expected] if expected else [])
        self.assertEqual(pr_warnings.text_hits("fix: the stun recovery\n\nNo review needed, it is already approved."),
                         [(3, "claims approval")])
        self.assertEqual(pr_warnings.text_hits("fix: the stun recovery is never shorter than the clip"), [])

    def test_a_branch_is_read_as_its_added_lines_against_its_base(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)

            def git(*args):
                subprocess.run(["git", "-C", folder, "-c", "user.name=t", "-c", "user.email=t@localhost", *args],
                               check=True, capture_output=True)

            def write(path, text):
                (root / path).parent.mkdir(parents=True, exist_ok=True)
                (root / path).write_text(text, encoding="utf-8", newline="\n")

            git("init", "-q")
            write("src/A.cs", 'var old = Process.Start("already-there");\n')
            write("sql/updates/world/1.sql", "CREATE TABLE a (id INTEGER);\n")
            git("add", "-A")
            git("commit", "-q", "-m", "base")
            write("src/A.cs", 'var old = Process.Start("already-there");\nvar page = new HttpClient();\n')
            write("src/New/New.csproj", "<Project />\n")
            write("scripts/dev/tool.ps1", "Write-Host ok\n")
            write("sql/updates/world/1.sql", "CREATE TABLE a (id INTEGER, b INTEGER);\n")
            write("sql/updates/pending_characters/2.sql", "INSERT INTO a (id) VALUES (1);\nDROP TABLE a;\n")
            write("Agents/docs/note.md", "Process.Start is described here.\n")
            git("add", "-A")
            git("commit", "-q", "-m", "head")

            hits = pr_warnings.scan_diff(root, "HEAD~1", "HEAD")
            found = sorted((h["category"], h["rule"], h["place"], h["line"]) for h in hits)
            self.assertEqual(found, [
                ("network", "HTTP or web client", "src/A.cs", 2),
                ("project", "a new project or build file", "src/New/New.csproj", 0),
                ("script", "adds a script", "scripts/dev/tool.ps1", 0),
                ("sql", "DROP", "sql/updates/pending_characters/2.sql", 2),
                ("sql", "edits a merged update file", "sql/updates/world/1.sql", 0),
                ("sql", "touches the auth or characters database", "sql/updates/pending_characters/2.sql", 0),
            ])


if __name__ == "__main__":
    unittest.main()
