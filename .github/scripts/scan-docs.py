#!/usr/bin/env python3
"""Scan the lines a pull request adds for text hidden from a reviewer or aimed at an agent.

Only reads a diff: nothing from the pull request runs. The report names a rule and a place, never the matched text, so
it cannot repeat an injection to whoever reads it. Agents/docs/dev/pr-checks.md
Exit code: 0 clean, 1 something blocks the merge, 2 something needs the maintainer's review.
"""
import argparse
import base64
import math
import re
import subprocess
import sys
import unicodedata
from collections import Counter
from urllib.parse import urlsplit

DOC_EXTENSIONS = (".md", ".txt", ".rst")
COMMENT_EXTENSIONS = (".cs", ".ps1", ".psm1", ".py", ".js", ".mjs", ".sh", ".yml", ".yaml", ".props", ".targets",
                      ".csproj", ".toml", ".ini")
BINARY_EXTENSIONS = (".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".exe", ".dll", ".pdb", ".zip", ".7z", ".bin",
                     ".dat", ".datc64", ".ggpk", ".wasm", ".ttf", ".otf", ".woff", ".woff2", ".pdf")
# Hashes and generated data, where long encoded-looking strings are normal.
NO_TEXT_RULES = ("package-lock.json", "data/schema/", "Agents/docs/re/components.json")

ALLOWED_HOSTS = {
    "127.0.0.1", "0.0.0.0", "localhost", "poewiki.net", "www.poewiki.net", "pathofexile.com", "www.pathofexile.com",
    "poedb.tw", "learn.microsoft.com", "dotnet.microsoft.com", "frida.re", "nodejs.org", "www.python.org",
    "git-scm.com", "www.conventionalcommits.org",
}
ALLOWED_GITHUB_OWNERS = {"localexile", "wrathen", "pathofbuildingcommunity", "vilsol", "poe-tool-dev"}

PHRASES = [(re.compile(pattern, re.IGNORECASE), name) for pattern, name in [
    (r"\b(ignore|disregard|forget|override)\b[^.\n]{0,40}\b(instructions?|prompts?|rules|guidelines)\b",
     "overrides instructions"),
    (r"\bsystem prompt\b", "mentions a system prompt"),
    (r"\byou are (now )?(an? |the )?(ai|assistant|agent|claude|codex|copilot|gemini|llm)\b", "addresses an agent"),
    (r"\bas an? (ai|agent|assistant|llm)\b", "addresses an agent"),
    # Not "AI" here: on this server that is mostly a monster's.
    (r"\b(agents?|assistants?|claude|codex|copilot|gemini|llms?)\b[^.\n]{0,20}\b(must|shall|should|needs? to|have to)\b",
     "tells agents what to do"),
    (r"\b(when|if|once) you (read|see|find|open) this\b", "addresses the reader"),
    (r"\byou (must|shall|should|need to|have to)\b[^.\n]{0,20}\b(run|execute|download|install|fetch|delete|push|commit|"
     r"merge|approve|send|upload|disable)\b", "tells the reader to act"),
    (r"(^|[.:;!]\s+|\bplease\s+|\bthen\s+|^\s*[-*]\s+)(run|execute) (this|these)\b|\b(run|execute) the following\b",
     "tells the reader to run something"),
    (r"\b(do not|don't|never|without) (tell|telling|mention|mentioning|inform|informing|ask|asking|confirm|confirming|"
     r"reveal|revealing|show|showing)\b[^.\n]{0,30}\b(user|maintainer|reviewer|owner|human)\b", "hides from a person"),
    (r"\b(pre-?authori[sz]ed|already approved|approved by the maintainer|no review needed)\b", "claims approval"),
    (r"\b(curl|wget|iwr|invoke-webrequest)\b[^\n]*\|\s*(sh|bash|iex|invoke-expression|python)\b", "pipes a download"),
    (r"\b(invoke-expression|frombase64string)\b", "runs or decodes text"),
    (r"\bgh\s+(pr\s+(merge|review)|auth\s+token|api\s+-X)\b", "acts through GitHub"),
    (r"--no-verify\b|--dangerously", "skips a safety check"),
    (r"\b(send|post|upload|exfiltrate|leak)\b[^.\n]{0,40}\b(token|credential|secret|password|api key|private key)s?\b",
     "moves a secret"),
]]

