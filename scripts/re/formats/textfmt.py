# The line-based text formats of the game data. Every parser reads through a Lines cursor and raises if a token is left
# over, so a format that changes or a field that was missed shows up as a failure in decode_all.py's report.
# Field names come from the client's reader where it names them; a number we can't name stays in a list called "n".
import json
import re

TOKEN = re.compile(r'"[^"]*"|[^\s"]+')
NUM = re.compile(r'[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?f?$')


class FormatError(Exception):
    pass


def value(tok):
    """A token as JSON: a quoted string loses its quotes, a number becomes a number, anything else stays text."""
    if tok[0] == '"':
        return tok[1:-1]
    if NUM.match(tok):
        t = tok.rstrip("f")
        try:
            return int(t)
        except ValueError:
            return float(t)
    return tok


class Lines:
    def __init__(self, text):
        self.lines = []
        for line in re.split(r"\r\n|\n|\r", text.lstrip("﻿")):
            line = line.split("//")[0] if line.lstrip().startswith("//") else line
            self.lines.append(TOKEN.findall(line))
        self.i = 0

    def skip_blank(self):
        while self.i < len(self.lines) and not self.lines[self.i]:
            self.i += 1

    def done(self):
        self.skip_blank()
        return self.i >= len(self.lines)

    def peek(self):
        self.skip_blank()
        return self.lines[self.i] if self.i < len(self.lines) else None

    def next(self):
        line = self.peek()
        if line is None:
            raise FormatError("unexpected end of file")
        self.i += 1
        return line

    def blank_next(self):
        """Whether the cursor is on a blank line or the end: blank lines end a group in several formats."""
        return self.i >= len(self.lines) or not self.lines[self.i]

    def version(self):
        line = self.peek()
        if line and line[0] == "version":
            self.i += 1
            return int(line[1])
        return None

    def rest(self):
        out = []
        while not self.done():
            out.append(self.next())
        return out

    def finish(self, result):
        if not self.done():
            raise FormatError(f"left over at line {self.i + 1}: {self.peek()[:12]}")
        return result


def need(cond, what, line=None):
    if not cond:
        raise FormatError(f"{what}: {line[:14] if line else ''}")


def is_num(t):
    return bool(NUM.match(t))


def is_str(t):
    return t[0] == '"'


def weighted(line):
    need(len(line) == 2 and is_num(line[0]) and is_str(line[1]), "expected weight \"path\"", line)
    return {"weight": value(line[0]), "path": value(line[1])}


def key_values(text):
    """.tsi: version, then Key value; 0x142a0f6f0 reads them by name."""
    c = Lines(text)
    out = {"version": c.version(), "values": {}}
    while not c.done():
        line = c.next()
        need(len(line) >= 2, "expected key value", line)
        v = [value(t) for t in line[1:]]
        v = v[0] if len(v) == 1 else v
        if line[0] in out["values"]:
            prev = out["values"][line[0]]
            out["values"][line[0]] = (prev if isinstance(prev, list) else [prev]) + [v]
        else:
            out["values"][line[0]] = v
    return out


def weighted_entry(line):
    """[weight] "path" [more]: a missing weight is the client's default."""
    i = 1 if not is_str(line[0]) else 0
    need(len(line) > i and is_str(line[i]), "expected [weight] \"path\"", line)
    e = {"path": value(line[i])}
    if i:
        e["weight"] = value(line[0])
    if len(line) > i + 1:
        e["more"] = [value(t) for t in line[i + 1:]]
    return e


def weighted_list(text):
    """.rs and .toy: version, header lines (.toy: 1 1 FileOrder +Flag...), then [weight] "path" [more]."""
    c = Lines(text)
    out = {"version": c.version(), "header": [], "entries": []}
    while not c.done():
        line = c.next()
        if not any(is_str(t) for t in line):
            # .toy: a later header starts another list with its own flags
            (out["entries"] if out["entries"] else out["header"]).append([value(t) for t in line])
        else:
            out["entries"].append(weighted_entry(line))
    return out


