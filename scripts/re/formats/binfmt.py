# The binary formats of the game data. Every parser reads through a Reader, so it can tell how many bytes it understood.
# A parser that stops at a payload it can't read yet names that in "undecoded_bytes"; decode_all.py reports those files
# as partial, not as decoded.
import os
import struct
import subprocess
import sys


class FormatError(Exception):
    pass


class Reader:
    def __init__(self, data):
        self.b = data
        self.i = 0

    def left(self):
        return len(self.b) - self.i

    def take(self, n):
        if n < 0 or self.i + n > len(self.b):
            raise FormatError(f"read of {n} at {self.i} past the end ({len(self.b)})")
        v = self.b[self.i:self.i + n]
        self.i += n
        return v

    def u8(self):
        return self.take(1)[0]

    def u16(self):
        return struct.unpack("<H", self.take(2))[0]

    def u32(self):
        return struct.unpack("<I", self.take(4))[0]

    def u64(self):
        return struct.unpack("<Q", self.take(8))[0]

    def i32(self):
        return struct.unpack("<i", self.take(4))[0]

    def f32(self):
        return struct.unpack("<f", self.take(4))[0]

    def floats(self, n):
        return list(struct.unpack(f"<{n}f", self.take(4 * n)))

    def finish_padded(self, result):
        """A .header is padded with zeros to at least the size of the client's object."""
        if any(self.b[self.i:]):
            raise FormatError(f"{self.left()} bytes left at {self.i} of {len(self.b)}, not padding")
        self.i = len(self.b)
        return result

    def finish(self, result):
        if self.left():
            raise FormatError(f"{self.left()} bytes left at {self.i} of {len(self.b)}")
        return result

    def partial(self, result, what):
        """The rest of the file is a payload this parser does not read yet."""
        result["undecoded_bytes"] = self.left()
        result["undecoded"] = what
        return result


_gm_tables = None


def gm_tables():
    """Names for the hashes in a .gm, from the generated data/tables; a hash without a name stays a number."""
    global _gm_tables
    if _gm_tables is None:
        import json
        root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
        _gm_tables = []
        for name in ("Stats", "GrantedEffects"):
            try:
                rows = json.load(open(os.path.join(root, "data", "tables", name + ".json"), encoding="utf-8"))
            except OSError:
                rows = []
            _gm_tables.append({x["HASH32"]: x["Id"] for x in rows})
    return _gm_tables


def grandmaster(b):
    """.gm, a character snapshot for the Hall of Grandmasters. The client has no reader for it, so the layout comes from
    its counts adding up to the last byte in every file and its hashes matching Stats and GrantedEffects. Field names
    without a table behind them are neutral."""
    r = Reader(b)
    if r.take(4) != b"GM1\0":
        raise FormatError("not GM1")
    out = {"n": r.u32()}
    pairs = r.u16()
    out.update(a=r.u8(), level=r.u8())
    length, items, sections, passives = r.u8(), r.u8(), r.u8(), r.u8()
    out["name"] = r.take(length).decode("utf-8")
    stats, effects = gm_tables()
    out["stats"] = []
    for _ in range(pairs):
        key, value = r.u32(), r.i32()
        out["stats"].append([stats.get(key, key), value])
    out["items"] = [{"slot": r.u8(), "byte": r.u8(), "u16": r.u16(), "u32": r.u32(), "u16b": r.u16()} for _ in range(items)]
    def gem():
        key = r.u32()
        out = {"effect": effects.get(key, key), "level": r.u8(), "index": r.u8(), "quality": r.u8(), "stats": []}
        for _ in range(r.u8()):
            stat = r.u32()
            out["stats"].append([stats.get(stat, stat), r.i32()])
        return out

    out["skills"] = []
    for _ in range(sections):
        slot, count = r.u16(), r.u16()
        out["skills"].append({"slot": slot, "gems": [gem() for _ in range(count)]})
    out["entries"] = []
    for _ in range(passives):
        entry = {"byte": r.u8(), "byte2": r.u8(), "id": r.u16()}
        entry["data"] = r.take(r.u16()).hex()
        out["entries"].append(entry)
    return r.finish(out)


def passive_graph(b):
    """.psg, the layout of src/GameServer.Data/PassiveSkillGraph.cs."""
    r = Reader(b)
    out = {"version": r.u8(), "kind": r.u8()}
    if out["version"] != 3:
        raise FormatError(f"version {out['version']}")
    out["orbits"] = list(r.take(r.u8()))
    out["roots"] = [r.u32() for _ in range(r.u32())]
    out["groups"] = []
    for _ in range(r.u32()):
        g = {"x": r.f32(), "y": r.f32(), "association": r.u32(), "background": r.u32(), "jewel_reference": r.u8(), "nodes": []}
        for _ in range(r.u32()):
            g["nodes"].append({"id": r.u32(), "orbit": r.u32(), "position": r.u32(),
                               "connections": [r.u32() for _ in range(r.u32())]})
        out["groups"].append(g)
    return r.finish(out)


OODLE = None


def oodle(compressed, size):
    """Tools/ooz/ooz_worker.mjs, one per process."""
    global OODLE
    if OODLE is None:
        repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
        ooz = os.path.join(repo, "Tools", "ooz")
        OODLE = subprocess.Popen(["node", os.path.join(ooz, "ooz_worker.mjs")], stdin=subprocess.PIPE, stdout=subprocess.PIPE, cwd=ooz)
    OODLE.stdin.write(struct.pack("<II", size, len(compressed)) + compressed)
    OODLE.stdin.flush()
    status, length = struct.unpack("<II", OODLE.stdout.read(8))
    data = OODLE.stdout.read(length)
    if status:
        raise FormatError(f"oodle: {data.decode()}")
    return data


CLIENT_OODLE = None


def client_oodle(block, size):
    """The client's own decoder (oodle_client.py), which needs Unicorn, from Tools/re/py."""
    global CLIENT_OODLE
    if CLIENT_OODLE is None:
        here = os.path.dirname(os.path.abspath(__file__))
        sys.path[:0] = [os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(here))), "Tools", "re", "py"), here]
        import oodle_client
        CLIENT_OODLE = oodle_client.ClientOodle()
    data = CLIENT_OODLE.decompress(block, size)
    if data is None:
        raise FormatError("oodle: the client's decoder refuses the block")
    return data


def bundle(r):
    """The block format of the game's bundles: sizes, then a head with the compressor, block count and block size."""
    size, _, head = r.u32(), r.u32(), r.u32()
    at = r.i + head
    r.u32(), r.u32(), r.u64(), r.u64()
    blocks, granularity = r.u32(), r.u32()
    r.take(16)
    sizes = [r.u32() for _ in range(blocks)]
    if r.i != at:
        raise FormatError(f"bundle head ends at {r.i}, not {at}")
    out = bytearray()
    for s in sizes:
        block, want = r.take(s), min(granularity, size - len(out))
        try:
            out += oodle(block, want)
        except FormatError:
            out += client_oodle(block, want)  # ooz-wasm rejects a few small blocks
    if len(out) != size:
        raise FormatError(f"bundle gave {len(out)} of {size}")
    return bytes(out)


