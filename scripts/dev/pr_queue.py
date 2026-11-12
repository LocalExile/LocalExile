#!/usr/bin/env python3
"""Where every open pull request stands (Agents/docs/dev/pr-checks.md, "Labels and claiming").

  pr_queue.py            every open pull request that is not a draft (--drafts adds those)
  pr_queue.py 736 741    only these

For each one: whether it really conflicts with main (the merge is tried in memory, with main's rules for the list
files), which open pull request it is stacked on, which protected paths it changes and who owns them, and which of
the labels needs-author, BLOCKED and protected-files that adds up to, next to the labels it has. Nothing clears a
label by itself, so it ends with the labels to add and the ones nothing was found for, each with its reason: for
needs-author, whether the author has pushed or replied since it was set. It only reads: nothing is checked out,
merged, labelled or posted. Needs Git 2.41 or newer. Exit code: 0, or 1 when something could not be read.
"""
import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))    # python -I leaves the script's folder out
from pr_warnings import REPO, ROOT, gh, git, run, scan_docs  # noqa: E402

LABELS = ("needs-author", "BLOCKED", "protected-files")
FAILED = ("FAILURE", "TIMED_OUT", "CANCELLED", "ACTION_REQUIRED", "STARTUP_FAILURE", "ERROR")


def owner_rules(text):
    """The patterns of a CODEOWNERS file as (pattern, regex, owners), in the file's order."""
    found = []
    for raw in text.splitlines():
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue
        pattern, *owners = line.split()
        body = pattern.strip("/")
        anchored = pattern.startswith("/") or "/" in body
        regex = "".join(".*" if part == "**" else "[^/]*" if part == "*" else re.escape(part)
                        for part in re.split(r"(\*\*|\*)", body))
        found.append((pattern, re.compile(("^" if anchored else "^(?:.*/)?") + regex + "(?:/.*)?$"),
                      [owner.lstrip("@") for owner in owners]))
    return found


def owners_of(path, rules):
    """(pattern, owners) of the last pattern that matches, as GitHub reads the file; None when nothing does."""
    hit = None
    for pattern, regex, owners in rules:
        if regex.match(path):
            hit = (pattern, owners)
    return hit


def merge_conflicts(root, main, head):
    """The files that conflict when head is merged into main, tried in memory with main's merge rules."""
    result = run(["git", "-C", str(root), "-c", "core.quotePath=false", f"--attr-source={main}", "merge-tree",
                  "--write-tree", "--name-only", "--no-messages", main, head])
    if result.returncode not in (0, 1):
        raise RuntimeError("git merge-tree: " + result.stderr.decode("utf-8", "replace").strip()[:300])
    return [] if result.returncode == 0 else result.stdout.decode("utf-8", "replace").splitlines()[1:]


def check_git(root, main):
    """Stop with a plain message on a Git without --attr-source (2.41) or merge-tree --write-tree (2.38)."""
    probe = run(["git", "-C", str(root), f"--attr-source={main}", "merge-tree", "--write-tree", main, main])
    if probe.returncode != 0:
        version = run(["git", "--version"]).stdout.decode("utf-8", "replace").strip()
        raise RuntimeError(f"pr_queue.py needs Git 2.41 or newer; this is {version}")


class Queue:
    """What Git knows about each pull request: pulls maps a number to at least {"head": commit}."""

    def __init__(self, root, main, pulls):
        self.root, self.main, self.pulls, self.read = root, main, pulls, {}
        self.rules = owner_rules(git(root, "show", f"{main}:.github/CODEOWNERS"))
        self.by_head = {pull["head"]: number for number, pull in pulls.items()}

    def entry(self, number):
        if number not in self.read:
            self.read[number] = self.look(number)
        return self.read[number]

    def look(self, number):
        head = self.pulls[number]["head"]
        commits = git(self.root, "rev-list", f"{self.main}..{head}").split()
        # The nearest open pull request whose head this one contains: the one it waits for.
        stacked_on = next((self.by_head[oid] for oid in commits if self.by_head.get(oid, number) != number), None)
        base = git(self.root, "merge-base", self.main, head).strip()
        protected = {}
        for path in git(self.root, "-c", "core.quotePath=false", "diff", "--name-only", "--no-renames", base,
                        head).splitlines():
            hit = owners_of(path, self.rules)
            if hit:
                protected.setdefault(hit[0], {"owners": hit[1], "files": []})["files"].append(path)
        conflicts = merge_conflicts(self.root, self.main, head)
        # The same files as the pull request below it: fixed there, not here.
        inherited = bool(conflicts) and stacked_on is not None and set(conflicts) <= set(
            self.entry(stacked_on)["conflicts"])
        return {"conflicts": conflicts, "inherited": inherited, "stackedOn": stacked_on, "protected": protected}