def tile_set(text):
    """.tst: groups separated by blank lines; a group is "path" or weight "path" lines."""
    c = Lines(text)
    out = {"version": c.version(), "groups": []}
    while not c.done():
        group = []
        c.skip_blank()
        while not c.blank_next():
            line = c.next()
            group.append([value(t) for t in line])
        out["groups"].append(group)
    return out


def named_groups(text):
    """.gft: version, then "ground" [number] with its weighted tiles. Old files also give the counts on lines of their own."""
    c = Lines(text)
    out = {"version": c.version(), "counts": [], "groups": []}
    while not c.done():
        line = c.next()
        if is_str(line[0]):
            out["groups"].append({"name": value(line[0]), "n": [value(t) for t in line[1:]], "tiles": []})
        elif len(line) == 1 and is_num(line[0]):
            (out["groups"][-1]["n"] if out["groups"] else out["counts"]).append(value(line[0]))
        else:
            need(out["groups"], "weighted tile before a group name", line)
            out["groups"][-1]["tiles"].append(weighted_entry(line))
    return out


def materials(text):
    """.mtd, a stream of tokens (0x1429f9240): version, the default group, then "name" groups. A group is the number of mix
    and of sprinkle materials, then the mix materials with their numbers, then the sprinkle ones. A material is a path
    with an optional doodad layer path after it."""
    c = Lines(text)
    out = {"version": c.version(), "groups": []}
    toks = [t for line in c.rest() for t in line]
    i, n = 0, len(toks)

    def material(i):
        need(i < n and is_str(toks[i]), "expected a material", toks[i:])
        m = {"path": value(toks[i])}
        i += 1
        if i < n and is_str(toks[i]) and value(toks[i]).lower().endswith(".dlp"):
            m["doodad_layer"] = value(toks[i])
            i += 1
        return m, i

    def numbers(i):
        start = i
        while i < n and is_num(toks[i]):
            i += 1
        return [value(x) for x in toks[start:i]], i

    while i < n:
        g = {}
        if is_str(toks[i]):
            g["name"] = value(toks[i])
            i += 1
        need(i + 1 < n and is_num(toks[i]) and is_num(toks[i + 1]), "expected the material counts", toks[i:])
        mix, sprinkle = int(toks[i]), int(toks[i + 1])
        i += 2
        g["mix"], g["sprinkle"] = [], []
        for _ in range(mix):
            m, i = material(i)
            g["mix"].append(m)
        g["weights"], i = numbers(i)
        for _ in range(sprinkle):
            m, i = material(i)
            m["numbers"], i = numbers(i)
            g["sprinkle"].append(m)
        out["groups"].append(g)
    return out


def doodads_or_decals(text):
    """.ddt .dct .clt: version, density lines, then groups: ground types (Default or "name" [flags]) and weighted lines
    (weight or All, "path", more)."""
    c = Lines(text)
    out = {"version": c.version(), "density": [], "groups": []}
    while c.peek() and all(is_num(x) for x in c.peek()):
        out["density"].append([value(x) for x in c.next()])
    while not c.done():
        line = c.next()
        entry = len(line) >= 2 and is_str(line[1]) and not is_str(line[0])
        if not entry:
            ground = [value(x) for x in line]
            if out["groups"] and not out["groups"][-1]["entries"]:
                out["groups"][-1]["grounds"].append(ground)
            else:
                out["groups"].append({"grounds": [ground], "entries": []})
        else:
            need(out["groups"], "weighted line before a ground type", line)
            out["groups"][-1]["entries"].append([value(x) for x in line])
    return out


def chests(text):
    """.cht: version, numbers, then weight "names, names"."""
    c = Lines(text)
    out = {"version": c.version(), "n": [], "entries": []}
    while not c.done():
        line = c.next()
        if line[0].startswith("#"):
            continue
        if not out["entries"] and all(is_num(t) for t in line):
            out["n"].append([value(t) for t in line])
            continue
        if len(line) == 1 and is_str(line[0]):
            out["entries"].append({"grounds": [s.strip() for s in value(line[0]).split(",")]})
            continue
        need(len(line) == 2 and is_num(line[0]) and is_str(line[1]), "expected weight \"chests\"", line)
        out["entries"].append({"weight": value(line[0]), "chests": [s.strip() for s in value(line[1]).split(",")]})
    return out


