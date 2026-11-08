"""Rewrite message ids in tracked files from one client build to the next (Agents/docs/re/tooling.md section 11).

Usage: python -I scripts/re/port/rewrite_message_ids.py <report.tsv> --shift <first>:<last>:<delta> ...
       [--only <glob> ...] [--skip <glob> ...] [--apply]

A hex number whose value lies in a --shift range (from compare_messages.py) is a message id unless its context says
otherwise; ids keep their spelling (0x2b9 -> 0x2bb, 0x02b9 -> 0x02bb, letter case). Not ids: offsets (+0x2b0,
[0x2b0], .add(0x2b0), a reader helper's offset argument such as u16(m, 0x2b0)), sizes (0x298-byte, 0x2b0 bytes), a
ClientStrings row, and every number in a --skip file. Numbers of three or four digits only, so addresses never match.
<report.tsv> lists every number looked at: file, line, old, new or "kept", the rule, the line. Review it before
--apply: a value that is not a message id but has no tell-tale context is rewritten.
"""
import fnmatch, re, subprocess, sys

NUMBER = re.compile(r"(?<![0-9A-Za-z_])0([xX])([0-9a-fA-F]{3,4})(?![0-9A-Za-z_])")
OFFSET_BEFORE = re.compile(r"(?:[+\[]\s*|(?<![\w-])-|\.add\(\s*|\b(?:i32|u32|u16|u8|f32|s32)\(\s*\w+\s*,\s*|ClientStrings\s+)$")
SIZE_AFTER = re.compile(r"^(?:-byte|\s*bytes?\b)")


def main():
    args = sys.argv[1:]
    def opt(name):
        return [args[i + 1] for i, a in enumerate(args) if a == name]
    apply = "--apply" in args
    only, skip = opt("--only"), opt("--skip")
    shifts = [tuple(int(x, 0) for x in s.split(":")) for s in opt("--shift")]
    report_path = next(a for i, a in enumerate(args)
                       if not a.startswith("--") and (i == 0 or args[i - 1] not in ("--only", "--skip", "--shift")))

    def new_id(v):
        for first, last, delta in shifts:
            if first <= v <= last:
                return v + delta
        return None

    files = subprocess.check_output(["git", "ls-files", "-z"]).decode("utf-8").split("\0")
    report, changed, replaced, kept = [], 0, 0, 0
    for path in filter(None, files):
        if only and not any(fnmatch.fnmatch(path, g) for g in only):
            continue
        try:
            raw = open(path, "rb").read()
            text = raw.decode("utf-8")
        except (UnicodeDecodeError, FileNotFoundError, IsADirectoryError):
            continue
        skipped_file = any(fnmatch.fnmatch(path, g) for g in skip)
        lines = text.split("\n")
        file_changed = False
        for n, line in enumerate(lines):
            def swap(m):
                nonlocal replaced, kept, file_changed
                digits = m.group(2)
                v = int(digits, 16)
                nv = new_id(v)
                if nv is None:
                    return m.group(0)
                if skipped_file:
                    rule = "skip file"
                elif OFFSET_BEFORE.search(line[:m.start()]):
                    rule = "offset"
                elif SIZE_AFTER.search(line[m.end():]):
                    rule = "size"
                else:
                    rule = None
                if rule:
                    kept += 1
                    report.append(f"{path}\t{n + 1}\t{m.group(0)}\tkept\t{rule}\t{line.strip()[:200]}")
                    return m.group(0)
                out = f"{nv:0{len(digits)}x}"
                if re.search(r"[A-F]", digits):
                    out = out.upper()
                replaced += 1
                file_changed = True
                report.append(f"{path}\t{n + 1}\t{m.group(0)}\t0{m.group(1)}{out}\tid\t{line.strip()[:200]}")
                return f"0{m.group(1)}{out}"
            lines[n] = NUMBER.sub(swap, line)
        if file_changed:
            changed += 1
            if apply:
                open(path, "wb").write("\n".join(lines).encode("utf-8"))
    with open(report_path, "w", encoding="utf-8") as f:
        f.write("\n".join(report) + ("\n" if report else ""))
    print(f"ids rewritten {replaced}, kept {kept}; files changed {changed}" + ("" if apply else " (dry run)"))


if __name__ == "__main__":
    main()
