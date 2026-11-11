"""The files extract-data.ps1's generators write that the servers read, as client.db tables (Agents/docs/dev/databases.md,
"Generated tables"). client_sql.py diffs these tables like the client's own; the servers read them with
ClientTables.Table, the Python tools with clientdb.table.

Each generator's JSON becomes rows: a record list or map is a table (a map's key is a column), a record nested in
another is a row of a child table that names its parent's _row and its position (`ord`) in the parent's list, a nested
object of a record is flattened into `<field>_<key>` columns, and a list of numbers or strings is a JSON list column.
The tables are snake_case, the client's own PascalCase, so the two never collide.
"""
import json

# The generated files and the tables made from each. client-constants.json keeps only the HNC mask: the GGPK root hash
# describes the local Content.ggpk (the Patch server writes it to the local data/client-constants.json).
FILES = ["area-transitions.json", "room-points.json", "object-types.json", "monster-contacts.json", "monster-takehit.json",
         "item-types.json", "vendor-stock.json", "hideouts.json", "animation-contacts.json", "projectile-clips.json",
         "passive-skill-graphs.json", "delve-paths.json", "client-constants.json", "stat-descriptions.txt"]


def flat(record, skip=(), prefix=""):
    """A record's columns: scalars and lists as they are, nested objects as <field>_<key> (recursively)."""
    row = {}
    for key, value in record.items():
        if key in skip:
            continue
        name = prefix + key
        if isinstance(value, dict):
            row.update(flat(value, prefix=name + "_"))
        elif isinstance(value, list) and any(isinstance(v, (dict, list)) for v in value):
            raise ValueError(f"{name}: a list of records or lists needs a child table")
        else:
            row[name] = value
    return row


def child(parent_column, parent_row, ord_, record, skip=()):
    return {parent_column: parent_row, "ord": ord_, **flat(record, skip)}


def info(name, doc, keys):
    """The file's own fields (a note, a version) as a one-row table, objects as JSON."""
    row = {k: doc[k] for k in keys if k in doc}
    return {name: [row]} if row else {}


def area_transitions(doc):
    areas, connections, status, topologies, nodes, transitions = [], [], [], [], [], []
    for a, area in enumerate(doc["areas"]):
        areas.append(flat(area, skip=("connections", "topologies", "connectionStatus")))
        connections += [child("area", a, i, c) for i, c in enumerate(area["connections"])]
        status += [child("area", a, i, c) for i, c in enumerate(area["connectionStatus"])]
        for i, t in enumerate(area["topologies"]):
            topology = len(topologies)
            topologies.append(child("area", a, i, t, skip=("nodes", "transitions")))
            nodes += [child("topology", topology, k, n) for k, n in enumerate(t["nodes"])]
            transitions += [child("topology", topology, k, x) for k, x in enumerate(t["transitions"])]
    return {"area_transition_areas": areas, "area_transition_connections": connections,
            "area_transition_connection_status": status, "area_topologies": topologies,
            "area_topology_nodes": nodes, "area_topology_transitions": transitions,
            **info("area_transitions_info", {k: json.dumps(v, ensure_ascii=False) if isinstance(v, dict) else v
                                             for k, v in doc.items()}, ["about", "coverage", "missingFiles"])}


def room_points(doc):
    rooms, points, doodads = [], [], []
    for r, (room, value) in enumerate(doc["rooms"].items()):
        rooms.append({"room": room, **flat(value, skip=("points", "doodads"))})
        points += [child("room", r, i, p) for i, p in enumerate(value["points"])]
        doodads += [child("room", r, i, d) for i, d in enumerate(value["doodads"])]
    areas = [{"area": area, "rooms": list(names)} for area, names in doc["areas"].items()]
    return {"room_point_areas": areas, "room_point_rooms": rooms, "room_points": points, "room_doodads": doodads,
            **info("room_points_info", doc, ["note"])}


def object_types(doc):
    types, states = [], []
    for t, record in enumerate(doc["types"]):
        # positioned: whether the type has Positioned at all (897 have it with none of its fields set).
        types.append({**flat(record, skip=("shared_states",)), "positioned": isinstance(record.get("positioned"), dict)})
        states += [{"type": t, "ord": i, "name": s[0], "value": s[1]} for i, s in enumerate(record["shared_states"])]
    return {"object_types": types, "object_type_shared_states": states, **info("object_types_info", doc, ["generator"])}


def item_types(doc):
    return {"item_types": [flat(t) for t in doc["types"]], **info("item_types_info", doc, ["generator"])}


def monster_contacts(doc):
    monsters, clips = [], []
    for m, (variety, value) in enumerate(doc.items()):
        monsters.append({"variety": variety, **flat(value, skip=("pools",))})
        for pool, entries in value["pools"].items():
            clips += [{"monster": m, "pool": pool, **child("monster", m, i, c)} for i, c in enumerate(entries)]
    return {"monster_contacts": monsters, "monster_contact_clips": clips}


def monster_takehit(doc):
    return {"monster_takehit": [{"variety": variety, **flat(value)} for variety, value in doc.items()]}


def vendor_stock(doc):
    shops = [{"npc": npc, **flat(value)} for npc, value in doc["Shops"].items()]
    gems = [flat(g) for g in doc["Gems"]]
    layouts = [{"vendor": vendor, "tab": t, "row": r, "gems": row}
               for vendor, tabs in doc["GemLayouts"].items() for t, tab in enumerate(tabs) for r, row in enumerate(tab)]
    return {"vendor_shops": shops, "vendor_gems": gems, "vendor_gem_layouts": layouts,
            **info("vendor_stock_info", doc, ["Version", "Policy"])}