def table(text):
    """.ecf .gcf .tmo: version, then rows of quoted paths and numbers."""
    c = Lines(text)
    return {"version": c.version(), "rows": [[value(t) for t in line] for line in c.rest()]}


def doodad_layer(text):
    """.dlp: version optional, a line of numbers, then "model" weight lines."""
    c = Lines(text)
    out = {"version": c.version()}
    out["n"] = [value(t) for t in c.next()]
    out["models"] = [[value(t) for t in line] for line in c.rest()]
    return out


def atlas(text):
    """.atlas: version, "material", size, then name x y size [more]."""
    c = Lines(text)
    out = {"version": c.version()}
    line = c.next()
    need(len(line) == 1 and is_str(line[0]), "expected the material", line)
    out["material"] = value(line[0])
    out["size"] = [value(t) for t in c.next()]
    out["sprites"] = [[value(t) for t in line] for line in c.rest()]
    return out


def file_groups(text):
    """.fgp: version, then "group" and its relative paths."""
    c = Lines(text)
    out = {"version": c.version(), "groups": []}
    while not c.done():
        line = c.next()
        if is_str(line[0]) and len(line) == 1:
            out["groups"].append({"name": value(line[0]), "files": []})
        else:
            need(out["groups"], "file before a group", line)
            out["groups"][-1]["files"].append(" ".join(line))
    return out


def edge_points(text):
    """.edp: version, the number of entries, then Name count n and count weighted edge types, sometimes one more line."""
    c = Lines(text)
    out = {"version": c.version(), "entries": []}
    out["count"] = int(c.next()[0])
    while not c.done():
        line = c.next()
        e = {"name": value(line[0]), "n": [value(t) for t in line[2:]], "edges": []}
        for _ in range(int(line[1])):
            e["edges"].append([value(t) for t in c.next()])
        nxt = c.peek()
        if nxt and not is_num(nxt[0]) and len(nxt) > 3:
            e["more"] = [value(t) for t in c.next()]
        out["entries"].append(e)
    return out


def ground_type(text):
    """.gt: name [#colour], then numbers."""
    c = Lines(text)
    first = c.next()
    out = {"name": value(first[0])}
    if len(first) > 1:
        out["more"] = [value(t) for t in first[1:]]
    out["rows"] = [[value(t) for t in line] for line in c.rest()]
    return out


def edge_type(text):
    """.et: name [#colour], paths of the two ground types, numbers."""
    return ground_type(text)


def skin_mesh(text):
    """.sm: version, SkinnedMeshData "smd", Materials count + "mat" faces, BoundingBox six numbers (0x1429edf10)."""
    c = Lines(text)
    out = {"version": c.version(), "materials": []}
    while not c.done():
        line = c.next()
        key = line[0]
        if key == "Materials":
            for _ in range(int(line[1])):
                m = c.next()
                need(is_str(m[0]), "expected a material", m)
                out["materials"].append({"path": value(m[0]), "n": [value(t) for t in m[1:]]})
        else:
            v = [value(t) for t in line[1:]]
            out[key] = v[0] if len(v) == 1 else v
    return out


def tile_geometry(text):
    """.tgt: version, Size, TileMeshRoot, NormalMaterials and its paths, SubTileMaterialIndices with one line per sub tile
    (0x1429ca010)."""
    c = Lines(text)
    out = {"version": c.version()}
    while not c.done():
        line = c.next()
        key = line[0]
        if key.endswith("Materials") and len(line) == 2 and is_num(line[1]):
            out[key] = []
            for _ in range(int(line[1])):
                m = c.next()
                need(is_str(m[0]), "expected a material", m)
                out[key].append(value(m[0]))
        elif key == "SubTileMaterialIndices" or key.endswith("Indices"):
            rows = []
            while c.peek() and is_num(c.peek()[0]):
                rows.append([value(t) for t in c.next()])
            out[key] = rows
        else:
            v = [value(t) for t in line[1:]]
            out[key] = v[0] if len(v) == 1 else v
    return out


