"""Bounded equipped-item snapshot study; nested socket gems are decoded recursively."""
import argparse
import json
from semantics import Reader, records, table
from pathlib import Path

BASES = {row["HASH32"]:(index,row) for index,row in enumerate(table("BaseItemTypes"))}
TYPES = {row["type"]:row["components"] for row in json.loads(Path("data/item-types.json").read_text())["types"]}
MODS = {row["HASH16"]:row for row in table("Mods")}
STATS = table("Stats")
GEMS = {row["BaseItemTypesKey"]:row for row in table("SkillGems")}
XP = table("ItemExperiencePerLevel")


def item(reader, depth=0):
    if depth > 6:
        raise ValueError("Nested item limit")
    index, base = BASES[reader.num(4)]
    flags = reader.num(2)
    if flags & 0x200: reader.take(8)
    result = dict(name=base["Name"],type=base["Id"],mods=[])
    def mod_list():
        for _ in range(reader.num(1)):
            row = MODS[reader.num(2)]
            values = {}
            for key in range(1,9):
                stat = row.get("StatsKey"+str(key))
                if stat is None: break
                values[STATS[stat]["Id"]] = reader.signed()
            result["mods"].append(dict(id=row["Id"],stats=values))
    for component in TYPES[base["Id"]]:
        if component in ("LocalStats","AttributeRequirements","Weapon","Usable","Flask","Quest","Shield","RenderItem"):
            continue
        if component == "Base":
            reader.take(4)
            flags = reader.num(2)
            reader.num(1)
            if flags & ~0x882: raise ValueError("Unsupported item Base fields")
            if flags & 2: reader.take(2)
            if flags & 128:
                for _ in range(reader.num(1)): reader.num(4); reader.unsigned()
        elif component == "Quality": result["quality"]=reader.num(1)
        elif component == "Armour": result["defence_roll"]=reader.num(1)
        elif component == "Charges": result["charges"]=reader.signed()
        elif component == "Stack": result["stack_size"]=reader.num(2)
        elif component == "SkillGem":
            experience = reader.num(4)
            result.update(experience=experience,variant=reader.num(4),gem_flags=reader.num(1))
            reader.take(2)
            thresholds=[x for x in XP if x["ItemExperienceType"]==GEMS[index]["ItemExperienceType"]]
            result["level"]=len(thresholds)+1 if experience == 0xffffffff else sum(x["Experience"]<=experience for x in thresholds)
        elif component == "Mods":
            result["item_level"],result["required_level"]=reader.num(1),reader.num(1)
            if reader.num(2)!=0: raise ValueError("Unsupported Mods flags")
            magic = reader.num(2)
            result.update(rarity=magic & 7,identified=bool(magic & 8))
            reader.num(1)
            if magic & 512: reader.take(2)
            for bit in (4,6,10,11):
                if magic & (1<<bit): mod_list()
            if magic & 8:
                if magic & 128: reader.take(reader.num(4)*2)
                elif magic & 256: reader.take(reader.num(1)*4)
                if magic & 32: mod_list()
            reader.take(2)
        elif component == "Sockets":
            flags=reader.num(1)
            result["sockets"]=[]
            if not flags & 3:
                for _ in range((flags>>2)&7):
                    socket=reader.num(1)
                    result["sockets"].append(dict(colour=socket>>1,gem=item(reader,depth+1) if socket&1 else None))
                result["links"]=[reader.num(1) for _ in range(flags>>5)]
        else:
            raise ValueError("Unsupported item component "+component)
    return result


def snapshots(folder):
    output, errors = [], []
    inventory_rows=table("Inventories")
    for number, value, packet in records(folder):
        if value["direction"]!="S2C" or value["opcode"]!=0xc0: continue
        reader=Reader(packet[2:])
        try:
            manager,inventory=reader.num(1),reader.num(4,"big")
            kind,row,width,height,flags=[reader.num(1) for _ in range(5)]
            count=reader.unsigned()
            row_name=inventory_rows[row]["Id"] if row<len(inventory_rows) else "UnmappedRow"+str(row)
            # Equipment/flasks and reward manager only; skip private stash and general loot.
            if row_name not in ("Weapon1","Helm1","Flask1") and manager!=1: continue
            entries=[]
            for _ in range(count):
                itemid=reader.num(4,"big");reader.take(2)
                blob=reader.take(reader.num(2,"big"));nested=Reader(blob)
                decoded=item(nested)
                if nested.offset!=len(blob): raise ValueError("Unconsumed item bytes")
                entries.append(decoded)
            if reader.take(2)!=bytes(2) or reader.offset!=len(reader.data): raise ValueError("Inventory trailing fields")
            if entries: output.append(dict(record=number,time=value["started"],manager=manager,inventory=inventory,row=row_name,items=entries))
        except (ValueError,KeyError,IndexError) as error:
            errors.append(dict(record=number,error=str(error)))
    return dict(snapshots=output,errors=errors)

if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument("folder",type=Path);args=parser.parse_args()
    result=snapshots(args.folder)
    (args.folder/"equipment.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
    print(json.dumps(dict(snapshots=len(result["snapshots"]),errors=result["errors"]),indent=2))
