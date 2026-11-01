"""Read-only identity checks for the bounded Tornado Shot input-contract receipt.

No native execution or semantic replay. Run from the repository root; Python 3.9+,
standard library only. Local game inputs are never copied or modified.
"""

import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def require(condition, label):
    if not condition:
        raise ValueError(label)


def pe_reader(raw):
    require(raw[:2] == b"MZ", "DOS signature")
    nt = struct.unpack_from("<I", raw, 0x3C)[0]
    require(raw[nt:nt + 4] == b"PE\0\0", "PE signature")
    require(struct.unpack_from("<H", raw, nt + 4)[0] == 0x8664, "x64 machine")
    section_count = struct.unpack_from("<H", raw, nt + 6)[0]
    optional_size = struct.unpack_from("<H", raw, nt + 20)[0]
    optional = nt + 24
    require(struct.unpack_from("<H", raw, optional)[0] == 0x20B, "PE32+ header")
    image_base = struct.unpack_from("<Q", raw, optional + 24)[0]
    sections = []
    for i in range(section_count):
        at = optional + optional_size + 40 * i
        _, rva, size, offset = struct.unpack_from("<IIII", raw, at + 8)
        sections.append((rva, size, offset))

    def read(va, length):
        rva = va - image_base
        for start, size, offset in sections:
            if start <= rva and rva + length <= start + size:
                result = raw[offset + rva - start:offset + rva - start + length]
                require(len(result) == length, "truncated selected PE range")
                return result
        raise ValueError("selected address is outside file-backed sections")

    return read


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", type=Path, default=Path("Client/Game.exe"))
    parser.add_argument("--tables", type=Path, default=Path("data/tables"))
    parser.add_argument("--receipt", type=Path,
                        default=Path("Agents/docs/re/tornado-shot-input-evidence.json"))
    args = parser.parse_args()
    receipt = json.loads(args.receipt.read_text(encoding="utf-8"))
    raw = args.client.read_bytes()
    require(sha256(raw) == receipt["client_sha256"], "client SHA256 mismatch")
    read = pe_reader(raw)
    for span in receipt["pe_ranges"]:
        start, end = int(span["start"], 16), int(span["end"], 16)
        require(end > start, "nonpositive selected PE range")
        require(sha256(read(start, end - start)) == span["sha256"],
                "selected PE range mismatch: " + span["start"])
    table = int(receipt["final_vtable"], 16)
    for slot, target in receipt["slots"].items():
        actual = struct.unpack("<Q", read(table + int(slot, 16), 8))[0]
        require(actual == int(target, 16), "vtable slot mismatch: " + slot)
    for entry in receipt["table_files"]:
        require(sha256((args.tables / entry["file"]).read_bytes()) == entry["sha256"],
                "table SHA256 mismatch: " + entry["file"])
    for entry in receipt["source_blobs"]:
        result = subprocess.run(["git", "show", entry["commit"] + ":" + entry["path"]],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        require(result.returncode == 0, "unavailable fixed Git blob: " + entry["path"])
        require(sha256(result.stdout) == entry["sha256"],
                "fixed Git blob SHA256 mismatch: " + entry["path"])
    print(json.dumps({"identity_check": "passed", "semantic_replay": False,
                      "runtime_capture": False, "pe_ranges": len(receipt["pe_ranges"]),
                      "slots": len(receipt["slots"]), "table_files": len(receipt["table_files"]),
                      "source_blobs": len(receipt["source_blobs"])}))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, struct.error) as error:
        # Avoid putting a user's resolved filesystem paths into shareable output.
        reason = str(error) if isinstance(error, ValueError) else type(error).__name__
        raise SystemExit("Identity check failed: " + reason)
