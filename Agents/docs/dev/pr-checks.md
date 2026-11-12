# Pull request checks

Docs, comments and issues reach agents as text they may act on, so a change to them can be a prompt injection.

## Docs scan

`.github/workflows/docs-scan.yml` runs `.github/scripts/scan-docs.py` on the lines a pull request adds.
It runs as it is on `main` and only reads the diff.
Its comment names a rule and a place, never the matched text.

| Result | Rules |
|---|---|
| Blocks the merge | hidden Unicode (all text files), HTML comments and non-rendering Markdown |
| Needs the maintainer's review | links to hosts outside the allowed list, command-like phrasing, encoded text, raw HTML that can hide or load content (docs and code comments only) |

For a review finding, the maintainer reads the lines, approves that commit and re-runs the check.
A new push needs a new approval.
A clean scan does not show that a doc is true: plain false statements pass.

## SQL

`.github/workflows/sql.yml` runs `.github/scripts/check-sql.py` on a pull request that touches `sql/`: a new update only
in `sql/updates/pending_<db>/`, no change to a merged file, no rows in `base/`, LF line ends, no tabs or trailing spaces,
a final `;`. Then it builds every database from scratch in the updater's order ([databases](databases.md)); a file
that fails names itself and the error.

## Protected files

`.github/CODEOWNERS` lists what only the maintainer can approve: agent instructions and guides, repository controls,
build files and dependencies, the reviewer warnings script, the launcher, the patch server, mod loading, the SQL schema
and the `auth` and `characters` updates, and legal files.
Gateway and module code is not on the list and still runs on the machine of whoever launches it, so review it as code.

## Reviewer warnings

`scripts/dev/pr_warnings.py` lists what a reviewer has to look at by hand in what a pull request adds.
It only warns: nothing it reports blocks a merge, and a quiet report does not show that a change is safe.
It reads the diff, the title, the description and the commit messages, and never checks out or runs the pull request.
Use the copy on `main`, not the pull request's own.

```powershell
python -I scripts/dev/pr_warnings.py 852                             # one pull request (needs gh)
python -I scripts/dev/pr_warnings.py --all                           # every open one that is not a draft
python -I scripts/dev/pr_warnings.py --base origin/main --head HEAD  # a local branch, the diff only
```

| Warning | Rules |
|---|---|
| project | a new project, solution or build file; a package, an assembly reference or a build step |
| network | web clients and requests, DNS lookups, downloads from a script, an address outside the docs scan's hosts; sockets outside `tests/` |
| process | a started process or shell, PowerShell from text, code loaded at run time, native calls, the registry, scheduled tasks |
| filesystem | the user's profile, key and credential files; deletes outside `tests/` |
| encoded | Base64 decoded at run time |
| sql | `sql/base/`, an edited merged update, the `auth` and `characters` databases, `DROP`, `TRUNCATE`, `ATTACH`, `PRAGMA`, triggers, `DELETE` and `UPDATE` without `WHERE` |
| injection, pr-text | the docs scan's phrasing rules on code and data outside comments, and on the title, description and commit messages |
| binary, script, repository | an added binary, an added script, a change under `.github/` |

The report names a rule and a place; `--show-lines` adds the line, cut short.
Loopback sockets and deletes inside `tests/` are not reported: the tests work that way.
A stacked pull request is read against the open one it sits on, so a warning shows once.
The rules match text: they find the careless and the plain, not what was hidden on purpose.
Offline checks: `python scripts/dev/test_pr_warnings.py`.

## Labels and claiming

| Label | Means |
|---|---|
| `needs-author` | waits on its author: a conflict with `main`, requested changes or a failing check |
| `BLOCKED` | waits on another pull request, named in a comment |
| `protected-files` | changes a path in `.github/CODEOWNERS`, so it needs that path's owner |

A maintainer who starts on a pull request or an issue assigns themselves; one with no assignee is free.

## Merging lists

`.gitattributes` merges the status, roadmap and index files by keeping both sides' lines, in local Git only.
GitHub's merge API ignores it and reports a conflict, so when a pull request conflicts only in these files, run
`git merge origin/main` locally and push: no hand edits are needed.
Two edits of one entry therefore both stay without a conflict: when you see that, remove the old one.
Everything else in `Agents/` conflicts as normal.