URL = re.compile(r"\b[a-z][a-z0-9+.-]*://[^\s)>\]\"'`]+", re.IGNORECASE)
HIDDEN_MARKUP = re.compile(r"<!--|^\s*\[[^\]]*\]:\s*#", re.MULTILINE)
RISKY_HTML = re.compile(r"<(iframe|script|style|object|embed|img)\b|\bstyle\s*=|\bhidden\b\s*=", re.IGNORECASE)
INLINE_CODE = re.compile(r"`[^`]*`")
TOKEN = re.compile(r"[A-Za-z0-9+/_-]{40,}={0,2}")
ESCAPES = re.compile(r"(?:\\u[0-9a-fA-F]{4}|\\x[0-9a-fA-F]{2}|%[0-9a-fA-F]{2}|&#x?[0-9a-fA-F]+;){6,}")
DATA_URI = re.compile(r"\bdata:[a-z-]+/[a-z0-9.+-]+[;,]", re.IGNORECASE)
COMMENT = re.compile(r"(?://|#|<!--|^\s*\*|^\s*/\*)(.*)$")


def hidden_characters(text, allow_leading_bom):
    """The names of characters that do not show, or show as something else, in a review."""
    names = set()
    for index, char in enumerate(text):
        code = ord(char)
        if char == "\t" or (allow_leading_bom and index == 0 and char == chr(0xFEFF)):
            continue
        if (unicodedata.category(char) in ("Cf", "Cc", "Zl", "Zp", "Co", "Cn")
                or 0xFE00 <= code <= 0xFE0E or 0xE0100 <= code <= 0xE01EF
                or code in (0x115F, 0x1160, 0x17B4, 0x17B5, 0x2800, 0x3164, 0xFFA0)):
            names.add(f"U+{code:04X}")
    return names


def entropy(text):
    counts = Counter(text)
    return -sum(n / len(text) * math.log2(n / len(text)) for n in counts.values())


def decodes_to_text(token):
    padded = token.replace("-", "+").replace("_", "/")
    try:
        decoded = base64.b64decode(padded + "=" * (-len(padded) % 4))
    except ValueError:
        return False
    return len(decoded) > 0 and sum(32 <= b < 127 or b in (9, 10, 13) for b in decoded) / len(decoded) > 0.85


def looks_encoded(token):
    if re.fullmatch(r"[0-9a-fA-F]+", token) or not (re.search(r"[a-z]", token) and re.search(r"[A-Z]", token)
                                                     and re.search(r"[0-9]", token)):
        return False
    return decodes_to_text(token) or entropy(token) >= 4.8


def host_allowed(url):
    try:
        parts = urlsplit(url)
        host = (parts.hostname or "").lower()
    except ValueError:
        return False, "an unreadable address"
    if parts.scheme.lower() not in ("http", "https"):
        return False, f"scheme {parts.scheme.lower()}"
    if host == "github.com":
        owner = parts.path.strip("/").split("/")[0].lower()
        return owner in ALLOWED_GITHUB_OWNERS, f"github.com/{owner}"
    return host in ALLOWED_HOSTS and "@" not in parts.netloc, host


def data_portion(path, line):
    """The part of an added line that a reader takes as prose: all of a doc, the comment of source code."""
    lower = path.lower()
    if lower.endswith(DOC_EXTENSIONS):
        return line
    if lower.endswith(COMMENT_EXTENSIONS):
        match = COMMENT.search(line)
        return match.group(1) if match else ""
    return ""


