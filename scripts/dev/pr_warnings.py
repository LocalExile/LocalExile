#!/usr/bin/env python3
"""List what a reviewer has to look at by hand in a pull request (Agents/docs/dev/pr-checks.md, "Reviewer warnings").

  pr_warnings.py 852                             one pull request
  pr_warnings.py --all                           every open pull request that is not a draft (--drafts adds those)
  pr_warnings.py --base origin/main --head HEAD  a local branch: the diff only

It only reads: the lines the diff adds and, for a pull request, its title, description and commit messages. Nothing
from the pull request is checked out or run, and nothing is posted. The report names a rule and a place, never the
matched text, unless --show-lines asks for the line, cut short. It warns; it does not judge: a quiet report does not
show that a change is safe. A pull request that cannot be read is named and the others are still read.
Exit code: 0 nothing to report, 2 something to look at, 1 something could not be read.
"""
import argparse
import importlib.util
import json
import re
import subprocess
import sys
import time
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
REPO = "LocalExile/LocalExile"
CATEGORIES = ("project", "network", "process", "filesystem", "encoded", "sql", "injection", "pr-text", "binary",
              "script", "repository")


def load_scan_docs():
    """The docs scan's rules, so the phrasing, hidden-character and allowed-host lists exist once."""
    spec = importlib.util.spec_from_file_location("scan_docs", ROOT / ".github" / "scripts" / "scan-docs.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


scan_docs = load_scan_docs()

DOC_EXTENSIONS = (".md", ".txt", ".rst")
SCRIPT_EXTENSIONS = (".ps1", ".psm1", ".sh", ".bat", ".cmd", ".py", ".js", ".mjs", ".cjs")
PROJECT_FILE = re.compile(r"(\.csproj|\.sln|\.slnx|\.vcxproj|\.props|\.targets|(^|/)nuget\.config|(^|/)global\.json|"
                          r"(^|/)package(-lock)?\.json|(^|/)requirements[^/]*\.txt|(^|/)Dockerfile)$", re.IGNORECASE)


def rules(pairs, flags=0):
    return [(re.compile(pattern, flags), name) for pattern, name in pairs]


# A reference between two projects already in the repository is not listed: only what brings code in or runs it.
PROJECT_LINE = rules([
    (r"<PackageReference\b|<PackageVersion\b", "adds a package"),
    (r"<Reference\b|<Analyzer\b", "adds an assembly reference"),
    (r"<Exec\b|<Target\b|<UsingTask\b|<Import\b", "adds a build step"),
    (r"<Project\s+Path=", "adds a project to the solution"),
], re.IGNORECASE)

NETWORK = rules([
    (r"\b(HttpClient|HttpClientHandler|SocketsHttpHandler|WebClient|HttpWebRequest|WebRequest|HttpRequestMessage|"
     r"FtpWebRequest|SmtpClient|ClientWebSocket)\b", "HTTP or web client"),
    (r"\.(Get|Post|Put|Patch|GetString|GetStream|GetByteArray)Async\(|\bDownload(File|String|Data)(Async|TaskAsync)?\(",
     "web request call"),
    (r"\b(TcpClient|UdpClient)\b|\.ConnectAsync\(|\.Connect\(", "outbound connection"),
    (r"\bDns\.(GetHost|Resolve)\w*", "DNS lookup"),
    (r"\b(HttpListener|TcpListener)\b|\.Listen\(", "opens a listener"),
    (r"\b(Invoke-WebRequest|Invoke-RestMethod|Start-BitsTransfer)\b|\b(iwr|irm)\s|Net\.WebClient|\bcurl(\.exe)?\s|"
     r"\bwget\s", "download from a script"),
    (r"\brequests\.(get|post|put|delete|request|Session)\b|urllib\.request|\burlopen\(|http\.client|"
     r"socket\.(socket|create_connection)|\b(aiohttp|httpx)\b", "Python network call"),
    (r"\bfetch\(|XMLHttpRequest|\baxios\b|require\(['\"](https?|net|dgram|tls)['\"]\)", "JavaScript network call"),
])

PROCESS = rules([
    (r"\bProcess\.Start\b|\bProcessStartInfo\b|\bStart-Process\b", "starts a process"),
    (r"\bInvoke-Expression\b|\biex\s|\bAdd-Type\b|Set-ExecutionPolicy|-EncodedCommand|powershell(\.exe)?\s+-e(nc)?\b",
     "runs text as PowerShell"),
    (r"\bcmd(\.exe)?\s*/c\b|\bos\.system\(|\bsubprocess\.|child_process|\bshell\s*=\s*True", "runs a shell command"),
    (r"\bAssembly\.(Load|LoadFrom|LoadFile|UnsafeLoadFrom)\b|\bAssemblyLoadContext\b|\bCSharpScript\b|"
     r"\bCSharpCompilation\b|Reflection\.Emit|\bActivator\.CreateInstance\(", "loads or builds code at run time"),
    (r"\bDllImport\b|\bLibraryImport\b|\bNativeLibrary\.|VirtualAlloc|VirtualProtect|WriteProcessMemory|"
     r"CreateRemoteThread|\bOpenProcess\b|GetDelegateForFunctionPointer", "calls native code"),
    (r"(?<![\w.])eval\(|(?<![\w.])exec\(", "eval or exec"),
    (r"\bschtasks\b|New-ScheduledTask|Register-ScheduledTask|\bsc(\.exe)?\s+create\b|CurrentVersion\\\\?Run\b|"
     r"\bRegistry(Key)?\.|Microsoft\.Win32\.Registry", "registry or scheduled task"),
])

FILESYSTEM = rules([
    (r"Environment\.GetFolderPath|SpecialFolder\.|%(APPDATA|USERPROFILE|LOCALAPPDATA)%|"
     r"\$env:(APPDATA|USERPROFILE|LOCALAPPDATA|HOME)\b|os\.path\.expanduser", "reaches the user's profile"),
    (r"(?<![\w-])\.ssh\b|id_rsa|\.aws\b|\.gitconfig|\.git-credentials|login-keys\.json|instance-secret\.bin|"
     r"\b(GITHUB_TOKEN|GH_TOKEN|API_KEY|SECRET_KEY)\b", "names a key or credential file"),
    (r"\bDirectory\.Delete\(|Remove-Item\b[^\n]*-Recurse|\brm\s+-rf?\b|\brmdir\s+/s\b|shutil\.rmtree|Format-Volume",
     "deletes a folder"),
    (r"\bFile\.Delete\(|\bos\.remove\(|\bunlink\(", "deletes a file"),
    (r"(\.\.[\\/]){3,}", "walks up three or more folders"),
])

ENCODED = rules([
    (r"FromBase64String|FromBase64CharArray|\batob\(|b64decode|\[Convert\]::FromBase64", "decodes text at run time"),
])

LINE_TABLES = (("network", NETWORK), ("process", PROCESS), ("filesystem", FILESYSTEM), ("encoded", ENCODED))

# Loopback sockets and the clean-up of temporary files are how the tests work; outside tests/ they still warn.
QUIET_IN_TESTS = {"outbound connection", "opens a listener", "deletes a folder", "deletes a file"}

SQL_STATEMENT = rules([
    (r"^\s*DROP\s+(TABLE|INDEX|VIEW|TRIGGER)\b", "DROP"),
    (r"^\s*TRUNCATE\b", "TRUNCATE"),
    (r"^\s*(ATTACH|DETACH)\b", "ATTACH or DETACH"),
    (r"^\s*PRAGMA\b", "PRAGMA"),
    (r"^\s*CREATE\s+(TEMP\s+|TEMPORARY\s+)?TRIGGER\b", "CREATE TRIGGER"),
    (r"^\s*ALTER\s+TABLE\b[^;]*\b(DROP|RENAME)\b", "ALTER TABLE drops or renames"),
    (r"\bload_extension\b|\bsqlite_master\b|\bsqlite_schema\b|\bwritable_schema\b", "touches SQLite internals"),
], re.IGNORECASE | re.DOTALL)


def is_comment(path, line):
    stripped, lower = line.lstrip(), path.lower()
    if lower.endswith((".cs", ".js", ".mjs", ".cjs")):
        return stripped.startswith(("//", "*", "/*"))
    if lower.endswith((".ps1", ".psm1", ".py", ".sh", ".yml", ".yaml")):
        return stripped.startswith("#")
    return False


def line_hits(path, line):
    """(category, rule) for one added line of a file that is neither a doc nor SQL."""
    found = []
    if PROJECT_FILE.search(path):
        found += [("project", name) for pattern, name in PROJECT_LINE if pattern.search(line)]
    in_tests = path.startswith("tests/")
    for category, table in LINE_TABLES:
        for pattern, name in table:
            if not (in_tests and name in QUIET_IN_TESTS) and pattern.search(line):
                found.append((category, name))
    if not is_comment(path, line) and not all(scan_docs.host_allowed(url)[0] for url in scan_docs.URL.findall(line)):
        found.append(("network", "an address outside the docs scan's allowed hosts"))
    # Phrasing aimed at an agent where the docs scan does not read: string literals and data files.
    covered = scan_docs.data_portion(path, line)
    for pattern, name in scan_docs.PHRASES:
        if name == "runs or decodes text":      # in code that is the process and encoded rules above
            continue
        if pattern.search(line) and not (covered and pattern.search(covered)):
            found.append(("injection", name))
    return list(dict.fromkeys(found))


def file_hits(path, kind, is_binary):
    """(category, rule) for a changed file as a whole; kind is git's A, M or D."""
    found, lower = [], path.lower()
    if kind == "A" and PROJECT_FILE.search(path):
        found.append(("project", "a new project or build file"))
    if kind == "A" and (is_binary or lower.endswith(scan_docs.BINARY_EXTENSIONS)):
        found.append(("binary", "adds a binary file"))
    if kind == "A" and lower.endswith(SCRIPT_EXTENSIONS) and not path.startswith("tests/"):
        found.append(("script", "adds a script"))
    if path.startswith(".github/"):
        found.append(("repository", "changes a file under .github/"))
    if path.startswith("sql/"):
        if path.startswith("sql/base/"):
            found.append(("sql", "changes sql/base/"))
        elif "/pending_" not in path and kind != "A":
            found.append(("sql", "edits a merged update file"))
        if re.search(r"/(pending_)?(auth|characters)/", path):
            found.append(("sql", "touches the auth or characters database"))
    return found


def sql_hits(lines):
    """(line number, rule) for the statements in one .sql file's added lines, given as (number, text)."""
    text, starts = "", []
    for number, line in lines:
        starts.append((len(text), number))
        text += re.sub(r"--.*$", "", line) + "\n"
    found, position = [], 0
    for statement in text.split(";"):
        begin = position + len(statement) - len(statement.lstrip())
        position += len(statement) + 1
        statement = statement.strip()
        if not statement:
            continue
        number = max((n for offset, n in starts if offset <= begin), default=0)
        found += [(number, name) for pattern, name in SQL_STATEMENT if pattern.search(statement)]
        has_where = re.search(r"\bWHERE\b", statement, re.IGNORECASE)
        if re.match(r"DELETE\s+FROM\b", statement, re.IGNORECASE) and not has_where:
            found.append((number, "DELETE without WHERE"))
        if re.match(r"UPDATE\b", statement, re.IGNORECASE) and not has_where:
            found.append((number, "UPDATE without WHERE"))
    return found


def text_hits(text):
    """(line number, rule) for a title, a description or a commit message: what a reviewing agent reads as prose."""
    found = []
    for number, line in enumerate(text.splitlines(), 1):
        if scan_docs.hidden_characters(line, False):
            found.append((number, "hidden characters"))
        if scan_docs.HIDDEN_MARKUP.search(line):
            found.append((number, "markup that does not render"))
        found += [(number, name) for pattern, name in scan_docs.PHRASES if pattern.search(line)]
    return found


def run(command, **options):
    try:
        return subprocess.run(command, capture_output=True, **options)
    except FileNotFoundError:
        raise RuntimeError(f"{command[0]} is not installed or not on PATH") from None


def git(root, *args, text=True):
    result = run(["git", "-C", str(root), *args])
    if result.returncode != 0:
        raise RuntimeError(f"git {' '.join(args[:3])}: {result.stderr.decode('utf-8', 'replace').strip()[:300]}")
    return result.stdout.decode("utf-8", "replace") if text else result.stdout


def gh(*args):
    for attempt in range(4):
        result = run(["gh", *args], text=True, encoding="utf-8", errors="replace")
        if result.returncode == 0 or "dial tcp" not in result.stderr:
            break
        time.sleep(5 * (attempt + 1))
    if result.returncode != 0:
        raise RuntimeError(f"gh {' '.join(args[:3])}: {result.stderr.strip()[:300]}")
    return result.stdout


def hit(category, rule, place, line=0, text=""):
    return {"category": category, "rule": rule, "place": place, "line": line,
            "text": re.sub(r"[^\x20-\x7e]", "?", text.strip())[:150]}


def scan_diff(root, base, head):
    """The warnings for what head adds over base, two commits already in the repository at root."""
    quiet = ("-c", "core.quotePath=false")
    status = dict(reversed(row.split("\t", 1)) for row in
                  git(root, *quiet, "diff", "--name-status", "--no-renames", base, head).splitlines())
    binary = {row.split("\t", 2)[2] for row in git(root, *quiet, "diff", "--numstat", "--no-renames", base,
                                                    head).splitlines() if row.startswith("-\t")}
    hits = [hit(category, rule, path) for path, kind in status.items()
            for category, rule in file_hits(path.strip('"'), kind, path in binary)]
    diff = git(root, *quiet, "diff", "-a", "--unified=0", "--no-color", "--no-renames", "--no-ext-diff",
               "--no-textconv", "--diff-filter=d", base, head, text=False)
    sql = defaultdict(list)
    for path, number, raw in scan_docs.added_lines(diff):
        lower = path.lower()
        if lower.endswith(scan_docs.BINARY_EXTENSIONS) or path in binary or b"\0" in raw:
            continue
        if lower.endswith(DOC_EXTENSIONS) or any(part in path for part in scan_docs.NO_TEXT_RULES):
            continue    # the docs scan reads the docs; generated data is hashes
        line = raw.decode("utf-8", "replace").removesuffix("\r")
        if lower.endswith(".sql"):
            sql[path].append((number, line))
        else:
            hits += [hit(category, rule, path, number, line) for category, rule in line_hits(path, line)]
    for path, lines in sql.items():
        by_number = dict(lines)
        hits += [hit("sql", rule, path, number, by_number.get(number, "")) for number, rule in sql_hits(lines)]
    return hits


def scan_pull_request(root, repo, pr, open_heads, main):
    """Fetch one pull request's head and read it against main, or against the open pull request it is stacked on."""
    number = pr["number"]
    git(root, "fetch", "-q", "--no-tags", f"https://github.com/{repo}", f"pull/{number}/head")
    head = git(root, "rev-parse", "FETCH_HEAD").strip()
    base, against = git(root, "merge-base", main, head).strip(), "main " + main[:9]
    own = [commit["oid"] for commit in pr["commits"]]
    below = [(own.index(oid), other) for oid, other in open_heads.items() if oid in own and other != number]
    if below:   # so a warning shows once, on the pull request that adds it
        index, other = max(below)
        base, against = own[index], f"#{other}"
    hits = scan_diff(root, base, head)
    words = [("title", pr["title"]), ("description", pr.get("body") or "")]
    words += [(f"commit {c['oid'][:9]}", c["messageHeadline"] + "\n" + (c.get("messageBody") or ""))
              for c in pr["commits"]]
    for place, text in words:
        hits += [hit("pr-text", rule, place, line) for line, rule in text_hits(text)]
    return {"number": number, "author": pr["author"]["login"], "draft": pr["isDraft"], "head": head[:9],
            "against": against, "hits": hits}


def report(results, show_lines):
    out, clean, unread = [], [], []
    for result in results:
        name = f"#{result['number']}" if result.get("number") else "the branch"
        if result.get("error"):
            unread.append(f"  {name}: {result['error']}")
            continue
        if not result["hits"]:
            clean.append(name)
            continue
        by = f" by {scan_docs.safe(result['author'])}" if result.get("author") else ""
        draft = " (draft)" if result.get("draft") else ""
        out.append(f"{name}{by}{draft}: head {result['head']} read against {result['against']}")
        for category in CATEGORIES:
            grouped = defaultdict(list)
            for h in result["hits"]:
                if h["category"] == category:
                    grouped[(h["place"], h["rule"])].append(h)
            if grouped:
                out.append(f"  {category}")
            for (place, rule), found in grouped.items():
                numbers = [str(h["line"]) for h in found if h["line"]]
                where = scan_docs.safe(place) + (":" + ",".join(numbers[:8]) if numbers else "")
                more = f" and {len(numbers) - 8} more lines" if len(numbers) > 8 else ""
                out.append(f"    {where}: {rule}{more}")
                if show_lines:
                    out += [f"        {h['line']}: {h['text']}" for h in found[:8] if h["text"]]
        out.append("")
    if clean:
        out.append("No warnings: " + " ".join(clean))
    if unread:
        out += ["Could not read:"] + unread
    return "\n".join(out)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("numbers", nargs="*", type=int, help="pull request numbers")
    parser.add_argument("--all", action="store_true", help="every open pull request that is not a draft")
    parser.add_argument("--drafts", action="store_true", help="with --all: the drafts too")
    parser.add_argument("--base", help="with --head: read a local branch against this commit")
    parser.add_argument("--head", help="with --base: the commit to read")
    parser.add_argument("--repo", default=REPO, help="owner/name on GitHub (default: %(default)s)")
    parser.add_argument("--show-lines", action="store_true", help="add each matched line, cut short")
    parser.add_argument("--json", action="store_true", help="print the results as JSON")
    parser.error = lambda message: parser.exit(1, f"{parser.prog}: {message}\n")    # 2 means warnings here
    args = parser.parse_args(argv)

    if (args.base or args.head) and (not (args.base and args.head) or args.numbers or args.all):
        parser.error("--base and --head go together, without pull request numbers or --all")
    if not args.base and bool(args.numbers) == args.all:
        parser.error("give pull request numbers, or --all, or --base with --head")
    try:
        if args.base:
            head = git(ROOT, "rev-parse", args.head).strip()
            base = git(ROOT, "merge-base", args.base, head).strip()
            results = [{"head": head[:9], "against": base[:9], "hits": scan_diff(ROOT, base, head)}]
        else:
            listing = json.loads(gh("pr", "list", "--repo", args.repo, "--state", "open", "--limit", "500", "--json",
                                    "number,isDraft,headRefOid"))
            open_heads = {pr["headRefOid"]: pr["number"] for pr in listing}
            numbers = args.numbers or sorted(pr["number"] for pr in listing if args.drafts or not pr["isDraft"])
            git(ROOT, "fetch", "-q", "--no-tags", f"https://github.com/{args.repo}", "main")
            main_commit = git(ROOT, "rev-parse", "FETCH_HEAD").strip()
            results = []
            for number in numbers:
                try:    # one pull request that cannot be read does not stop the others
                    pr = json.loads(gh("pr", "view", str(number), "--repo", args.repo, "--json",
                                       "number,title,body,author,isDraft,headRefOid,commits"))
                    results.append(scan_pull_request(ROOT, args.repo, pr, open_heads, main_commit))
                except RuntimeError as error:
                    results.append({"number": number, "hits": [], "error": scan_docs.safe(str(error))})
    except RuntimeError as error:
        print("pr_warnings: " + scan_docs.safe(str(error)), file=sys.stderr)
        return 1

    if not args.show_lines:
        for result in results:
            for h in result["hits"]:
                h["text"] = ""
    print(json.dumps(results, indent=1) if args.json else report(results, args.show_lines))
    if any(result.get("error") for result in results):
        return 1
    return 2 if any(result["hits"] for result in results) else 0


if __name__ == "__main__":
    sys.exit(main())