def moves_since_label(rows, author, label="needs-author"):
    """What the author did after the label was last set. rows: (event, time, label, login) from the timeline."""
    set_at = max((time for event, time, name, _ in rows if event == "labeled" and name == label), default=None)
    if set_at is None:
        return None
    later = [(event, login) for event, time, _, login in rows if time > set_at]
    return {"set": set_at[:10],
            "pushed": any(event == "committed" or event == "head_ref_force_pushed" and login == author
                          for event, login in later),
            "replied": any(event == "commented" and login == author for event, login in later)}


def timeline_rows(repo, number):
    """(event, time, label, login) for a pull request's timeline; a commit's time is its committer date."""
    lines = gh("api", f"repos/{repo}/issues/{number}/timeline", "--paginate", "--jq",
               '.[] | [.event, (.created_at // .committer.date // ""), (.label.name // ""),'
               ' (.actor.login // .user.login // "")] | @tsv').splitlines()
    return [tuple((line.split("\t") + ["", "", "", ""])[:4]) for line in lines if line]


def wanted_labels(entry, pull):
    """The labels the facts add up to, each with its reasons."""
    wanted = {}
    if entry["conflicts"] and not entry["inherited"]:
        wanted.setdefault("needs-author", []).append("conflicts with main")
    if pull.get("review") == "CHANGES_REQUESTED":
        wanted.setdefault("needs-author", []).append("changes requested")
    if pull.get("failing"):
        wanted.setdefault("needs-author", []).append("failing check")
    if entry["stackedOn"] is not None:
        wanted["BLOCKED"] = [f"stacked on #{entry['stackedOn']}"]
    if entry["protected"]:
        wanted["protected-files"] = ["changes a protected path"]
    return wanted


