"""List client message ids that no server code mentions.

Runs the dispatcher dump, then greps every .cs under Modules/ and src/ for the id as a hex literal, so the sweep only
tries ids we never send (the ones with a chance of being the missing message).
"""

import re
import subprocess
import sys
from pathlib import Path

REPO = Path(r"D:\大肥鱼工作区\LocalExile")
PY = r"D:\大肥鱼工作区\python\python.exe"

out = subprocess.run([PY, str(REPO / "scripts" / "re" / "dispatch_ids.py")],
                     capture_output=True, text=True, cwd=REPO)
ids = [int(m.group(1), 16) for m in re.finditer(r"id 0x([0-9a-f]{4})", out.stdout)]
if not ids:
    print("no ids from dispatch_ids.py", file=sys.stderr)
    print(out.stdout[:400], out.stderr[:400])
    raise SystemExit(1)
print(f"dispatcher knows {len(ids)} id(s): 0x{min(ids):04x}..0x{max(ids):04x}")

texts = []
for folder in ("Modules", "src"):
    for p in (REPO / folder).rglob("*.cs"):
        try:
            texts.append(p.read_text(encoding="utf-8", errors="replace").lower())
        except OSError:
            pass
blob = "\n".join(texts)
print(f"scanned {len(texts)} .cs file(s)")

unused = []
for i in ids:
    pat = f"0x{i:04x}"
    if pat not in blob:
        unused.append(i)

print(f"{len(unused)} id(s) never appear as a hex literal in the server code:")
runs = []
for i in unused:
    if runs and i == runs[-1][1] + 1:
        runs[-1][1] = i
    else:
        runs.append([i, i])
print("  " + ", ".join(f"0x{a:04x}" if a == b else f"0x{a:04x}-0x{b:04x}" for a, b in runs))