TRACK_KEY_SIZES = (16, 20, 16, 16, 20, 16)  # time and scale, time and rotation (x y z w), time and translation; twice


def clip_tracks(data, clip, version):
    """A clip's tracks in the unpacked data, one per bone: a byte, the bone, six key counts (from version 11 a u32 more)
    and the keys, all plain floats. Keys are not copied into the JSON: track["keys_offset"] says where they are."""
    r = Reader(data)
    r.i = clip["offset"]
    tracks = []
    for _ in range(clip["bones"]):
        track = {"byte": r.u8(), "bone": r.u32(), "keys": [r.u32() for _ in range(6)]}
        if version >= 11:
            track["value"] = r.u32()
        track["keys_offset"] = r.i
        r.take(sum(n * s for n, s in zip(track["keys"], TRACK_KEY_SIZES)))
        tracks.append(track)
    if r.i != clip["offset"] + clip["size"]:
        raise FormatError(f"clip {clip['name']} ends at {r.i}, not {clip['offset'] + clip['size']}")
    return tracks


def skeleton(b):
    """.ast: version, bone count, a byte, clip count (u32: a player class has thousands), light count. Bones: next sibling and first child (255 =
    none), the 4x4 matrix, name length, a byte, the name. Lights: a name length, then data (56 bytes including the length
    in version 8, 60 later), the name. Clips: bone count, a byte, frame rate, a letter (o, n, l), a byte, name length (from
    version 11 with the length of a second name after it), offset and size in the unpacked data, the names. Then one bundle with all clips."""
    r = Reader(b)
    out = {"version": r.u8(), "bone_count": r.u8(), "byte": r.u8()}
    clip_count = r.u32()
    light_count = r.u8()
    out["bones"], out["lights"], out["clips"] = [], [], []
    for _ in range(out["bone_count"]):
        bone = {"sibling": r.u8(), "child": r.u8(), "matrix": r.floats(16)}
        length = r.u8()
        bone["flag"] = r.u8()
        bone["name"] = r.take(length).decode("ascii")
        out["bones"].append(bone)
    for _ in range(light_count):
        length = r.u8()
        light = {"data": r.take((56 if out["version"] == 8 else 60) - 1).hex()}
        light["name"] = r.take(length).decode("ascii")
        out["lights"].append(light)
    for _ in range(clip_count):
        clip = {"bones": r.u8(), "byte": r.u8(), "fps": r.u8(), "kind": chr(r.u8())}
        if out["version"] >= 11:
            clip["byte2"] = r.u8()
            length = r.u8()
            base_length = r.u8()
        else:
            length, base_length = r.u8(), 0
        clip["offset"], clip["size"] = r.u32(), r.u32()
        clip["name"] = r.take(length).decode("ascii")
        if base_length:
            clip["base"] = r.take(base_length).decode("ascii")  # open_01 of open
        out["clips"].append(clip)
    if clip_count:
        data = bundle(r)
        for clip in out["clips"]:
            clip["tracks"] = clip_tracks(data, clip, out["version"])
    return r.finish(out)


# The vertex layout of a mesh, from its flags (0x14105b090). The names are what the fields look like; only position is proven.
VERTEX_FIELDS = [(0x200, "position", 12), (0x10, "normal", 4), (0x20, "tangent", 4), (0x08, "uv", 4), (0x80, "uv1", 8),
                 (0x100, "uv2", 8), (0x04, "skin", 8), (0x01, "color0", 4), (0x02, "color1", 4), (0x40, "wind", 4)]
LIGHT_SIZE = {2: 0x2d, 3: 0x46, 4: 0x4e, 5: 0x52, 6: 0x53}  # a .fmt or old .tgm version's record; the others are 0x57
TILE_RECORD_SIZE = {9: 26, 10: 28, 11: 28, 12: 30, 13: 30, 14: 30}  # .tgm version 9 and up


def vertex_layout(flags):
    fields, at = {}, 0
    for bit, name, size in VERTEX_FIELDS:
        if flags & bit:
            fields[name] = at
            at += size
    return fields, at


def geometry(r, flags, triangles, vertices, shapes=0, indices_first=True):
    """One index and vertex block. The geometry is not copied into the JSON (14 GB of it): this says where it is."""
    fields, stride = vertex_layout(flags)
    index_size = 4 if vertices > 0xffff else 2
    out = {"triangles": triangles, "vertices": vertices, "index_size": index_size, "vertex_stride": stride, "fields": fields}
    if indices_first:
        out["index_offset"] = r.i
        r.take(triangles * 3 * index_size)
    out["vertex_offset"] = r.i
    r.take(vertices * stride)
    if not indices_first:
        out["index_offset"] = r.i
        r.take(triangles * 3 * index_size)
    return out


def dolm(r):
    """A "DOLm" chunk (0x1429eb550): the LODs of a mesh."""
    if r.take(4) != b"DOLm":
        raise FormatError(f"no DOLm at {r.i - 4}")
    version, lod_count = r.u16(), r.u8()
    shapes = r.u16()
    flags = r.u32() | 0x200
    counts = [(r.u32(), r.u32()) for _ in range(lod_count)]
    lods = []
    for triangles, vertices in counts:
        ranges = [[r.u32(), r.u32()] for _ in range(shapes)]
        lod = geometry(r, flags, triangles, vertices)
        lod["shape_ranges"] = ranges
        if flags & 0x40:
            r.take(shapes * (0x28 if version < 3 else 0x24))
        if version > 3:
            n = r.u32()
            if n:
                lod["lookup_size"] = n
                lod["lookup_offset"] = r.i
                r.take(n * n * lod["index_size"])
        lods.append(lod)
    return {"version": version, "shapes": shapes, "flags": flags, "lods": lods}


def bbox(r):
    return r.floats(6)


def light(d):
    """A light by its record size (0x1429e2160). Size 0x2d and 0x46 hold the colour as 4 bytes of which it uses 3, 8 less
    than the three floats, so every later field is 8 lower; the client turns them into the longer layout. Tools saved uninitialised memory
    in unused fields of some old records. Only colour, position and direction are named from their use."""
    r = Reader(d)
    out = {"kind": r.u32()}
    if len(d) < 0x4e:
        out["color"] = [c / 255 for c in r.take(4)[:3]]
    else:
        out["color"] = r.floats(3)
    out.update(position=r.floats(3), direction=r.floats(3), radius=r.f32(), a=r.f32(), b=r.f32(), flag=r.u8())
    if r.left():
        out.update(vector=r.floats(3), vector2=r.floats(3), flag2=r.u8())
    if len(d) >= 0x52:
        out["value"] = r.f32()
    if len(d) >= 0x53:
        out["byte"] = r.u8()
    if len(d) == 0x57:
        out["value2"] = r.f32()
    return r.finish(out)


def lights(r, count, version):
    size = LIGHT_SIZE.get(version, 0x57)
    return [light(r.take(size)) for _ in range(count)]


def tile_record(d):
    """A .tgm record, the shape it covers: u16 index, then a value (u16 in versions 10 and 11, u32 in 12 and up), the bounding
    box as min and max x, y, z. Up to version 8 the box comes first, with the shape's first triangle and triangle count after."""
    r = Reader(d)
    out = {"index": r.u16()}
    if len(d) == 0x22:
        out["bbox"] = r.floats(6)
        out["first_triangle"], out["triangles"] = r.u32(), r.u32()
    else:
        if len(d) > 26:
            out["value"] = int.from_bytes(r.take(len(d) - 26), "little")
        out["bbox"] = r.floats(6)
    return r.finish(out)