def animation_metadata(text):
    """.amd: version, the number of animations, then for each "name" mode, a list of numbers and event blocks."""
    c = Lines(text)
    out = {"version": c.version(), "count": None, "animations": []}
    out["count"] = int(c.next()[0])
    current = None
    for line in c.rest():
        if len(line) == 1 and is_str(line[0]):
            current = {"name": value(line[0]), "rows": []}
            out["animations"].append(current)
        else:
            need(current is not None, "row before an animation", line)
            current["rows"].append([value(t) for t in line])
    return out


def actions(text):
    """.act: animated_object "x.ao", then { Action = "clips" [effect ...]; then "clips"; }, then key value (0x142a0db10).
    A statement ends at ";" and may span lines; "then" continues the action before it."""
    c = Lines(text)
    out = {"animated_object": None, "actions": [], "values": {}}
    inside = False
    pending = []
    while not c.done():
        line = c.next()
        if not inside and not pending and line == ["{"]:
            inside = True
        elif line == ["}"]:
            if pending:  # the last statement may leave out its ";"
                action_statement(out, pending)
                pending = []
            inside = False
        elif inside:
            for tok in line:
                ended = tok.endswith(";") and tok[0] != '"'
                if tok != ";":
                    pending.append(tok[:-1] if ended else tok)
                if ended:
                    action_statement(out, pending)
                    pending = []
        elif line[0] == "animated_object":
            out["animated_object"] = value(line[1])
        else:
            v = [value(x) for x in line[1:]]
            out["values"][line[0]] = v[0] if len(v) == 1 else v
    need(not pending, "statement without its ';'", pending)
    return out


def action_statement(out, toks):
    if toks[0] == "then":
        need(out["actions"], "then before an action", toks)
        out["actions"][-1].setdefault("then", []).append([value(x) for x in toks[1:]])
        return
    if len(toks) < 2 or toks[1] != "=":  # timeline_variation Name::n "x.atl";
        out["actions"].append({"directive": toks[0], "args": [value(x) for x in toks[1:]]})
        return
    out["actions"].append({"action": toks[0], "clips": [value(x) for x in toks[2:]], "then": []})


JSON_TAIL = re.compile(r"""[\r\n]\{\s*\"""")


def json_values(text):
    """One JSON value after another, as .pet files with a second object have them."""
    text = re.sub(r",(\s*[}\]])", r"\1", text)
    decoder, out, i = json.JSONDecoder(), [], 0
    while True:
        while i < len(text) and text[i].isspace():
            i += 1
        if i >= len(text):
            return out, None
        try:
            v, i = decoder.raw_decode(text, i)
        except json.JSONDecodeError:
            if not out:
                raise
            return out, text[i:]
        out.append(v)


def loads_relaxed(text):
    """JSON, also with the trailing commas the client's reader lets through."""
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return json.loads(re.sub(r",(\s*[}\]])", r"\1", text))


def param_lines(lines):
    """name then values, one per line, as in the .pet and .trl emitters; values are kept in order."""
    out = []
    for line in lines:
        out.append({"name": line[0], "values": [value(t) for t in line[1:]]})
    return out


def blocks(text):
    """.pet .trl: the optional count and version in either order, then { ... } emitters of name values lines; the first
    line of a .pet emitter is its type and the second its material, each alone on a line."""
    m = JSON_TAIL.search(text)
    tail = out_next = None
    if m:
        tail, after = json_values(text[m.start() + 1:])
        if after:  # one file holds another whole .pet after its JSON
            out_next = blocks(after)
        text = text[:m.start() + 1]
    c = Lines(text)
    out = {"version": None, "count": None, "emitters": []}
    if tail is not None:
        out["json"] = tail
    if out_next is not None:
        out["next"] = out_next
    while not c.done():
        line = c.next()
        if line[0] == "version":
            out["version"] = int(line[1])
        elif line == ["{"]:
            body = []
            while True:
                line = c.next()
                if line == ["}"]:
                    break
                body.append(line)
            out["emitters"].append(param_lines(body))
        else:
            need(len(line) == 1 and is_num(line[0]), "expected a count", line)
            out["count"] = value(line[0])
    return out