def hideouts(doc):
    rows, doodads = [], []
    for h, hideout in enumerate(doc["hideouts"]):
        rows.append(flat(hideout, skip=("doodads",)))
        doodads += [child("hideout", h, i, d) for i, d in enumerate(hideout["doodads"])]
    radii = [{"model": model, "radius": radius} for model, radius in doc["collisionRadii"].items()]
    return {"hideout_layouts": rows, "hideout_doodads": doodads, "hideout_collision_radii": radii,
            **info("hideouts_info", doc, ["note"])}


def animation_contacts(doc):
    """Per rig (.ao) its clips: animations[name] = [length ms, earliest contact ms], positions[name] = the contact point,
    cancels[name] = the lock_cancelling time (a null cancel is still listed: cancel_listed), contacts[name] = every
    contact frame [ms, point], indices[name] = the clip's index in the rig; and the default attack's stance pools."""
    rigs, clips, frames, attacks = [], [], [], []
    for r, (ao, value) in enumerate(doc.items()):
        rigs.append({"ao": ao, "amd": value.get("amd"), "act": value.get("act")})
        known = set(value) - {"amd", "act", "animations", "positions", "cancels", "contacts", "indices", "defaultAttack"}
        if known:
            raise ValueError(f"animation-contacts {ao}: unknown keys {sorted(known)}")
        names = list(dict.fromkeys([*value.get("animations", {}), *value.get("positions", {}), *value.get("cancels", {}),
                                    *value.get("contacts", {}), *value.get("indices", {})]))
        for name in names:
            animation = value.get("animations", {}).get(name)
            clip = len(clips)
            clips.append({"rig": r, "name": name,
                          "length": animation[0] if animation else None, "contact": animation[1] if animation else None,
                          "position": value.get("positions", {}).get(name),
                          "cancel_listed": name in value.get("cancels", {}), "cancel": value.get("cancels", {}).get(name),
                          "rig_index": value.get("indices", {}).get(name)})
            frames += [{"clip": clip, "ord": i, "ms": f[0], "position": f[1]}
                       for i, f in enumerate(value.get("contacts", {}).get(name) or [])]
        for stance, entries in value.get("defaultAttack", {}).items():
            attacks += [{"rig": r, "stance": stance, "ord": i, **flat(a)} for i, a in enumerate(entries)]
    return {"animation_rigs": rigs, "animation_clips": clips, "animation_clip_contacts": frames,
            "animation_default_attacks": attacks}


def projectile_clips(doc):
    return {"projectile_clips": [{"type": t, **flat(value)} for t, value in doc.items()]}


def passive_skill_graphs(doc):
    graphs, nodes, groups, members = [], [], [], []
    for name in ("normal", "phrecia"):
        if name not in doc:
            continue
        g = len(graphs)
        value = doc[name]
        graphs.append({"graph": name, **flat(value, skip=("nodes", "groups"))})
        nodes += [child("graph", g, i, n) for i, n in enumerate(value["nodes"])]
        for i, group in enumerate(value["groups"]):
            k = len(groups)
            groups.append(child("graph", g, i, group, skip=("nodes",)))
            members += [child("grp", k, j, n) for j, n in enumerate(group["nodes"])]
    return {"passive_graphs": graphs, "passive_graph_nodes": nodes, "passive_graph_groups": groups,
            "passive_graph_group_nodes": members,
            **info("passive_graphs_info", doc, ["revision", "analysisOnly", "mergedGraphFile", "originalPhreciaSha256"])}


def delve_paths(doc):
    rooms, nodes, links, spawns = [], [], [], []
    for r, (room, value) in enumerate(doc["rooms"].items()):
        rooms.append({"room": room})
        nodes += [child("room", r, i, n) for i, n in enumerate(value["nodes"])]
        links += [{"room": r, "ord": i, "from": a, "to": b} for i, (a, b) in enumerate(value["links"])]
        spawns += [child("room", r, i, s) for i, s in enumerate(value["spawns"])]
    return {"delve_path_rooms": rooms, "delve_path_nodes": nodes, "delve_path_links": links, "delve_path_spawns": spawns,
            **info("delve_paths_info", doc, ["version"])}


def client_constants(doc):
    return {"client_constants": [{"HncMask": doc["HncMask"]}]}


def stat_descriptions(text):
    """One row per description block: a line that does not start with whitespace and the indented lines after it,
    as its exact text."""
    blocks = []
    for line in text.splitlines(keepends=True):
        if not blocks or line[:1] not in (" ", "\t", "\r", "\n"):
            blocks.append(line)
        else:
            blocks[-1] += line
    return {"stat_descriptions": [{"block": b} for b in blocks]}


TABLES = {
    "area-transitions.json": area_transitions, "room-points.json": room_points, "object-types.json": object_types,
    "monster-contacts.json": monster_contacts, "monster-takehit.json": monster_takehit, "item-types.json": item_types,
    "vendor-stock.json": vendor_stock, "hideouts.json": hideouts, "animation-contacts.json": animation_contacts,
    "projectile-clips.json": projectile_clips, "passive-skill-graphs.json": passive_skill_graphs,
    "delve-paths.json": delve_paths, "client-constants.json": client_constants, "stat-descriptions.txt": stat_descriptions,
}


def tables(name, text):
    """The tables of one generated file, {table: [row dict]}, from its text."""
    if name.endswith(".txt"):
        return TABLES[name](text)
    return TABLES[name](json.loads(text))


def columns(rows):
    """Every column in first-seen order (records leave out keys they do not have)."""
    return list(dict.fromkeys(c for r in rows for c in r))
