"""Read-only checks of the small, hash-pinned network contract manifest.

python -I scripts/re/port/verify_network_contracts.py --client PATH [--client PATH ...] [--require-all]
Requires capstone, as compare_builds.py does. No input files are modified.
This verifies retained observations, not whole-program equivalence or permission to allow a new build.
"""
import argparse
import hashlib
import importlib.util
import json
import struct
from pathlib import Path

import capstone

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("compare_builds", HERE / "compare_builds.py")
compare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compare)


def verify(image, build, manifest):
    failures = []

    def check(ok, label):
        if not ok:
            failures.append(label)

    check(image.base == int(manifest["image_base"], 16), "image base")
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    for name, function in build["functions"].items():
        data = image.at_va(int(function["va"], 16), function["size"])
        check(hashlib.sha256(data).hexdigest() == function["sha256"], f"function span: {name}")
    for witness in build["witnesses"]:
        va = int(witness["va"], 16)
        data = image.at_va(va, len(bytes.fromhex(witness["bytes"])))
        check(data.hex() == witness["bytes"], f"bytes: {witness['meaning']}")
        instructions = [f"{i.mnemonic} {i.op_str}".rstrip() for i in md.disasm(data, va)]
        check(instructions == witness["instructions"], f"instructions: {witness['meaning']}")
    for anchor in build["data_references"]:
        va = int(anchor["instruction"], 16)
        detailed = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        detailed.detail = True
        instruction = next(detailed.disasm(image.at_va(va, 15), va))
        operand = next(o for o in instruction.operands if o.type == capstone.x86.X86_OP_MEM
                       and o.mem.base == capstone.x86.X86_REG_RIP)
        target = instruction.address + instruction.size + operand.mem.disp
        check(target == int(anchor["target"], 16), f"data address: {anchor['meaning']}")
        check(image.at_va(target, len(bytes.fromhex(anchor["bytes"]))).hex() == anchor["bytes"],
              f"data value: {anchor['meaning']}")
    for route in build["switch_routes"]:
        index = int(route["message_id"], 16) - int(route["subtract"], 16)
        if "index_table" in route:
            index = image.at_va(int(route["index_table"], 16) + index, 1)[0]
        rva = struct.unpack("<I", image.at_va(int(route["target_table"], 16) + 4 * index, 4))[0]
        target = image.base + rva
        check(target == int(route["target"], 16), f"switch target: {route['meaning']}")
        check(hashlib.sha256(image.at_va(target, route["size"])).hexdigest() == route["sha256"],
              f"switch branch: {route['meaning']}")
    return failures


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", action="append", required=True, type=Path)
    parser.add_argument("--manifest", type=Path, default=HERE / "network_contracts.json")
    parser.add_argument("--require-all", action="store_true")
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    results, seen = [], set()
    for path in args.client:
        try:
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
        except OSError as error:
            results.append({"file": str(path), "errors": [str(error)]})
            continue
        match = next(((name, b) for name, b in manifest["builds"].items() if b["sha256"] == digest), None)
        if match is None:
            results.append({"sha256": digest, "errors": ["unknown executable; no contract mapping"]})
            continue
        image = compare.Image(path)
        name, build = match
        seen.add(name)
        results.append({"build": name, "sha256": image.sha256, "errors": verify(image, build, manifest)})
    missing = sorted(set(manifest["builds"]) - seen) if args.require_all else []
    print(json.dumps({"results": results, "missing_builds": missing,
                      "scope": "Pinned function spans, field witnesses, data anchors and switch routes only; no live parity claim."}, indent=2))
    return 1 if missing or any(r["errors"] for r in results) else 0


if __name__ == "__main__":
    raise SystemExit(main())
