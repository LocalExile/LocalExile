"""Decode bounded entity, combat and NPC evidence using this checkout's extracted tables."""
from collections import Counter, defaultdict
import json
import math
from pathlib import Path
import struct
from analyze import compact


def table(name):
    return json.loads(Path("data/tables", name + ".json").read_text(encoding="utf-8-sig"))


def object_hash(text):
    data = bytes(ord(c) & 255 for c in text)
    value, multiplier = len(data), 0x5bd1e995
    for offset in range(0, len(data)//4*4, 4):
        part = int.from_bytes(data[offset:offset+4], "little") * multiplier & 0xffffffff
        part ^= part >> 24
        value = (value * multiplier & 0xffffffff) ^ (part * multiplier & 0xffffffff)
    tail = data[len(data)//4*4:]
    if tail:
        value = (value ^ int.from_bytes(tail, "little")) * multiplier & 0xffffffff
    value = (value ^ (value >> 13)) * multiplier & 0xffffffff
    return value ^ (value >> 15)


class Reader:
    def __init__(self, data):
        self.data, self.offset = data, 0
    def take(self, count):
        if count < 0 or self.offset + count > len(self.data):
            raise ValueError("Truncated component prefix")
        result = self.data[self.offset:self.offset+count]
        self.offset += count
        return result
    def num(self, count, endian="little"):
        return int.from_bytes(self.take(count), endian)
    def unsigned(self):
        value, end = compact(self.data, self.offset)
        first = self.data[self.offset]
        bits = 7 if first < 128 else 14 if first < 192 else 21 if first < 224 else 28 if first < 240 else 32
        self.offset = end
        return value & ((1 << bits) - 1)
    def signed(self):
        value, self.offset = compact(self.data, self.offset)
        return value


def records(folder):
    data = (folder / "payloads.bin").read_bytes()
    for number, line in enumerate((folder / "events.jsonl").read_text(encoding="utf-8").splitlines(), 1):
        event = json.loads(line)
        value = event["message"].get("payload", {})
        if value.get("kind") != "bytes" or not value["accepted"]:
            continue
        offset, count = event["offset"], event["length"]
        if offset + count <= len(data):
            yield number, value, data[offset:offset+count]


def positioned(reader):
    x, y, rotation, team = struct.unpack("<iifH", reader.take(14))
    flags0, flags1 = reader.num(1), reader.num(1)
    if flags0 & ~0xe7 or flags1 & ~0x3f:
        raise ValueError("Unsupported positioned flags")
    if flags0 & 0x20: reader.take(12)
    if flags1 & 4: reader.take(16)
    if flags0 & 4: reader.take(8)
    if flags0 & 0x40: reader.take(8)
    if flags1 & 1: reader.take(18)
    if flags1 & 2: reader.take(2)
    if flags1 & 8: reader.take(1)
    if flags1 & 16: reader.take(4)
    return x,y,team


def object_base(reader):
    """0x141dea920 / 0x141dfaf40: count, then key/flags/typed values. Type 1 is u32 entity references."""
    flags = reader.num(1)
    if flags & ~3:
        raise ValueError("Unsupported object flags")
    entries = []
    for _ in range(reader.num(1)):
        key, entry_flags, count = reader.num(4), reader.num(1), reader.num(1)
        kind = reader.num(1) if count else None
        if count and kind != 1:
            raise ValueError("Unsupported base-data value type " + str(kind))
        entries.append(dict(key=hex(key),flags=entry_flags,type=kind,values=[reader.num(4) for _ in range(count)]))
    return flags, entries


def player_prefix(blob):
    reader=Reader(blob)
    object_base(reader)
    positioned(reader)
    for _ in range(reader.unsigned()): reader.unsigned();reader.signed()
    reader.take(9)
    if reader.num(2): raise ValueError("Player initial buffs not decoded")
    pools=[]
    for _ in range(3):
        pools.append(reader.num(4));reader.take(14)
    reader.num(1)
    animated=reader.num(1)
    if animated & ~3: raise ValueError("Unsupported player Animated flags")
    if animated & 1 and not animated & 2: reader.take(reader.num(4)*2)
    reader.take(reader.num(4)*2)  # discard character name
    character_class,experience=reader.num(1),reader.num(4)
    reader.take(8)
    flags=[]
    quest_rows=table("QuestFlags")
    for _ in range(reader.num(2)):
        block,bits=reader.num(1),reader.num(8)
        flags.extend(quest_rows[block*64+bit]["Id"] for bit in range(64) if bits & (1<<bit))
    return dict(class_index=character_class,experience=experience,pools=pools,quest_flags=flags)


def initial_buffs(reader, count, definitions):
    """Creation reader 0x141e114d0 -> 0x141e06bb0; distinct from the compact 0x1c1 update layout."""
    result = []
    for _ in range(count):
        instance, flags, row, visual = reader.num(2), reader.num(1), reader.num(2), reader.num(2)
        if row >= len(definitions):
            raise ValueError("Invalid initial buff definition row")
        definition = definitions[row]
        # These captured backends consume no extra serialized state in 0x141e11dc0's callback.
        # Other backends may read more after the common data; do not guess the following Life offset.
        if definition["Id"] not in {"hidden_monster", "kitava_heart_damage_taken"}:
            return result, "Initial buff backend not decoded: " + definition["Id"]
        fields = {offset: reader.num(size) for offset, size in [
            ("0x28", 4), ("0x2c", 4), ("0x38", 2), ("0x34", 4), ("0x3a", 2),
            ("0x46", 2), ("0x44", 2), ("0x48", 4), ("0x3c", 1)]}
        value = reader.num(2) if definition["__col44"] else None
        recovery = [reader.num(4) for _ in definition["GrantedStats"]] if definition["IsRecovery"] else []
        values = [reader.num(4) for _ in range(reader.num(4))]
        result.append(dict(instance=instance, flags=flags, row=row, id=definition["Id"], visual=visual,
                           native_fields=fields, extra_value=value, recovery_values=recovery, values=values))
    return result, None


def monster_prefix(blob, mods, stats, buffs=None):
    reader=Reader(blob)
    object_flags,base_entries=object_base(reader)
    x,y,team=positioned(reader)
    values = {}
    for _ in range(reader.unsigned()):
        key, value = reader.unsigned(), reader.signed()
        values[stats[key-1]["Id"]] = value
    reader.take(9)  # Stats tail, recorded independently of its values
    flags, unknown = reader.num(2), reader.num(1)
    modifier_rows = []
    def mod_list():
        for _ in range(reader.num(1)):
            row = mods[reader.num(2)]
            pairs = []
            for index in range(1, 9):
                key = row.get("StatsKey" + str(index))
                if key is None:
                    break
                pairs.append((stats[key]["Id"], reader.signed()))
            modifier_rows.append(dict(id=row["Id"], stats=dict(pairs)))
    for bit in (4, 6, 10, 11):
        if flags & (1 << bit):
            mod_list()
    if flags & 8:
        if flags & 128:
            reader.take(reader.num(4) * 2)
        elif flags & 256:
            reader.take(reader.num(1) * 4)
        if flags & 32:
            mod_list()
    reader.num(1)  # ObjectMagicProperties own flags
    buff_count = reader.num(2)
    decoded_buffs, stop = ([], None)
    if buff_count:
        decoded_buffs, stop = initial_buffs(reader, buff_count, buffs if buffs is not None else table("BuffDefinitions"))
    if stop:
        return dict(position=[x,y],team=team,stats=values,rarity=flags & 7,mods=modifier_rows,base_entries=base_entries,
                    pools=[],stop_reason=stop,buff_count=buff_count,initial_buffs=decoded_buffs,
                    decoded_prefix_bytes=reader.offset,remaining_blob_bytes=len(blob)-reader.offset)
    pools = []
    for _ in range(3):
        current, reserved = reader.num(4), reader.num(4)
        reader.take(10)
        pools.append(dict(current=current, reserved=reserved))
    life_flags = reader.num(1)
    return dict(position=[x,y], team=team, stats=values, rarity=flags & 7, mods=modifier_rows,base_entries=base_entries,
                pools=pools, life_flags=life_flags, initial_buffs=decoded_buffs, decoded_prefix_bytes=reader.offset,
                remaining_blob_bytes=len(blob)-reader.offset)


def skill_stat_update(packet, stats):
    reader = Reader(packet[12:])
    skill, selector = reader.num(2, "big"), reader.num(1)
    pairs = []
    for _ in range(reader.unsigned()):
        key, value = reader.unsigned(), reader.signed()
        if not 1 <= key <= len(stats):
            raise ValueError("Invalid skill-update stat key")
        pairs.append(dict(key=key, id=stats[key-1]["Id"], value=value))
    if reader.offset != len(packet)-12:
        raise ValueError("Unconsumed skill-update bytes")
    return dict(skill=skill, stat_set=selector, pairs=pairs)


def buff_update_prefix(packet, buffs):
    reader = Reader(packet[12:])
    instance, row = reader.num(2, "big"), reader.num(2, "big")
    if row >= len(buffs):
        raise ValueError("Invalid buff-update row")
    source, skill, flags = reader.num(4, "big"), reader.num(2, "big"), reader.num(1)
    if flags & 1:
        reader.take(8)
    duration_bits = reader.num(4, "big") if flags & 2 else None
    duration = struct.unpack(">f", duration_bits.to_bytes(4, "big"))[0] if duration_bits is not None else None
    rate_bits = reader.num(4, "big") if flags & 128 else None
    # An absent stack field means the native default of one, not zero. Repeated adds can update one instance.
    stacks = reader.num(2, "big") if flags & 4 else 1
    active = dict(action=reader.num(2, "big"), skill=reader.num(2, "big")) if flags & 8 else None
    visual = reader.num(2, "big") if flags & 16 else None
    display = reader.num(1) if flags & 32 else None
    # Backend-specific extra/recovery fields and the final list remain deliberately undecoded.
    if len(packet)-12-reader.offset < 4:
        raise ValueError("Truncated buff-update tail")
    return dict(kind="add", instance=instance, row=row, buff=buffs[row]["Id"], source=source, source_skill=skill,
                flags=flags, duration_bits=duration_bits,
                duration_seconds=duration if duration is not None and math.isfinite(duration) else None,
                rate_bits=rate_bits, stacks=stacks, active_skill=active, visual=visual, display_state=display,
                decoded_prefix_bytes=12+reader.offset, remaining_bytes=len(packet)-12-reader.offset)


def study_semantics(folder):
    npcs, varieties, talks, categories = table("NPCs"), table("MonsterVarieties"), table("NPCTalk"), table("NPCTalkCategory")
    stat_rows, mod_rows, buff_rows = table("Stats"), table("Mods"), table("BuffDefinitions")
    mods = {row["HASH16"]:row for row in mod_rows}
    types = {object_hash(row["Id"]):dict(kind="npc", name=row["Name"], path=row["Id"], row=index)
             for index,row in enumerate(npcs)}
    for index,row in enumerate(varieties):
        for level in (1,2):
            types[object_hash(row["Id"] + "@" + str(level))] = dict(kind="monster",name=row["Name"],path=row["Id"],row=index,level=level)
    entities = {}
    monsters, dialogue, life, hits, failures = [], [], [], [], []
    players, player_failures, local_ids = [], [], {}
    skill_stats, skill_stat_failures = [], []
    buff_updates, buff_update_failures = [], []
    for number, value, packet in records(folder):
        if value["direction"] != "S2C":
            continue
        opcode, timestamp, stream = value["opcode"], value["started"], value["stream"]
        if opcode == 0x22:
            local_ids[stream]=int.from_bytes(packet[2:6],"big")
        if opcode == 0x2b9 and int.from_bytes(packet[2:6],"big")==local_ids.get(stream):
            try:
                players.append(dict(record=number,stream=stream,time=timestamp,**player_prefix(packet[18:])))
            except (ValueError,IndexError,KeyError,struct.error) as error:
                player_failures.append(dict(record=number,reason=str(error)))
        if opcode == 0x2b9:
            entity = int.from_bytes(packet[2:6], "big")
            info = types.get(int.from_bytes(packet[12:16], "big"))
            if not info:
                continue
            info = dict(info, entity=entity, stream=stream, time=timestamp, record=number)
            entities[(stream,entity)] = info
            if info["kind"] == "monster":
                length = int.from_bytes(packet[16:18], "big")
                blob = packet[18:]
                if length != len(blob):
                    failures.append(dict(record=number, reason="Entity blob length mismatch"))
                    continue
                try:
                    info["prefix"] = monster_prefix(blob, mods, stat_rows, buff_rows)
                except (ValueError, IndexError, KeyError, struct.error) as error:
                    failures.append(dict(record=number, name=info["name"], reason=str(error)))
                monsters.append(info)
        if opcode == 0x84:
            entity, option = int.from_bytes(packet[2:6], "big"), packet[-1]
            info = entities.get((stream,entity), {})
            matches = [(index,row) for index,row in enumerate(talks) if row["NPCKey"] == info.get("row") and row["__col1"] == option] if entity and option != 255 else []
            dialogue.append(dict(time=timestamp, record=number, entity=entity, npc=info.get("name"), option=option,
                                 rows=[dict(row=index, option=row["DialogueOption"], category=categories[row["Category"]]["Id"]) for index,row in matches]))
        if opcode == 0x1b1 and len(packet)==24:
            entity=int.from_bytes(packet[2:6],"big")
            info=entities.get((stream,entity),{})
            life.append(dict(time=timestamp,record=number,stream=stream,entity=entity,name=info.get("name"),
                             current=int.from_bytes(packet[12:16],"big"),pool=packet[20]))
        if opcode == 0x1af:
            hits.append(dict(time=timestamp,record=number,stream=stream,actor=int.from_bytes(packet[2:6],"big"),
                             sequence=int.from_bytes(packet[12:14],"big"),target=int.from_bytes(packet[16:20],"big"),
                             flags=hex(int.from_bytes(packet[20:22],"big"))))
        if opcode == 0x1b4:
            try:
                skill_stats.append(dict(record=number, stream=stream, time=timestamp,
                                        entity=int.from_bytes(packet[2:6],"big"),
                                        **skill_stat_update(packet, stat_rows)))
            except (ValueError, IndexError, KeyError, struct.error) as error:
                skill_stat_failures.append(dict(record=number, reason=str(error)))
        if opcode in (0x1c1, 0x1c2):
            try:
                if opcode == 0x1c1:
                    update = buff_update_prefix(packet, buff_rows)
                elif len(packet) == 14:
                    update = dict(kind="remove", instance=int.from_bytes(packet[12:14], "big"))
                else:
                    raise ValueError("Unsupported buff-removal length")
                buff_updates.append(dict(record=number, stream=stream, time=timestamp,
                                         entity=int.from_bytes(packet[2:6], "big"), **update))
            except (ValueError, IndexError, KeyError, struct.error) as error:
                buff_update_failures.append(dict(record=number, reason=str(error)))
    return dict(monsters=monsters,dialogue=dialogue,life=life,hits=hits,prefix_failures=failures,players=players,
                player_prefix_failures=player_failures,skill_stats=skill_stats,skill_stat_failures=skill_stat_failures,
                buff_updates=buff_updates,buff_update_failures=buff_update_failures)


if __name__ == "__main__":
    import argparse
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder",type=Path)
    args=parser.parse_args()
    report=study_semantics(args.folder)
    (args.folder/"semantics.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
    print(json.dumps(dict(monsters=len(report["monsters"]),prefix_failures=report["prefix_failures"][:4],
                          dialogue_count=len(report["dialogue"]), decoded_prefixes=sum("prefix" in m for m in report["monsters"]),
                          skill_stat_updates=len(report["skill_stats"]), skill_stat_failures=report["skill_stat_failures"][:4],
                          buff_updates=len(report["buff_updates"]), buff_update_failures=report["buff_update_failures"][:4]),indent=2))