def string_table(r):
    table = utf16_table(r.take(2 * r.u32()))

    def at(offset):
        if offset >= len(table):
            raise FormatError(f"string offset {offset} past the table ({len(table)})")
        return table[offset:table.index("\0", offset)]
    return at


def fixed_mesh(b):
    """.fmt (0x1429e8fd0): counts and the bounding box, the mesh (a DOLm from version 9, one LOD before), the names of its
    shapes and point groups, the points, lights, a string table."""
    r = Reader(b)
    out = {"kind": "fixed", "version": r.u8()}
    v = out["version"]
    if v == 9:
        shapes, groups, points, light_count = r.u16(), r.u8(), r.u16(), r.u8()
        out["bbox"] = bbox(r)
        out["mesh"] = dolm(r)
        shape_table = [(r.u32(), r.u32()) for _ in range(shapes)]
    else:
        triangles, vertices, shapes, groups, points, light_count = r.u32(), r.u32(), r.u16(), r.u8(), r.u16(), r.u8()
        out["bbox"] = bbox(r)
        flags = 0x238 | (r.u32() if v == 8 else 0)
        shape_table = [(r.u32(), r.u32(), r.u32()) for _ in range(shapes)]
    group_table = [(r.u8(), r.u8(), r.u32()) for _ in range(groups)]
    if v != 9 and (triangles or vertices or shapes):
        out["mesh"] = {"flags": flags, "shapes": shapes, "lods": [geometry(r, flags, triangles, vertices)]}
    point_data = [r.floats(3) for _ in range(points)]
    out["lights"] = lights(r, light_count, v)
    name = string_table(r)
    out["shape_names"] = [{"name": name(s[0]), "material": name(s[1])} for s in shape_table]
    out["point_groups"] = []
    for g in group_table:
        out["point_groups"].append({"byte": g[0], "name": name(g[2]), "points": point_data[:g[1]]})
        point_data = point_data[g[1]:]
    return r.finish(out)


def tile_mesh(b):
    """.tgm (0x1429e2160): the render mesh, its records, the ground mesh."""
    r = Reader(b)
    out = {"kind": "tile", "version": r.u8()}
    v = out["version"]
    out["bbox"] = bbox(r)
    if v >= 9:
        records, ground_records, out["ground_kind"], light_count = r.u16(), r.u16(), r.u8(), r.u8()
        out["mesh"] = dolm(r)
        size = TILE_RECORD_SIZE[v]
        out["records"] = [tile_record(r.take(size)) for _ in range(records)]
        out["ground_mesh"] = dolm(r)
        out["ground_records"] = [tile_record(r.take(26)) for _ in range(ground_records)]
    else:
        records, vertices, triangles = r.u16(), r.u32(), r.u32()
        ground_records, ground_vertices, ground_triangles = r.u16(), r.u32(), r.u32()
        out["ground_kind"], light_count = r.u8(), r.u8()
        flags = 0x238 | (r.u32() if v == 8 else 0)
        out["records"] = [tile_record(r.take(0x22)) for _ in range(records)]
        out["mesh"] = {"flags": flags, "lods": [geometry(r, flags, triangles, vertices, indices_first=False)]}
        out["ground_records"] = [tile_record(r.take(0x22)) for _ in range(ground_records)]
        # 0x143014ad0 by ground kind; the strides are measured, its flags are not read from the table
        stride = {1: 20, 2: 24, 3: 28}.get(out["ground_kind"], 0)
        if ground_vertices and not stride:
            raise FormatError(f"ground kind {out['ground_kind']}")
        out["ground_mesh"] = {"vertices": ground_vertices, "triangles": ground_triangles, "vertex_stride": stride,
                              "vertex_offset": r.i}
        r.take(ground_vertices * stride)
        out["ground_mesh"]["index_offset"] = r.i
        r.take(ground_triangles * 3 * (4 if ground_vertices > 0xffff else 2))
    out["lights"] = lights(r, light_count, v)
    return r.finish(out)


def skinned_mesh(b):
    """.smd (0x1429e6ec0): the mesh, then a table of (0x2c) entries and four sections the client reads into an object
    with vtable slots 0x48 to 0x50. Sections hold 0x40 or 0x14 byte records, with a name from sub version 4."""
    r = Reader(b)
    out = {"kind": "skinned", "version": r.u8()}
    v = out["version"]
    if v == 3:
        r.u8()
        n, blob = r.u16(), r.u32()
        out["bbox"] = bbox(r)
        out["mesh"] = dolm(r)
        out["shape_values"] = [r.u32() for _ in range(n)]
    else:
        triangles, vertices = r.u32(), r.u32()
        r.u8()
        n, blob = r.u16(), r.u32()
        out["bbox"] = bbox(r)
        flags = 0x23c if v == 1 else r.u32() | 0x238
        out["shape_values"] = [[r.u32(), r.u32()] for _ in range(n)]
    names = r.take(blob)
    out["shape_names"], at = [], 0
    for value in out["shape_values"]:  # the name's length in bytes; before version 3 also where the shape's triangles start
        length = value if v == 3 else value[0]
        out["shape_names"].append(names[at:at + length].decode("utf-16-le", "replace"))
        at += length
    if v != 3:
        out["mesh"] = {"flags": flags, "lods": [geometry(r, flags, triangles, vertices)]}
    if r.left():
        out["extras"] = skin_extras(r)
    return r.finish(out)


def skin_ellipsoid(r):
    """A collision ellipsoid, by its names in the files: rotation, centre, radii, bone."""
    return {"rotation": r.floats(9), "position": r.floats(3), "radii": r.floats(3), "bone": r.u32()}


def skin_sphere(r):
    return {"position": r.floats(3), "radius": r.f32(), "bone": r.u32()}


def skin_extras(r):
    v = r.u32()
    second = third = fourth = extra = 0
    if v == 1:
        first, entries, values = r.u32(), r.u32(), r.u32()
    elif v == 2:
        first, entries, values, extra = r.u32(), r.u32(), r.u32(), r.u32()
    elif v in (3, 4):
        first, second, third, fourth, entries, values, extra = [r.u32() for _ in range(7)]
    else:
        raise FormatError(f"skin extras version {v}")
    out = {"version": v, "entries_offset": r.i, "entries": entries, "values": values}
    r.take(entries * 0x2c + values * 4)

    def section(count, read):
        items = []
        for _ in range(count):
            item = read(r)
            if v == 4:
                item["name"] = r.take(r.u32()).decode("utf-16-le", "replace")
            items.append(item)
        return items
    out["ellipsoids"] = section(first, skin_ellipsoid)
    out["spheres"] = section(second, skin_sphere)
    out["links"] = [[r.u32(), r.u32()] for _ in range(third)]
    out["fourth"] = section(fourth, skin_ellipsoid)
    out["trailing"] = [r.u32() for _ in range(extra)]
    return out


