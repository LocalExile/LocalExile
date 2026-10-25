"""Synthetic capture routing checks; no client, game data, or Frida attachment."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
import time
import unittest

import walkmaps


class QuestObjectCaptureTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        walkmaps.QUEST_OBJECTS.clear()
        self.area()

    def area(self, nonce=1, written=None):
        (self.root / "current.json").write_text(json.dumps({
            "id": "synthetic", "seed": 1, "nonce": nonce,
            "written": time.time() if written is None else written,
        }), encoding="utf-8")

    def send(self, object_type, x=12):
        with contextlib.redirect_stdout(io.StringIO()):
            walkmaps.on_message({"type": "send", "payload": {
                "type": "quest-object", "objectType": object_type,
                "model": "synthetic.ao", "x": x, "y": 34, "rotation": 1.5,
                "z": -5, "rawZ": 10, "heightOffset": 2, "code": 3,
            }}, None, str(self.root))

    def objects(self):
        return json.loads((self.root / "synthetic_seed1.objects.json").read_text())["objects"]

    def test_quest_objects_preserve_transform_without_touching_exits(self):
        exits = self.root / "synthetic_seed1.transitions.json"
        exits.write_text('{"sentinel": true}')
        self.send("Metadata/Chests/QuestChests/RhoaChest")
        self.send("Metadata/QuestObjects/WaterCave/GlyphWall", x=56)
        self.assertEqual('{"sentinel": true}', exits.read_text())
        self.assertEqual(2, len(self.objects()))
        self.assertEqual((56, -5, 10, 2, 3), tuple(self.objects()[1][k] for k in ("x", "z", "rawZ", "heightOffset", "code")))

    def test_new_nonce_starts_a_new_collection(self):
        self.send("Metadata/Chests/QuestChests/RhoaChest")
        self.area(nonce=2)
        self.send("Metadata/Chests/QuestChests/RhoaChest2", x=99)
        self.assertEqual([99], [o["x"] for o in self.objects()])
        self.assertEqual(1, len(walkmaps.QUEST_OBJECTS))

    def test_stale_or_unrelated_objects_do_not_write_a_capture(self):
        self.send("Metadata/Unrelated")
        self.area(written=time.time() - walkmaps.FRESH_SECONDS - 1)
        self.send("Metadata/Chests/QuestChests/RhoaChest")
        self.assertFalse((self.root / "synthetic_seed1.objects.json").exists())

    def test_room_placements_are_deduplicated_and_reset_on_new_nonce(self):
        room={"path":"Metadata/Terrain/Test.arm","x":23,"y":46,"width":69,"height":92,"code":3}
        walkmaps.save_room(str(self.root),room)
        walkmaps.save_room(str(self.root),room)
        file=self.root/"synthetic_seed1.rooms.json"
        self.assertEqual([room],json.loads(file.read_text())["rooms"])
        self.area(nonce=2)
        changed={**room,"x":138}
        walkmaps.save_room(str(self.root),changed)
        self.assertEqual([changed],json.loads(file.read_text())["rooms"])
        self.area(written=time.time()-walkmaps.FRESH_SECONDS-1)
        walkmaps.save_room(str(self.root),room)
        self.assertEqual([changed],json.loads(file.read_text())["rooms"])


if __name__ == "__main__":
    unittest.main()