def effect_pack(text):
    """.epk: lines of Keyword "bone" "file" and a RenderPasses block of JSON."""
    c = Lines(text)
    out = {"render_passes": None, "entries": []}
    while not c.done():
        line = c.next()
        if line[0] == "RenderPasses":
            c.i -= 1
            depth, raw = 0, []
            first = True
            while True:
                line = c.lines[c.i]
                c.i += 1
                toks = line[1:] if first else line
                first = False
                raw += toks
                s = " ".join(toks)
                depth += s.count("{") - s.count("}")
                if depth == 0 and raw:
                    break
            out["render_passes"] = " ".join(raw)
        else:
            out["entries"].append({"kind": line[0], "args": [value(t) for t in line[1:]]})
    return out


def graph(text):
    """.tgr .dgr: version, Size:, MasterFile:, Nodes:, Edges:, then the header strings and node/edge lines. Node:
    x y edgeCount edge... "tag" rotation nameCount "name"... numbers (scripts: TerrainFiles.GraphFile)."""
    c = Lines(text)
    out = {"version": c.version()}
    nodes = edges = 0
    while c.peek() and c.peek()[0].endswith(":") and c.peek()[0] not in ("Default%:",):
        line = c.next()
        key = line[0][:-1]
        v = [value(t) for t in line[1:]]
        out[key] = v[0] if len(v) == 1 else v
        if key == "Nodes":
            nodes = int(line[1])
        elif key == "Edges":
            edges = int(line[1])
    out["strings"] = []
    while c.peek() and all(is_str(t) for t in c.peek()):
        out["strings"].append([value(t) for t in c.next()])
    out["defaults"] = []
    while c.peek() and c.peek()[0].endswith(":"):
        line = c.next()
        out["defaults"].append([value(t) for t in line])
    while c.peek() and len(c.peek()) == 1 and is_num(c.peek()[0]):
        out["defaults"].append([value(t) for t in c.next()])
    out["nodes"] = []
    for _ in range(nodes):
        t = c.next()
        need(len(t) >= 4 and is_num(t[0]) and is_num(t[1]) and is_num(t[2]), "expected a node", t)
        n = int(t[2])
        need(len(t) > 3 + n and is_str(t[3 + n]), "expected the node's tag", t)
        out["nodes"].append({"x": value(t[0]), "y": value(t[1]), "edges": [value(x) for x in t[3:3 + n]],
                             "tag": value(t[3 + n]), "rest": [value(x) for x in t[4 + n:]]})
    out["edges"] = []
    for _ in range(edges):
        out["edges"].append([value(x) for x in c.next()])
    out["rest"] = [[value(x) for x in line] for line in c.rest()]
    return out


def room(text):
    """.arm .red: the string dictionary, then the room's rows. Reading the rows' meaning is TerrainFiles.RoomFile's and
    the level generator's job, so the rows stay in file order and split by kind."""
    c = Lines(text)
    out = {"version": c.version(), "dictionary": []}
    count = c.next()
    need(len(count) == 1 and is_num(count[0]), "expected the dictionary size", count)
    for _ in range(int(count[0])):
        s = c.next()
        need(len(s) == 1 and is_str(s[0]), "expected a dictionary string", s)
        out["dictionary"].append(value(s[0]))
    rows = []
    for line in c.rest():
        rows.append(line)
    out["slots"] = []
    out["rows"] = []
    for line in rows:
        if line[0] in ("k", "n", "s", "f", "o"):
            out["slots"].append(slot_row(line))
        else:
            out["rows"].append([value(t) for t in line])
    return out


def slot_row(t):
    slots, i = [], 0
    while i < len(t):
        k = t[i]
        i += 1
        if k == "k":
            j = i
            while j < len(t) and is_num(t[j]):
                j += 1
            slots.append({"k": [value(x) for x in t[i:j]]})
            i = j
        elif k == "f":
            slots.append({"f": value(t[i])})
            i += 1
        elif k in ("n", "s", "o"):
            slots.append(k)
        else:
            raise FormatError(f"unknown slot {k!r}: {t[:14]}")
    return slots


def plain_lines(text):
    return {"lines": re.split(r"\r\n|\n|\r", text.lstrip("﻿"))}