def utf16_table(b):
    return b.decode("utf-16-le", "replace")


def tile_grid(r, version, size):
    """A packed grid of a .tdt (0x1429f5ca0): before version 10 the raw bytes. After that a byte with the kind in its low 3
    bits, the number of extra size bytes in the next 2 and the low bits of the size in the top 3. Kind 0 stores no data,
    its "size" is the value of every cell; kinds 1 to 4 hold size packed bytes."""
    if version < 10:
        r.take(size)
        return {"kind": "raw", "size": size}
    first = r.u8()
    kind, value = first & 7, first >> 5
    for i in range((first >> 3) & 3):
        value |= r.u8() << (8 * i + 3)
    if kind > 4:
        raise FormatError(f"grid kind {kind}")
    if not kind:
        return {"kind": 0, "fill": value}
    r.take(value)
    return {"kind": kind, "packed": value}


def tile_definition(b):
    """.tdt (0x1429f31a0): u32 version, u32 string table length (UTF-16 units), the table, the parent .tdt. A file with a
    parent has a mask byte saying which groups it overrides: 1 tgt and sub tile data, 2 tag, 4 third name, 8 size and
    sub tiles, 0x10 grids and sub tile grids, 0x20 the nibble list, 0x40 the trailing pairs. See also
    src/GameServer.Data/Terrain/TerrainFiles.cs (TileDefinition)."""
    r = Reader(b)
    version = r.u32()
    out = {"version": version}
    length = r.u32()
    table = utf16_table(r.take(2 * length))
    out["strings"] = [s for s in table.split("\0") if s]

    def s(offset):
        if offset == 0xffffffff:
            return ""
        if offset >= len(table):
            raise FormatError(f"string offset {offset} past the table ({len(table)})")
        end = table.find("\0", offset)
        return table[offset:end if end >= 0 else len(table)]
    out["parent"] = s(r.u32())
    has_parent = bool(out["parent"])
    mask = r.u8() if has_parent else 0xff
    if has_parent:
        out["mask"] = mask
    tgt = s(r.u32()) if mask & 1 else ""
    out["tgt"] = tgt
    width = height = 0
    if mask & 8:
        out["tag"] = s(r.u32())
        out["edges"] = [s(r.u32()) for _ in range(4)]
        width, height = r.u8(), r.u8()
        out["grounds"] = [s(r.u32()) for _ in range(4)]
        out["edge_bytes"] = list(r.take(4))
        h = r.take(8)
        out["points"] = [h[0], h[4], h[1], h[5], h[2], h[6], h[3], h[7]]
    elif mask & 0x10:
        width, height = r.u8(), r.u8()
    out["width"], out["height"] = width, height
    flags = r.u16()
    if version < 14:
        flags &= 0xfeff
    if version < 13:
        flags &= 0xfdff
    out["flags"] = flags
    if version > 9 and mask & 0x20:
        out["byte2"] = r.u8()
    if mask & 0x10:
        count = r.u8() if version > 9 else 1
        if count > 4:
            raise FormatError(f"{count} grid values")
        out["grid_values"] = list(r.take(count)) if count > 1 else count
    if mask & 1 and flags & 0x200:
        out["sub_tile_byte"] = r.u8()
    if mask & 0x18:
        sub_tiles = out["sub_tiles"] = []
        for _ in range(r.u16()):
            sub = {}
            sub_flags = 0
            if mask & 0x10 or (mask & 1 and flags & 0x200):
                sub_flags = sub["flags"] = r.u16() if version > 9 else r.u8()
            if mask & 8:
                sub["kind"] = r.u8()
            if mask & 1 and flags & 0x200:
                sub["bytes"] = list(r.take(3))
            if mask & 0x10:
                if not sub_flags & 4:
                    sub["grid"] = tile_grid(r, version, 0x211)  # 0x1429f59a0
                if not sub_flags & 1 or not sub_flags & 2:
                    sub["extra"] = r.take(r.u16()).hex()
            sub_tiles.append(sub)
    if mask & 8:
        corners = (width + 1) * (height + 1)
        if flags & 2:
            out["corner_names"] = [s(r.u32()) for _ in range(corners)]
        if flags & 1:
            out["corner_values"] = list(r.take(corners))
    if mask & 2:
        out["name2"] = s(r.u32())
    if mask & 4:
        out["name3"] = s(r.u32())
    if mask & 0x20:
        out["byte3"] = r.u8()
    # The client reads the rest only under two global switches (0x1445b38cd, 0x14458159b); the bytes are always in the file.
    if mask & 0x20:
        if version < 10:
            out["nibbles"] = r.take(2).hex()
        else:
            nibbles = []  # each byte: bit 7 more follow, bits 4-6 index, bits 0-3 value
            for i in range(8):
                value = r.u8()
                if i == 7 and value & 0x80:
                    raise FormatError("nibble list too long")
                if not value:
                    break
                nibbles.append(value)
                if not value & 0x80:
                    break
            out["nibbles"] = nibbles
    if mask & 0x10:
        size = width * height
        if flags & 0x10:
            out["grid"] = tile_grid(r, version, size * 0x211)
            if version > 13:
                out["grid_names"] = [r.take(2 * r.u16()).decode("utf-16-le") for _ in range(r.u8())]
        if flags & 0x20:
            r.take(size * 4 if version < 10 else size)
            out["cell_bytes"] = size
        if version > 10:
            out["grids"] = [tile_grid(r, version, size * 0x211) for _ in range(4 if version > 11 else 3)]
    if version > 14 and mask & 0x40 and r.left():
        out["pairs"] = [[r.take(2 * r.u16()).decode("utf-16-le") for _ in range(2)] for _ in range(r.u8())]
    return r.finish(out)


def header_text(r):
    return r.take(r.u64()).decode("utf-8", "replace")


def header_mesh(r):
    """The common part of a .fmt or .smd .header (0x1411cc510)."""
    out = {"triangles": r.u32(), "vertices": r.u32(), "n": r.u32(), "bbox": bbox(r), "byte": r.u8(), "flags": r.u32() | 0x200}
    out["lods"] = [r.take(6).hex() for _ in range(r.u64())]
    out["value"] = r.u32()
    return out


def header_tile_mesh(r):
    """One mesh of a .tgm .header (0x1411cd180)."""
    out = {"records": [{"bbox": r.floats(6), "values": [r.u32() for _ in range(4)]} for _ in range(r.u64())], "values": [r.u32() for _ in range(r.u64())],
           "lods": [r.take(6).hex() for _ in range(r.u64())]}
    out.update(triangles=r.u32(), vertices=r.u32(), a=r.u32(), b=r.u32(), flags=r.u32() | 0x200, bytes=list(r.take(3)))
    return out


