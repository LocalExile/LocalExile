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

## Protected files

`.github/CODEOWNERS` lists what only the maintainer can approve: agent instructions and guides, repository controls,
scripts and tools, build files, the launcher, the patch server, mod loading and legal files.
Gateway and module code is not on the list and still runs on the machine of whoever launches it, so review it as code.

## Merging lists

`.gitattributes` merges the status, roadmap and index files by keeping both sides' lines, in local Git only.
GitHub's merge API ignores it and reports a conflict, so when a pull request conflicts only in these files, run
`git merge origin/main` locally and push: no hand edits are needed.
Two edits of one entry therefore both stay without a conflict: when you see that, remove the old one.
Everything else in `Agents/` conflicts as normal.