def ui(text):
    """.ui: <SECTIONS>, include "file", "template Name ... end" and "begin Type ... end" blocks of "key = value;"."""
    tokens = re.findall(r'//[^\r\n]*|"[^"]*"|<[A-Za-z_]+>|[=;]|[^\s=;"]+', text)
    root = {"items": []}
    stack = [root]
    i = 0
    while i < len(tokens):
        tok = tokens[i]
        if tok.startswith("//"):
            i += 1
        elif tok == "end":
            if len(stack) == 1:
                raise FormatError(f"end without begin at token {i}")
            stack.pop()
            i += 1
        elif tok in ("begin", "template"):
            block = {"block": tok, "type": tokens[i + 1], "items": []}
            i += 2
            if tokens[i:i + 1] == ["based"]:  # template X based Y
                i += 2 if tokens[i + 1] == "on" else 1  # or: based on Y
                block["based"] = tokens[i]
                i += 1
            stack[-1]["items"].append(block)
            stack.append(block)
        elif tok.startswith("<") and tok.endswith(">"):
            stack[-1]["items"].append({"section": tok[1:-1]})
            i += 1
        elif tok == "include":
            stack[-1]["items"].append({"include": tokens[i + 1].strip('"')})
            i += 2
        elif tokens[i + 1:i + 2] == ["="]:
            j = i + 2
            while j < len(tokens) and tokens[j] != ";":
                j += 1
            if j == len(tokens):
                raise FormatError(f"no ; after {tok}")
            stack[-1]["items"].append({"key": tok, "value": " ".join(x for x in tokens[i + 2:j] if not x.startswith("//"))})
            i = j + 1
        else:  # const NAME = value; and other statements of several words
            j = i
            while j < len(tokens) and tokens[j] not in (";", "begin", "end", "template"):
                j += 1
            if tokens[j:j + 1] != [";"]:
                raise FormatError(f"unexpected {tok!r} at token {i}")
            stack[-1]["items"].append({"statement": " ".join(x for x in tokens[i:j] if not x.startswith("//"))})
            i = j + 1
    if len(stack) != 1:
        raise FormatError(f"{len(stack) - 1} blocks not closed")
    return root


def shader_fragments(text):
    """.ffx: DECLARATIONS, FRAGMENT, EXTENSIONPOINT and GROUP blocks of header lines (include, in, out, uniform, cost: ...),
    each with an optional {{ HLSL }} body. Lines before the first block are kept as a preamble."""
    pieces = re.split(r"\{\{(.*?)\}\}", text, flags=re.S)
    out = {"preamble": [], "blocks": []}
    block = None
    for i, piece in enumerate(pieces):
        if i % 2:
            if block is None:
                raise FormatError("code before the first block")
            block.setdefault("code", []).append(piece)
            continue
        for line in piece.splitlines():
            line = line.strip()
            if not line:
                continue
            word = line.split()[0]
            if word in ("DECLARATIONS", "FRAGMENT", "EXTENSIONPOINT", "GROUP"):
                block = {"kind": word, "name": line[len(word):].strip(), "lines": []}
                out["blocks"].append(block)
            elif block is None:
                out["preamble"].append(line)
            else:
                block["lines"].append(line)
    if "{{" in "".join(pieces[::2]) or "}}" in "".join(pieces[::2]):
        raise FormatError("unbalanced code braces")
    return out


PARSERS = {
    ".ui": ui,
    ".tsi": key_values, ".properties": plain_lines, ".rs": weighted_list, ".toy": weighted_list, ".tst": tile_set,
    ".gft": named_groups, ".mtd": materials, ".ddt": doodads_or_decals, ".dct": doodads_or_decals,
    ".clt": doodads_or_decals, ".cht": chests, ".ecf": table, ".gcf": table, ".tmo": table, ".dlp": doodad_layer,
    ".atlas": atlas, ".fgp": file_groups, ".edp": edge_points, ".gt": ground_type, ".et": edge_type, ".sm": skin_mesh,
    ".tgt": tile_geometry, ".amd": animation_metadata, ".act": actions, ".pet": blocks, ".trl": blocks,
    ".epk": effect_pack, ".tgr": graph, ".dgr": graph, ".arm": room, ".red": table, ".slt": plain_lines,
    ".log": plain_lines, ".filter": plain_lines, ".ffx": shader_fragments,
}