def scan_line(path, number, line):
    """Yield (blocks, rule) for one added line."""
    names = hidden_characters(line, number == 1)
    if names:
        yield True, "hidden characters " + " ".join(sorted(names))
    lower = path.lower()
    if lower.endswith((".md", ".rst")) and HIDDEN_MARKUP.search(line):
        yield True, "markup that does not render"
    if any(part in path for part in NO_TEXT_RULES):
        return
    text = data_portion(path, line)
    if not text:
        return
    if lower.endswith((".md", ".rst")) and RISKY_HTML.search(INLINE_CODE.sub("", text)):
        yield False, "raw HTML that can hide or load content"
    for url in URL.findall(text):
        allowed, what = host_allowed(url)
        if not allowed:
            yield False, f"new link: {what}"
    for pattern, name in PHRASES:
        if pattern.search(text):
            yield False, f"command-like phrasing: {name}"
    # Without the links, whose commit hashes and paths look encoded and which the link rule already covers.
    plain = URL.sub("", text)
    if DATA_URI.search(plain) or ESCAPES.search(plain) or any(looks_encoded(t) for t in TOKEN.findall(plain)):
        yield False, "encoded text"


def added_lines(diff):
    """Yield (path, new line number, bytes) from a --unified=0 diff, counting hunks so no added text reads as a header."""
    lines = diff.split(b"\n")
    index, path = 0, ""
    hunk = re.compile(rb"^@@ -\d+(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
    while index < len(lines):
        line = lines[index]
        index += 1
        if line.startswith(b"+++ "):
            path = line[4:].rstrip(b"\r").decode("utf-8", "replace").strip('"')
            path = path[2:] if path.startswith("b/") else path
            continue
        match = hunk.match(line)
        if not match:
            continue
        removed = int(match.group(1) or 1)
        number = int(match.group(2))
        count = int(match.group(3) or 1)
        for _ in range(removed + count):
            while index < len(lines) and lines[index].startswith(b"\\"):
                index += 1
            if index >= len(lines):
                return
            content = lines[index]
            index += 1
            if content.startswith(b"+"):
                yield path, number, content[1:]
                number += 1


def safe(text):
    """Text from the pull request, reduced so it cannot format or mention anything in a comment."""
    return re.sub(r"[^A-Za-z0-9_./+ :=-]", "?", text)[:160]


def report_text(blocking, review, limit=60):
    out = []
    for title, findings in (("Blocks the merge", blocking), ("Needs the maintainer's review", review)):
        if findings:
            out.append(f"**{title}** ({len(findings)})\n")
            out.extend(f"- `{safe(path)}:{number}`: {safe(rule)}" for path, number, rule in findings[:limit])
            if len(findings) > limit:
                out.append(f"- and {len(findings) - limit} more")
            out.append("")
    return "\n".join(out) if out else "No findings in the added lines.\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True, help="the branch the pull request merges into")
    parser.add_argument("--head", required=True, help="the pull request's commit")
    parser.add_argument("--report", help="write the Markdown report here")
    args = parser.parse_args()

    merge_base = subprocess.run(["git", "merge-base", args.base, args.head], check=True, capture_output=True,
                                text=True).stdout.strip()
    diff = subprocess.run(["git", "-c", "core.quotePath=false", "diff", "-a", "--unified=0", "--no-color", "--no-renames",
                           "--no-ext-diff", "--no-textconv", "--diff-filter=d", merge_base, args.head],
                          check=True, capture_output=True).stdout

    blocking, review, seen = [], [], set()
    for path, number, raw in added_lines(diff):
        if path.lower().endswith(BINARY_EXTENSIONS) or b"\0" in raw:
            continue
        if path not in seen:
            seen.add(path)
            if hidden_characters(path, False):
                blocking.append((path, 0, "hidden characters in the file name"))
        line = raw.decode("utf-8", "replace").removesuffix("\r")
        for blocks, rule in scan_line(path, number, line):
            (blocking if blocks else review).append((path, number, rule))

    report = report_text(blocking, review)
    if args.report:
        with open(args.report, "w", encoding="utf-8") as file:
            file.write(report)
    print(report)
    return 1 if blocking else 2 if review else 0


if __name__ == "__main__":
    sys.exit(main())
