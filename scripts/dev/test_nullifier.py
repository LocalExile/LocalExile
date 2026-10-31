"""Offline checks of the nullifier launcher; no client is spawned or attached.
    .venv/Scripts/python.exe -m unittest discover -s scripts/dev -p test_nullifier.py
"""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import nullifier


class NullifierTests(unittest.TestCase):
    def test_bridge_is_bound_to_its_own_pid(self):
        control = nullifier.PidClientControl(4321, started=1000)
        with patch.object(nullifier.client_control, "live_process", side_effect=lambda pid: pid == 4321),                 patch.object(nullifier, "creation_time", return_value=1000):
            self.assertEqual(control._find_client(), 4321)
        with patch.object(nullifier.client_control, "live_process", return_value=False):
            self.assertIsNone(control._find_client())

    def test_a_reused_pid_is_not_the_client(self):
        control = nullifier.PidClientControl(4321, started=1000)
        with patch.object(nullifier.client_control, "live_process", return_value=True),                 patch.object(nullifier, "creation_time", return_value=2000):
            self.assertIsNone(control._find_client())

    def test_waypoint_travel_sends_the_pin_message(self):
        control = nullifier.PidClientControl(1, started=1)
        with patch.object(control, "rpc", return_value={"submitted": True}) as rpc:
            result = control.execute({"command": "waypoint:travel", "area": "0x7ad2"})
        rpc.assert_called_once_with("chat", bytes=[0x00, 0x4d, 0x7a, 0xd2, 0, 0, 0x2f, 0x00, 0])
        self.assertEqual(result["area"], 0x7ad2)
        for bad in [0, 0x10000, "x"]:
            with self.assertRaises(ValueError):
                control.execute({"command": "waypoint:travel", "area": bad})

    def test_other_commands_reach_client_control(self):
        control = nullifier.PidClientControl(1, started=1)
        with patch.object(nullifier.client_control.ClientControl, "execute", return_value={"x": 1}) as base:
            self.assertEqual(control.execute({"command": "game:status"}), {"x": 1})
        base.assert_called_once()

    def test_creation_time_matches_a_live_process(self):
        started = nullifier.creation_time(os.getpid())
        self.assertIsInstance(started, int)
        self.assertGreater(started, 116444736000000000)  # after 1970 in FILETIME ticks
        self.assertIsNone(nullifier.creation_time(0x7ffffffc))

    def test_preset_and_agent_exist(self):
        self.assertTrue(nullifier.PRESET.is_file())
        text = nullifier.PRESET.read_text(encoding="utf-8")
        self.assertIn("[DISPLAY]", text)
        self.assertNotIn("password", text.lower())
        self.assertNotIn("[LOGIN]", text)  # no saved login for whichever login server the gateway list names

    @unittest.skipUnless(shutil.which("node"), "node not installed")
    def test_agent_parses_with_options(self):
        with tempfile.TemporaryDirectory() as folder:
            agent = Path(folder) / "nullifier.js"
            agent.write_text("const OPTIONS = {};\n" + nullifier.AGENT.read_text(encoding="utf-8"), encoding="utf-8")
            result = subprocess.run(["node", "--check", str(agent)], text=True, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