def mesh_summary(b, kind):
    """<mesh>.header: what the client needs of a mesh before streaming it (0x1411cc630, 0x1411ccb30, 0x141146e60). It is
    the mesh's own data again, without the geometry, in a layout with u64 counts."""
    r = Reader(b)
    if kind == ".tgm":
        out = {}
        if r.u8():
            out["mesh"] = header_tile_mesh(r)
        if r.u8():
            out["ground_mesh"] = header_tile_mesh(r)
        out["byte"] = r.u8()
        out["bbox"] = bbox(r)
        out["ground_kind"] = r.u8()
        out["lights"] = [light(r.take(0x57)) for _ in range(r.u64())]
        return r.finish_padded(out)
    out = header_mesh(r)
    if kind == ".fmt":
        out["point_groups"] = [{"name": header_text(r), "points": [r.floats(3) for _ in range(r.u64())]} for _ in range(r.u64())]
        out["lights"] = [light(r.take(0x57)) for _ in range(r.u64())]
        out["shapes"] = [{"values": [r.u32(), r.u32()], "name": header_text(r), "material": header_text(r)} for _ in range(r.u64())]
        return r.finish_padded(out)
    out["shapes"] = [{"start": r.u32(), "count": r.u32(), "name": header_text(r)} for _ in range(r.u64())]
    out["bytes"] = list(r.take(2))
    out["ellipsoids"] = [{**skin_ellipsoid(r), "name": header_text(r)} for _ in range(r.u64())]
    out["spheres"] = [{**skin_sphere(r), "name": header_text(r)} for _ in range(r.u64())]
    out["links"] = [[r.u32(), r.u32()] for _ in range(r.u64())]
    out["fourth"] = [{**skin_ellipsoid(r), "name": header_text(r)} for _ in range(r.u64())]
    if r.left() >= 16 and any(r.b[r.i:]):
        out["entries"] = [r.take(0x2c).hex() for _ in range(r.u64())]
        out["values"] = [r.u32() for _ in range(r.u64())]
        out["trailing"] = [r.u32() for _ in range(r.u64())]
    return r.finish_padded(out)


def texture_summary(b):
    """<texture>.dds.header: version, width, height, mip count, a kind, the size of what follows, the bytes the texture
    streams from, then the start of the DDS: its header and its smallest mips."""
    r = Reader(b)
    out = {"version": r.u32(), "width": r.u32(), "height": r.u32(), "mips": r.u32(), "kind": r.u32()}
    size = r.u32()
    out["total"] = r.u32()
    if size != r.left() or b[r.i:r.i + 4] != b"DDS ":
        raise FormatError(f"size {size}, {r.left()} bytes left")
    out["dds_header"] = b[r.i:r.i + 128].hex()
    out["mip_bytes"] = r.left() - 128
    r.i = len(b)
    return out


def header(rel, b):
    kind = rel[:-len(".header")]
    kind = kind[kind.rfind("."):].lower()
    return texture_summary(b) if kind == ".dds" else mesh_summary(b, kind)


def tile_mesh_description(b):
    """.tmd binary, version 5: entries of six floats and three string offsets, a string table. Version 6 (rare) has eight
    floats and a byte after the offsets."""
    r = Reader(b)
    out = {"version": r.u8()}
    v6 = out["version"] == 6
    entries = []
    for _ in range(r.u32()):
        floats, a, b_, c = r.floats(8 if v6 else 6), r.u32(), r.u32(), r.u32()
        if v6:
            r.u8()
        entries.append((floats, a, b_, c))
    table = utf16_table(r.take(2 * r.u32()))

    def s(offset):
        if offset == 0xffffffff:
            return None
        if offset >= len(table):
            raise FormatError(f"string offset {offset} of {len(table)}")
        return table[offset:table.index("\0", offset)]
    out["entries"] = [{"floats": e[0], "strings": [s(e[1]), s(e[2]), s(e[3])]} for e in entries]
    return r.finish(out)


_item_tables = None


def item_tables():
    """Item types and mods from the generated data/ files; without them .itemblob stops at its type hash."""
    global _item_tables
    if _item_tables is None:
        import json
        import os
        root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
        try:
            types = json.load(open(os.path.join(root, "data", "item-types.json"), encoding="utf-8"))["types"]
            mods = json.load(open(os.path.join(root, "data", "tables", "Mods.json"), encoding="utf-8"))
        except OSError:
            _item_tables = ({}, {})
        else:
            _item_tables = ({murmur2(x["type"]): x for x in types}, {m["HASH16"]: m for m in mods})
    return _item_tables


def murmur2(text):
    """InstanceEntities.ObjectTypeHash: the BaseItemTypes HASH32 of a type's path."""
    m, mask = 0x5bd1e995, 0xffffffff
    b = text.encode("ascii")
    h, p = len(b), 0
    while len(b) - p >= 4:
        k = struct.unpack_from("<I", b, p)[0] * m & mask
        h = h * m & mask ^ (k ^ k >> 24) * m & mask
        p += 4
    if len(b) - p:
        h ^= int.from_bytes(b[p:], "little")
        h = h * m & mask
    h = (h ^ h >> 13) * m & mask
    return h ^ h >> 15


def compact(r):
    """Signed compact (0x141e490e0): 7, 14, 21 or 28 bits by the leading bits of the first byte, else 4 more bytes."""
    first = r.u8()
    for extra, prefix in ((0, 0), (1, 0x80), (2, 0xc0), (3, 0xe0)):
        bits = 7 - extra
        if first >> bits == prefix >> bits:
            value = first & (1 << bits) - 1
            for _ in range(extra):
                value = value << 8 | r.u8()
            size = bits + 8 * extra
            return value - (1 << size) if value >> size - 1 else value
    return r.i32()


class NeedGuess(Exception):
    """An old blob names a mod the tables no longer have; item_blob tries each number of values for it."""


MOD_GUESSES = None  # the values of each unknown mod in this attempt; None = an unknown mod is an error
MOD_CHANGES = None  # [changed mods, values each known mod has more than the table now gives]; None = the table is right


def item_mod_list(r):
    mods = item_tables()[1]
    out = []
    for _ in range(r.u8()):
        key = r.u16()
        mod = mods.get(key)
        if mod is None:
            if MOD_GUESSES is None:
                raise FormatError("unknown mod hash")
            if not MOD_GUESSES[1]:
                raise NeedGuess
            count = MOD_GUESSES[1].pop(0)
            MOD_GUESSES[0] += 1
            out.append({"unknown_mod": key, "values": [compact(r) for _ in range(count)]})
            continue
        count = sum(1 for k, v in mod.items() if k.startswith("StatsKey") and v is not None)
        entry = {"mod": mod["Id"]}
        if MOD_CHANGES is not None:
            if not MOD_CHANGES[1]:
                raise NeedGuess
            change = MOD_CHANGES[1].pop(0)
            if change:
                if count + change < 0:
                    raise FormatError("mod without values")
                MOD_CHANGES[0] += 1
                entry["table_values"] = count
                count += change
        entry["values"] = [compact(r) for _ in range(count)]
        out.append(entry)
    return out


def item_magic(r):
    """ObjectMagicProperties (0x142201d70, protocol/13-items.md 2a)."""
    flags = r.u16()
    out = {"rarity": flags & 7, "identified": bool(flags & 8), "fractured": r.u8()}
    if flags & 0x200:
        out["alternate_quality"] = list(r.take(2))
    for bit, name in ((0x10, "implicit"), (0x40, "enchant"), (0x400, "scourge"), (0x800, "hidden")):
        if flags & bit:
            out[name] = item_mod_list(r)
    if flags & 8:
        if flags & 0x80:
            out["name"] = r.take(2 * r.u32()).decode("utf-16-le")
        elif flags & 0x100:
            out["words"] = [r.u32() for _ in range(r.u8())]
        if flags & 0x20:
            out["explicit"] = item_mod_list(r)
    return out