def report(results):
    safe, out, waiting_on_nothing, list_files_only, unread = scan_docs.safe, [], [], [], []
    to_add, stale = [], []
    for result in results:
        name = f"#{result['number']}"
        if result.get("error"):
            unread.append(f"  {name}: {result['error']}")
            continue
        has = [label for label in result["labels"] if label in LABELS]
        wanted = list(result["wanted"])
        if result["mergeable"] == "CONFLICTING" and not result["conflicts"]:
            list_files_only.append(name)
        if not wanted and not has:
            waiting_on_nothing.append(name)
            continue
        add = [label for label in wanted if label not in has]
        unexplained = [label for label in has if label not in wanted]
        notes = (["add " + ", ".join(add)] if add else []) + (
            ["has " + ", ".join(unexplained) + ": nothing found for it"] if unexplained else [])
        since = result.get("sinceLabel") or {}
        moved = " and ".join(word for word in ("pushed", "replied") if since.get(word))
        to_add += [f"  {name} {label}" for label in add]
        for label in unexplained:
            if label == "protected-files":
                why = "it changes no protected path now"
            elif label == "BLOCKED":
                why = "it is not stacked on an open pull request now"
            elif result["inherited"]:
                why = f"its conflict is in the same files as #{result['stackedOn']}, which it waits for"
            elif moved:
                why = f"nothing is left to fix, and the author has {moved} since it was set"
            else:
                why = "no conflict, requested changes or failing check"
            stale.append(f"  {name} {label}: {why}")
        draft = " (draft)" if result["draft"] else ""
        out.append(f"{name} {safe(result['author'])}{draft}: {', '.join(wanted) or 'no label'}"
                   + (f"   [{'; '.join(notes)}]" if notes else "   [labels match]"))
        if result["conflicts"]:
            same = f" (the same files as #{result['stackedOn']})" if result["inherited"] else ""
            out.append(f"    conflicts with main{same}: " + ", ".join(safe(path) for path in result["conflicts"]))
        if result["review"] == "CHANGES_REQUESTED":
            out.append("    changes requested")
        if result["failing"]:
            out.append("    failing checks: " + ", ".join(safe(check) for check in result["failing"]))
        if result["stackedOn"] is not None:
            out.append(f"    stacked on #{result['stackedOn']}")
        if result["protected"]:
            out.append("    protected: " + "; ".join(f"{pattern} ({', '.join(found['owners'])})"    # main's own file
                                                     for pattern, found in result["protected"].items()))
        if result["assignees"]:
            out.append("    assigned: " + ", ".join(safe(login) for login in result["assignees"]))
        if moved:
            out.append(f"    the author has {moved} since needs-author was set on {since['set']}")
        out.append("")
    if to_add:
        out += ["Labels to add:"] + to_add
    if stale:   # a person may have set needs-author or BLOCKED for a reason Git does not show, so nothing is certain
        out += ["Labels nothing was found for (look before taking one off):"] + stale
    if list_files_only:
        out.append("GitHub shows a conflict, a local merge is clean (only the list files): "
                   + " ".join(list_files_only))
    if waiting_on_nothing:
        out.append("Waiting on nothing: " + " ".join(waiting_on_nothing))
    if unread:
        out += ["Could not read:"] + unread
    return "\n".join(out)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("numbers", nargs="*", type=int, help="pull request numbers (default: every open one)")
    parser.add_argument("--drafts", action="store_true", help="the drafts too")
    parser.add_argument("--repo", default=REPO, help="owner/name on GitHub (default: %(default)s)")
    parser.add_argument("--json", action="store_true", help="print the results as JSON")
    parser.error = lambda message: parser.exit(1, f"{parser.prog}: {message}\n")
    args = parser.parse_args(argv)

    try:
        listing = json.loads(gh("pr", "list", "--repo", args.repo, "--state", "open", "--limit", "500", "--json",
                                "number,author,isDraft,headRefOid,mergeable,reviewDecision,labels,assignees,"
                                "statusCheckRollup"))
        pulls = {pr["number"]: {
            "head": pr["headRefOid"], "author": pr["author"]["login"], "draft": pr["isDraft"],
            "mergeable": pr["mergeable"], "review": pr["reviewDecision"],
            "labels": [label["name"] for label in pr["labels"]],
            "assignees": [person["login"] for person in pr["assignees"]],
            "failing": [check.get("name") or check.get("context") or "?" for check in pr["statusCheckRollup"] or []
                        if (check.get("conclusion") or check.get("state")) in FAILED],
        } for pr in listing}
        url = f"https://github.com/{args.repo}"
        git(ROOT, "fetch", "-q", "--no-tags", url, "main")
        main_commit = git(ROOT, "rev-parse", "FETCH_HEAD").strip()
        check_git(ROOT, main_commit)
        if pulls:   # every open head, drafts too: a pull request can be stacked on any of them
            git(ROOT, "fetch", "-q", "--no-tags", url, *(f"pull/{number}/head" for number in pulls))
        queue = Queue(ROOT, main_commit, pulls)
    except RuntimeError as error:
        print("pr_queue: " + scan_docs.safe(str(error)), file=sys.stderr)
        return 1

    results = []
    for number in args.numbers or sorted(n for n, pull in pulls.items() if args.drafts or not pull["draft"]):
        if number not in pulls:
            results.append({"number": number, "error": "not an open pull request"})
            continue
        try:    # one pull request that cannot be read does not stop the others
            entry, pull = queue.entry(number), pulls[number]
            since = (moves_since_label(timeline_rows(args.repo, number), pull["author"])
                     if "needs-author" in pull["labels"] else None)
            results.append({"number": number, **pull, **entry, "wanted": wanted_labels(entry, pull),
                            "sinceLabel": since})
        except RuntimeError as error:
            results.append({"number": number, "error": scan_docs.safe(str(error))})

    print(json.dumps(results, indent=1) if args.json else report(results))
    return 1 if any(result.get("error") for result in results) else 0


if __name__ == "__main__":
    sys.exit(main())
