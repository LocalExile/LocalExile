"""Rewrite the client addresses in the repository's tracked files from one client build to the next.

Usage: python -I scripts/re/port/rewrite_addresses.py <resolved.tsv> <report.tsv> [--apply] [--exclude <glob> ...]
       [--only <glob> ...]

<resolved.tsv> is scripts/re/ghidra/PortMap.java's output: old address, new address or -, how. Every address token in a
tracked text file is looked up: 0x14XXXXXXX, FUN_/DAT_/LAB_/PTR_14XXXXXXX, and a bare 14XXXXXXX with a hex letter in it.
A mapped token is replaced in the same spelling (prefix and letter case); an unmapped one is left as it is and listed in
<report.tsv> (file, line, token, reason). Without --apply nothing is written. Excluded paths (fnmatch globs) are only
counted; with --only, only the matching paths are looked at.
"""
import fnmatch, re, subprocess, sys

# A Java long literal keeps its L suffix.
TOKEN = re.compile(r"(?<![0-9A-Za-z_])((?:0x|0X|FUN_|DAT_|LAB_|PTR_)?)(14[0-9A-Fa-f]{7})(?=[Ll]?(?:[^0-9A-Za-z_]|$))")


def main():
    args = sys.argv[1:]
    apply = "--apply" in args
    excludes = [args[i + 1] for i, a in enumerate(args) if a == "--exclude"]
    only = [args[i + 1] for i, a in enumerate(args) if a == "--only"]
    resolved_path, report_path = [a for i, a in enumerate(args)
                                  if not a.startswith("--") and (i == 0 or args[i - 1] not in ("--exclude", "--only"))][:2]

    mapping, why = {}, {}
    for line in open(resolved_path, encoding="utf-8"):
        old, new, how = line.rstrip("\n").split("\t", 2)
        key = int(old, 16)
        if new == "-":
            why[key] = how
        else:
            mapping[key] = int(new, 16)

    files = subprocess.check_output(["git", "ls-files", "-z"]).decode("utf-8").split("\0")
    report, changed_files, replaced, unmapped, skipped = [], 0, 0, 0, 0
    for path in filter(None, files):
        if only and not any(fnmatch.fnmatch(path, g) for g in only):
            continue
        if any(fnmatch.fnmatch(path, g) for g in excludes):
            try:
                skipped += len(TOKEN.findall(open(path, encoding="utf-8").read()))
            except (UnicodeDecodeError, FileNotFoundError, IsADirectoryError):
                pass
            continue
        try:
            raw = open(path, "rb").read()
            text = raw.decode("utf-8")
        except (UnicodeDecodeError, FileNotFoundError, IsADirectoryError):
            continue
        if not TOKEN.search(text):
            continue
        lines = text.split("\n")
        file_changed = False
        for n, line in enumerate(lines):
            def swap(m):
                nonlocal replaced, unmapped, file_changed
                prefix, digits = m.group(1), m.group(2)
                if not prefix and not re.search(r"[A-Fa-f]", digits):
                    return m.group(0)        # a bare decimal-looking number, not an address
                old = int(digits, 16)
                if old in mapping:
                    new = f"{mapping[old]:09x}"
                    if re.search(r"[A-F]", digits):
                        new = new.upper()
                    if new != digits.lower() and new != digits:
                        file_changed = True
                    replaced += 1
                    return prefix + new
                unmapped += 1
                report.append(f"{path}\t{n + 1}\t{m.group(0)}\t{why.get(old, 'not in resolved.tsv')}")
                return m.group(0)
            lines[n] = TOKEN.sub(swap, line)
        if file_changed:
            changed_files += 1
            if apply:
                open(path, "wb").write("\n".join(lines).encode("utf-8"))
    with open(report_path, "w", encoding="utf-8") as f:
        f.write("\n".join(report) + ("\n" if report else ""))
    print(f"tokens replaced {replaced}, unmapped {unmapped}, in excluded files {skipped}; files changed {changed_files}"
          + ("" if apply else " (dry run)"))


if __name__ == "__main__":
    main()