def item_body(r, model):
    """An item (0x1421ec390) in one of the layouts blobs were saved in. model: flags width, Mods flags width, bytes after
    Mods, bytes after SkillGem. Components without a known layout raise FormatError with the name."""
    flag_width, mods_flags, mods_tail, gem_tail = model[:4]
    old_mods = len(model) > 4 and model[4]
    old_fields = len(model) > 5 and model[5]  # no byte for Mods bit 0x10, no u32 in Map; 2: Map has 2 bytes as well
    type_hash = r.u32()
    kind = item_tables()[0].get(type_hash)
    if kind is None:
        raise FormatError("unknown item type")
    flags = r.u8() if flag_width == 1 else r.u16()
    if flag_width == 2 and flags >> 9 & 1:
        r.u64()
    out = {"type": kind["type"], "flags": flags}
    components = kind["components"]
    if old_mods and "Mods" not in components:  # a graft had Mods when the blob was saved
        components = components[:1] + ["Mods"] + components[1:]
    for name in components:
        if name in ("LocalStats", "AttributeRequirements", "Weapon", "Usable", "Flask", "Quest", "Shield", "RenderItem",
                    "DeepwaterChart", "UltimatumTrial", "MercenaryContract"):
            continue
        if name == "Base":
            base = {"visual": r.u16(), "byte": r.u8(), "byte2": r.u8()}
            bits = r.u16()
            base.update(bits=bits, influences=r.u8())
            if bits & 1:
                base["value"] = r.u8()
            if bits & 0x1000:
                base["pair"] = [r.u32(), r.u32()]
            if bits & 4:
                base["text"] = r.take(2 * r.u32()).decode("utf-16-le")
            elif bits & 2:
                base["flavour"] = r.u16()
            out[name] = base
        elif name == "Mods":
            mods = {"item_level": r.u8(), "required_level": r.u8()}
            bits = r.u8() if mods_flags == 1 else r.u16()
            if bits & 0x40:
                r.take(5)
            if bits & 0x10 and not old_fields:
                r.u8()
            if bits & 0x80:
                raise FormatError("Mods record")
            mods.update(bits=bits, magic=item_magic(r), tail=r.take(mods_tail).hex())
            out[name] = mods
        elif name == "SkillGem":
            out[name] = {"experience": r.i32(), "variant": r.u32(), "flags": r.u8(), "tail": r.take(gem_tail).hex()}
        elif name in ("Quality", "Armour", "Tincture", "SentinelDrone", "ArchnemesisMod"):
            out[name] = r.u8()
        elif name == "LakeTablet":
            out[name] = r.take(27).hex()
        elif name == "NecropolisCorpse":
            out[name] = {"u16": r.u16(), "bytes": list(r.take(2))}
        elif name == "SanctumKey":
            head = list(r.take(2))
            out[name] = {"bytes": head, "values": [r.u32() for _ in range(4)], "data": r.take(0x11).hex(),
                         "deferred_rewards": list(r.take(r.u8()))}
        elif name == "Stack":
            out[name] = r.u16()
        elif name == "Charges":
            out[name] = compact(r)
        elif name == "Map":
            data = list(r.take(2 if old_fields == 2 else 3))
            out[name] = {"bytes": data, "value": r.u32() if not old_fields and data[2] else None}
        elif name == "MapKey":
            bits = r.u8()
            out[name] = {"bits": bits, "u32": r.u32() if bits & 1 else None, "u16": r.u16() if bits & 2 else None,
                         "u8": r.u8() if bits & 4 else None}
        elif name == "Sockets":
            bits = r.u8()
            sockets = []
            if not bits & 3:
                for _ in range(bits >> 2 & 7):
                    socket = r.u8()
                    sockets.append({"colour": socket >> 1, "gem": item_body(r, model) if socket & 1 else None})
                out["links"] = list(r.take(bits >> 5))
            out[name] = sockets
        else:
            raise FormatError(f"component {name}")
    return out


def item_blob(b):
    """.itemblob, a saved item for the league panel. The layout changed between leagues (the 1-byte flags are older than
    the current client's u16), so every layout is tried and one counts only if it uses every byte. What no layout reads
    stays undecoded, with its bytes."""
    r = Reader(b)
    out = {"type_hash": r.u32()}
    last = None
    # A zero after the flags byte is the high byte of u16 flags: nothing else there is ever zero. Only the leagues that
    # kept u8 Mods flags have the marker byte (Base +2) at 0x29 to 0x2f.
    width = 2 if b[5] == 0 else 1
    marker = b[8] if width == 2 else b[7]
    mods_flags = (1, 2) if 0x29 <= marker <= 0x2f else (2, 1)
    for model in ((width, m, t, g) for m in mods_flags for t in (0, 1, 2) for g in (0, 2)):
        attempt = Reader(b)
        try:
            item = item_body(attempt, model)
        except FormatError as x:
            last = str(x)
            continue
        if not attempt.left():
            out.update(layout=list(model), item=item)
            r.i = len(b)
            return r.finish(out)
    found = old_item_blob(b, width, mods_flags)
    if found:
        out.update(found)
        r.i = len(b)
        return r.finish(out)
    out["hex"] = b[4:].hex()
    return r.partial(out, f"item body ({last})")


OLD_ITEM_BYTES = {"DeepwaterChart", "UltimatumTrial", "MercenaryContract", "Quest"}  # read nothing now, had bytes then


def old_item_blob(b, width, mods_flags):
    """A blob saved before its item's tables or layout changed: known mods with a value more or fewer than the tables give
    now, mods the tables no longer have (their value counts are what makes the whole file parse), fields that came later,
    and bytes at the end of a component that reads none now. Every key being a known mod is the stronger evidence, so an
    unknown mod is tried only if nothing else fits. Misaligned readings still land on real mod keys, but of other domains and
    with values far from the table's ranges, so the reading kept has the fewest such mods and values, then changes least, then
    has the fewest bytes left, then assumes the fewest tail bytes; "readings" says how many fit as well."""
    names = item_tables()[0][struct.unpack_from("<I", b)[0]]["components"]
    old = next((n for n in names if n in OLD_ITEM_BYTES), None)
    models = [(width, m, t, g, o, f) for f in (False, True, 2) for o in (False, True) for m in mods_flags for t in (0, 1, 2) for g in (0, 2)
              if not (o and "Mods" in names)]
    # Other Map layouts fit almost anything by moving bytes between the mods and Map, so each counts only if the one before fits
    # nothing: the current one, then 2 bytes (kept all six mods as the tables give them), then 3 bytes without the u32.
    current, short, no_u32 = ([m for m in models if m[5] is f] for f in (False, 2, True))
    for changes, group in ((True, current), (True, short), (True, no_u32), (False, current + no_u32)):
        found = old_item_search(b, group, old, changes)
        if found:
            best = min(f[:5] for f in found)
            found = [f for f in found if f[:5] == best]
            _, _, count, _, _, item, left, model = found[0]
            out = {"layout": list(model[:4]), "item": item, "changed_mods" if changes else "unknown_mods": count, "old_layout": True,
                   "readings": len({json_key(f[5]) for f in found})}
            if left:
                out["old_bytes"] = left.hex()
            return out
    return None


