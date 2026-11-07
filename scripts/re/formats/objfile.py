# Object files: .ot .otc .ao .aoc .it (version, extends, sections of key = value, GEAL events).
# The client's reader is 0x142475bd0; "client"/"server" hold more sections (0x142476580 skips them in its first pass).
import re

import geal

TOKEN = re.compile(r'''
    (?P<ws>\s+|//[^\r\n]*|/\*.*?\*/)
  | (?P<str>"[^"]*"|'[^']*')
  | (?P<op>[{}=])
  | (?P<word>[^\s{}="']+)
''', re.X | re.S)


class ObjError(Exception):
    pass


def tokenize(text):
    out, i, n = [], 0, len(text)
    while i < n:
        m = TOKEN.match(text, i)
        if not m:
            raise ObjError(f"bad character {text[i]!r} at {i}: {text[max(0, i - 30):i + 30]!r}")
        i = m.end()
        if m.lastgroup != "ws":
            out.append((m.lastgroup, m.group(), m.start(), m.end()))
    return out


class Reader:
    def __init__(self, text):
        self.text = text
        self.t = tokenize(text)
        self.i = 0

    def done(self):
        return self.i >= len(self.t)

    def value(self, k=0):
        j = self.i + k
        return self.t[j][1] if j < len(self.t) else None

    def kind(self, k=0):
        j = self.i + k
        return self.t[j][0] if j < len(self.t) else None

    def closing_brace(self):
        """The token index of the "}" that matches the "{" at the cursor. Strings are single tokens here, so a brace
        inside one does not count."""
        depth, j = 0, self.i
        while j < len(self.t):
            if self.t[j][0] == "op":
                if self.t[j][1] == "{":
                    depth += 1
                elif self.t[j][1] == "}":
                    depth -= 1
                    if depth == 0:
                        return j
            j += 1
        raise ObjError("unbalanced braces")

    def script(self):
        close = self.closing_brace()
        src = self.text[self.t[self.i][3]:self.t[close][2]]
        self.i = close + 1
        return src

    def entries(self, out):
        """Until "}" or the end: sections, key = value, and bare words."""
        while not self.done() and not (self.kind() == "op" and self.value() == "}"):
            k = self.kind()
            v = self.value()
            if k == "op":
                raise ObjError(f"unexpected {v!r}: {self.around()}")
            self.i += 1
            if self.kind() == "op" and self.value() == "=":
                self.i += 1
                out.append(self.assignment(v))
            elif self.kind() == "op" and self.value() == "{":
                out.append(self.section(v))
            else:
                out.append({"word": v})

    def section(self, name):
        if name == "GEAL":
            # Top-level GEAL block: named events, each = { script } (0x142475bd0 "expected '{' after 'GEAL'").
            self.i += 1
            sec = {"section": name, "entries": []}
            self.entries(sec["entries"])
            self.closing()
            return sec
        self.i += 1
        sec = {"section": name, "entries": []}
        self.entries(sec["entries"])
        self.closing()
        return sec

    def closing(self):
        if not (self.kind() == "op" and self.value() == "}"):
            raise ObjError(f"missing '}}': {self.around()}")
        self.i += 1

    def assignment(self, key):
        k, v = self.kind(), self.value()
        if k is None:
            raise ObjError(f"{key} = at the end of the file")
        if k == "op" and v == "{":
            src = self.script()
            return {"key": key, "script": geal.parse(src), "source_tokens": len(geal.tokenize(src))}
        if k == "op":
            raise ObjError(f"{key} = {v}: {self.around()}")
        self.i += 1
        return {"key": key, "value": v}

    def around(self):
        return repr(" ".join(t[1] for t in self.t[max(0, self.i - 5):self.i + 5]))


def parse(text):
    r = Reader(text)
    out = {"version": None, "abstract": False, "extends": [], "entries": []}
    while not r.done():
        v = r.value()
        if v == "version" and r.kind(1) == "word":
            out["version"] = int(r.value(1))
            r.i += 2
        elif v == "abstract":
            out["abstract"] = True
            r.i += 1
        elif v == "extends":
            out["extends"].append(r.value(1).strip("\"'"))
            r.i += 2
        else:
            r.entries(out["entries"])
            if not r.done():
                raise ObjError(f"stray '}}': {r.around()}")
    return out


def source_tokens(text):
    """The tokens of every script in a file, in order, for the round-trip check."""
    r = Reader(text)
    scripts = []
    while not r.done():
        if r.kind() == "word" and r.kind(1) == "op" and r.value(1) == "=" and r.kind(2) == "op" and r.value(2) == "{":
            r.i += 2
            scripts.append(geal.tokenize(r.script()))
        else:
            r.i += 1
    return scripts


def scripts_of(tree):
    """Every parsed script in a tree, as (key, statements)."""
    def go(entries):
        for e in entries:
            if "script" in e:
                yield e["key"], e["script"]
            elif "section" in e:
                yield from go(e["entries"])
    yield from go(tree["entries"])