def old_item_search(b, models, old, changes):
    global MOD_CHANGES, MOD_GUESSES
    found = []
    budget = 200000
    for model in models:
        stack = [[]]
        while stack and budget:
            budget -= 1
            guess = stack.pop()
            MOD_CHANGES = [0, list(guess)] if changes else None
            MOD_GUESSES = None if changes else [0, list(guess)]
            attempt = Reader(b)
            try:
                item = item_body(attempt, model)
            except NeedGuess:
                stack.extend(guess + [n] for n in ((0, -1, 1) if changes else range(5)))
                continue
            except FormatError:
                continue
            finally:
                count = MOD_CHANGES[0] if changes else MOD_GUESSES[0]
                MOD_CHANGES = MOD_GUESSES = None
            left = b[attempt.i:]
            if left and not old and not (model[4] and not any(left)):
                continue
            found.append((*(mod_misfit(item) if changes else (0, 0)), count, len(left), model[2] + model[3], item, left, model))
    return found


def mod_misfit(item):
    """How many of an item's mods are outside the domain most of them share, and how many values are outside the table's range
    by more than the range's own size: a save can be older than the range, not ten times off it."""
    table = {m["Id"]: m for m in item_tables()[1].values()}
    magic = item.get("Mods", {}).get("magic", {})
    values = [(table[e["mod"]], e["values"]) for v in magic.values() if isinstance(v, list) for e in v if isinstance(e, dict)]
    domains = [m["Domain"] for m, _ in values]
    off = 0
    for mod, read in values:
        for i, value in enumerate(read, 1):
            low, high = mod.get(f"Stat{i}Min") or 0, mod.get(f"Stat{i}Max") or 0
            slack = max(abs(low), abs(high), 1)
            off += not low - slack <= value <= high + slack
    return len(domains) - max(map(domains.count, domains), default=0), off


def json_key(item):
    import json
    return json.dumps(item, sort_keys=True)


def minimap_atlas(b):
    """.mtp (0x141176370): a texture atlas. Version 1: u32 1, u32 count, count u32 texture name offsets. Version 0 has no
    version field and one name offset. Then u32 count and the entries, u32 string table length (UTF-16 units), the table.
    A version 1 entry is 20 bytes: name offset, frame, texture index, then u16s the client keeps only from offset 10; a
    version 0 entry is 32 bytes: name offset, frame (u32), two i32, four floats."""
    r = Reader(b)
    first = r.u32()
    version = 1 if first == 1 else 0
    names = [r.u32() for _ in range(r.u32())] if version else [first]
    count = r.u32()
    if version:
        entries = [struct.unpack("<IBB7H", r.take(20)) for _ in range(count)]
    else:
        entries = [struct.unpack("<IIii4f", r.take(32)) for _ in range(count)]
    table = utf16_table(r.take(2 * r.u32()))

    def s(offset):
        if offset >= len(table):
            raise FormatError(f"string offset {offset} past the table ({len(table)})")
        return table[offset:table.index("\0", offset)]
    out = {"version": version, "textures": [s(n) for n in names]}
    if version:
        out["entries"] = [{"name": s(e[0]), "frame": e[1], "texture": e[2], "values": list(e[3:])} for e in entries]
    else:
        out["entries"] = [{"name": s(e[0]), "frame": e[1], "values": list(e[2:])} for e in entries]
    return r.finish(out)


def jewel_distances(b):
    """.pjd (0x141efd480): u16 first, u16 second, u16 pair counts; first and second records of (u16 id, u16 start, u16
    count); the pairs: f32, u16 passive id, u16. Each record's range points into the pairs."""
    r = Reader(b)
    first, second, pairs = r.u16(), r.u16(), r.u16()
    records = lambda n: [list(struct.unpack("<3H", r.take(6))) for _ in range(n)]
    out = {"first": records(first), "second": records(second)}
    out["pairs"] = [list(struct.unpack("<fHH", r.take(8))) for _ in range(pairs)]
    return r.finish(out)


def sentinel_links(b):
    """.slg (0x1424da230): 16 raw bytes, kept at +0x10 of the graph."""
    r = Reader(b)
    return r.finish({"bits": r.take(16).hex()})


def sprite_font(b):
    """.spritefont, DirectXTK's format: "DXTKfont", u32 glyph count, glyphs (u32 character, four u32 source rectangle, three
    floats), f32 line spacing, u32 default character, then the sheet: u32 width, height, DXGI format, stride, rows, data."""
    r = Reader(b)
    if r.take(8) != b"DXTKfont":
        raise FormatError("not DXTKfont")
    glyphs = [struct.unpack("<5I3f", r.take(32)) for _ in range(r.u32())]
    out = {"glyphs": [{"char": g[0], "rect": list(g[1:5]), "offset": list(g[5:7]), "advance": g[7]} for g in glyphs],
           "line_spacing": r.f32(), "default_char": r.u32(), "width": r.u32(), "height": r.u32(), "format": r.u32()}
    stride, rows = r.u32(), r.u32()
    out.update(stride=stride, rows=rows)
    r.take(stride * rows)
    return r.finish(out)


def level_grid(b):
    """mud_level_data.bin: u32 width, u32 height, width x height bytes."""
    r = Reader(b)
    width, height = r.u32(), r.u32()
    cells = r.take(width * height)
    return r.finish({"width": width, "height": height, "min": min(cells), "max": max(cells), "nonzero": sum(1 for c in cells if c)})


DAT_SIZE = {"bool": 1, "string": 8, "i16": 2, "u16": 2, "i32": 4, "u32": 4, "f32": 4, "i64": 8, "u64": 8, "foreignrow": 16,
            "row": 8, "enumrow": 4, "rid": 8}
_dat_schema = None


def dat_schema():
    global _dat_schema
    if _dat_schema is None:
        import json
        import os
        root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
        tables = json.load(open(os.path.join(root, "data", "schema", "schema.min.json"), encoding="utf-8"))["tables"]
        _dat_schema = {}
        for table in tables:
            _dat_schema.setdefault(table["name"].lower(), []).append(table)
    return _dat_schema


def spirv_module(b):
    """Walks a SPIR-V module to its last word, returning the entry points."""
    if len(b) % 4 or b[:4] != b"\x03\x02\x23\x07":
        raise FormatError("not SPIR-V")
    version, generator, bound = struct.unpack_from("<III", b, 4)
    entries, i = [], 20
    while i < len(b):
        word = struct.unpack_from("<I", b, i)[0]
        count = word >> 16
        if not count or i + count * 4 > len(b):
            raise FormatError(f"SPIR-V instruction at {i} runs past the end")
        if word & 0xffff == 15:  # OpEntryPoint
            entries.append(b[i + 12:i + count * 4].split(b"\0")[0].decode("ascii"))
        i += count * 4
    return {"version": version, "generator": generator, "bound": bound, "entry_points": entries}


def dxbc_chunks(b):
    """DXBC container: magic, 16-byte hash, u32 1, u32 size, u32 chunk count, chunk offsets."""
    if b[:4] != b"DXBC" or struct.unpack_from("<I", b, 24)[0] != len(b):
        raise FormatError("not DXBC")
    count = struct.unpack_from("<I", b, 28)[0]
    chunks = []
    for i in range(count):
        at = struct.unpack_from("<I", b, 32 + i * 4)[0]
        size = struct.unpack_from("<I", b, at + 4)[0]
        if at + 8 + size > len(b):
            raise FormatError("DXBC chunk runs past the end")
        chunks.append({"tag": b[at:at + 4].decode("ascii"), "size": size})
    return chunks


def compiled_shader(b):
    """cachedhlslshaders/<api>/<hash> (path 0x141124ee0). First byte: 0x0f DX11, 0x0a DX12, 0x12 Vulkan. Reflection, then
    the bytecode. The first three u32 are the compute thread group, zero for other stages. The bytecode is left in the
    file: "bytecode" gives where."""
    r = Reader(b)
    kind = r.u8()
    if kind == 0x12:
        def text():
            end = b.index(0, r.i)
            return r.take(end - r.i + 1)[:-1].decode("ascii")

        def signature():
            return [{"a": r.u32(), "b": r.u32(), "name": text(), "c": r.u32()} for _ in range(r.u32())]

        out = {"api": "vulkan", "entry": text(), "thread_group": [r.u32() for _ in range(3)], "inputs": signature(),
               "outputs": signature(), "constant_buffers": []}
        for _ in range(r.u32()):
            buffer = {"a": r.u32(), "b": r.u32(), "name": text(), "size": r.u32()}
            buffer["fields"] = [{"name": text(), "size": r.u32(), "offset": r.u32()} for _ in range(r.u32())]
            out["constant_buffers"].append(buffer)
        out["resources"] = signature()
        out["buffers"] = [{"a": r.u32(), "b": r.u32(), "name": text(), "c": r.u8()} for _ in range(r.u32())]
        out["storage"] = signature()
        size = r.u32()
        out["bytecode"] = {"offset": r.i, "size": size, **spirv_module(r.take(size))}
        if out["entry"] not in out["bytecode"]["entry_points"]:
            raise FormatError("entry point not in the module")
        out["tail"] = r.take(5).hex()
        out["hash"] = r.u32()
        return r.finish(out)
    if kind not in (0x0a, 0x0f):
        raise FormatError(f"unknown shader kind {kind}")
    out = {"api": "dx11" if kind == 0x0f else "dx12", "thread_group": [r.u32() for _ in range(3)]}
    out["bindings"] = [{"hash": r.u32(), "values": [r.u32() for _ in range(5)]} for _ in range(r.u16())]
    out["variables"] = [{"hash": r.u32(), "values": [r.u32() for _ in range(4)]} for _ in range(r.u16())]
    out["value"] = r.u32()
    size = r.u32()
    out["bytecode"] = {"offset": r.i, "size": size, "chunks": dxbc_chunks(r.take(size))}
    out["hash"] = r.u32()
    return r.finish(out)


def dat_table(name, b):
    """.datc64, .datcl64 (src/GameServer.Data/Dat/DatFile.cs): checks a table against data/schema: the row size, and that
    every string and array in every row lands inside the variable data. The rows themselves are in data/tables."""
    rows = struct.unpack_from("<I", b)[0]
    marker = b.find(b"\xbb" * 8, 4)
    if marker < 0:
        raise FormatError("no marker")
    length = (marker - 4) // rows if rows else 0
    if rows and (marker - 4) % rows:
        raise FormatError(f"marker at {marker} is not on a row boundary")
    out = {"rows": rows, "row_length": length, "variable": len(b) - marker}
    tables = dat_schema().get(name)
    if tables is None:
        if rows:
            raise FormatError("rows but no schema table")
        out["schema"] = None
        return out
    size = lambda c: 16 if c["array"] else DAT_SIZE[c["type"]]
    table = next((t for t in tables if sum(size(c) for c in t["columns"]) == length), None)
    if table is None:
        if rows:
            raise FormatError(f"row length {length}, schema {[sum(size(c) for c in t['columns']) for t in tables]}")
        table = tables[0]
    variable = len(b) - marker
    offset = 0
    checks = []
    for c in table["columns"]:
        if c["array"]:
            checks.append((offset, True, size(c), c["type"]))
        elif c["type"] == "string":
            checks.append((offset, False, 8, "string"))
        offset += size(c)
    for row in range(rows):
        at = 4 + row * length
        for col, is_array, _, kind in checks:
            if is_array:
                count, where = struct.unpack_from("<QQ", b, at + col)
                if count and (where < 8 or where + count * (8 if kind == "string" else DAT_SIZE.get(kind, 1)) > variable):
                    raise FormatError(f"row {row}: array of {count} at {where} past {variable}")
            else:
                where = struct.unpack_from("<Q", b, at + col)[0]
                if where > variable and where != 0xfefefefefefefefe:
                    raise FormatError(f"row {row}: string at {where} past {variable}")
    out["columns"] = len(table["columns"])
    out["unnamed_columns"] = sum(1 for c in table["columns"] if not c["name"])
    return out


def opaque(b):
    return {"undecoded_bytes": len(b), "undecoded": "whole file", "head": b[:32].hex()}


def mat_table(b):
    """data/mat_table.dat (0x1410f5dd0, version 2): material hash pairs, keyed entries, and a u16 list."""
    r = Reader(b)
    if r.u32() != 2:
        raise FormatError("mat table version")
    pairs = [[r.u64(), r.u64()] for _ in range(r.u32())]
    keyed = [[r.u64(), r.u32(), r.u16()] for _ in range(r.u32())]
    values = [r.u16() for _ in range(r.u32())]
    return r.finish({"pairs": pairs, "keyed": keyed, "values": values})


def languages(b):
    """data/languages.dat: u32 count, then id, four NUL-terminated UTF-16 strings and three u32."""
    r = Reader(b)

    def text():
        out = []
        while True:
            unit = r.u16()
            if not unit:
                return struct.pack(f"<{len(out)}H", *out).decode("utf-16-le")
            out.append(unit)

    rows = [{"id": r.u32(), "name": text(), "native": text(), "code": text(), "short_code": text(),
             "values": [r.u32() for _ in range(3)]} for _ in range(r.u32())]
    return r.finish({"languages": rows})


PARSERS = {
    ".gm": grandmaster, ".psg": passive_graph, ".ast": skeleton, ".tdt": tile_definition, ".tmd": tile_mesh_description,
    ".fmt": fixed_mesh, ".smd": skinned_mesh, ".tgm": tile_mesh, ".itemblob": item_blob, ".spritefont": sprite_font, ".bin": level_grid, ".mtp": minimap_atlas, ".pjd": jewel_distances, ".slg": sentinel_links, ".dat": languages,
}


def parse(rel, data):
    ext = rel[rel.rfind("."):].lower()
    if ext == ".tmd" and data[:2] == b"\xff\xfe":
        return {"text": data.decode("utf-16")}
    if ext == ".header":
        return header(rel, data)
    if ext == ".dat":
        return mat_table(data) if "mat_table" in rel else languages(data)
    return PARSERS[ext](data)
