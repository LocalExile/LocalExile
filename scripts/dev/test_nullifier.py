"""Offline checks of the nullifier launcher and bench; no client is spawned or attached.
    .venv/Scripts/python.exe -m unittest discover -s scripts/dev -p test_nullifier.py
"""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import nullifier

spec = importlib.util.spec_from_file_location("nullifier_bench", Path(__file__).with_name("nullifier-bench.py"))
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


class FakeChild:
    def __init__(self, pid, alive=True):
        self.pid, self._alive = pid, alive

    def alive(self):
        return self._alive


class NullifierTests(unittest.TestCase):
    def test_bridge_is_bound_to_its_own_child(self):
        self.assertEqual(nullifier.PidClientControl(FakeChild(4321))._find_client(), 4321)
        self.assertIsNone(nullifier.PidClientControl(FakeChild(4321, alive=False))._find_client())

    def test_waypoint_travel_sends_the_pin_message(self):
        control = nullifier.PidClientControl(FakeChild(1))
        with patch.object(control, "rpc", return_value={"submitted": True}) as rpc:
            result = control.execute({"command": "waypoint:travel", "area": "0x7ad2"})
        rpc.assert_called_once_with("chat", bytes=[0x00, 0x4d, 0x7a, 0xd2, 0, 0, 0x2f, 0x00, 0])
        self.assertEqual(result["area"], 0x7ad2)
        for bad in [0, 0x10000, "x"]:
            with self.assertRaises(ValueError):
                control.execute({"command": "waypoint:travel", "area": bad})

    def test_other_commands_reach_client_control(self):
        control = nullifier.PidClientControl(FakeChild(1))
        with patch.object(nullifier.client_control.ClientControl, "execute", return_value={"x": 1}) as base:
            self.assertEqual(control.execute({"command": "game:status"}), {"x": 1})
        base.assert_called_once()

    def test_child_handle_is_the_live_process(self):
        child = nullifier.Child.__new__(nullifier.Child)
        child.handle = None
        self.assertFalse(child.alive())

    def test_settings_must_be_the_clients_own(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(SystemExit):
                nullifier.settings_name(folder)  # no config.json: the default settings file would be used
            config = Path(folder) / "config.json"
            for name in ["poeoffline_Config.ini", "Production_Config.ini", "", None]:
                config.write_text(json.dumps({"GGG_CONFIG_FILE_NAME": name}), encoding="ascii")
                with self.assertRaises(SystemExit):
                    nullifier.settings_name(folder)
            config.write_text(json.dumps({"GGG_CONFIG_FILE_NAME": "localexile-x-bot_Config.ini"}), encoding="ascii")
            self.assertEqual(nullifier.settings_name(folder), "localexile-x-bot_Config.ini")

    def test_session_names_are_plain(self):
        for bad in ["..", "a/b", "a b", "", "x" * 33]:
            with self.assertRaises(SystemExit):
                nullifier.prepare_session(bad, nullifier.PRESET)

    def test_record_merges_atomically(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "s.json"
            path.write_text(json.dumps({"pid": 0, "session": "s"}), encoding="ascii")
            nullifier.write_record(path, {"pid": 7, "launcher": 9})
            self.assertEqual(json.loads(path.read_text()), {"pid": 7, "session": "s", "launcher": 9})
            self.assertEqual([p.name for p in Path(folder).iterdir()], ["s.json"])

    def test_preset_and_agent_exist(self):
        self.assertTrue(nullifier.PRESET.is_file())
        text = nullifier.PRESET.read_text(encoding="utf-8")
        self.assertIn("[DISPLAY]", text)
        self.assertNotIn("password", text.lower())
        self.assertNotIn("[LOGIN]", text)  # no saved login for whichever login server the gateway list names

    def test_run_mutex_prefix_is_session_local_and_unique(self):
        a = nullifier.run_mutex_prefix(Path("data/clients/bot1"))
        b = nullifier.run_mutex_prefix(Path("data/clients/bot2"))
        self.assertTrue(a.startswith("Local\\LocalExile-") and a.endswith("-"), a)
        self.assertNotIn("Global", a)
        self.assertNotEqual(a, b)
        self.assertNotIn("\\", a[len("Local\\"):])  # one namespace separator only
        self.assertRegex(nullifier.run_mutex_prefix(Path("x y/a b")), r"^Local\\LocalExile-[A-Za-z0-9_-]+-$")

    @unittest.skipUnless(shutil.which("node"), "node not installed")
    def test_agent_renames_only_the_two_copies_mutexes(self):
        result = subprocess.run(["node", str(Path(__file__).with_name("test-nullifier-mutex.cjs"))], text=True,
                                capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr)

    @unittest.skipUnless(shutil.which("node"), "node not installed")
    def test_agent_parses_with_options(self):
        with tempfile.TemporaryDirectory() as folder:
            agent = Path(folder) / "nullifier.js"
            agent.write_text("const OPTIONS = {};\n" + nullifier.AGENT.read_text(encoding="utf-8"), encoding="utf-8")
            result = subprocess.run(["node", "--check", str(agent)], text=True, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr)


class BenchLockTests(unittest.TestCase):
    def test_lock_is_exclusive_and_foreign_formats_are_held(self):
        with tempfile.TemporaryDirectory() as folder:
            lock = Path(folder) / ".extra-client.lock"
            lock.write_text("pr-campaign-a 2026-10-30T21:22:49\n", encoding="utf-8")  # another format: held
            with self.assertRaises(SystemExit):
                bench.take_lock(lock)
            self.assertIn("pr-campaign-a", lock.read_text())
            lock.write_text(f"nullifier-bench {os.getpid()} now\n", encoding="utf-8")  # a live run of ours: held
            with self.assertRaises(SystemExit):
                bench.take_lock(lock)
            lock.write_text("nullifier-bench 4294967291 then\n", encoding="utf-8")  # a dead run of ours: taken over
            with patch.object(bench, "alive_pid", return_value=False):
                bench.take_lock(lock)
            self.assertEqual(lock.read_text().split()[:2], ["nullifier-bench", str(os.getpid())])
            bench.release_lock(lock)
            self.assertFalse(lock.exists())

    def test_release_compares_whole_tokens(self):
        with tempfile.TemporaryDirectory() as folder:
            lock = Path(folder) / "l"
            lock.write_text(f"nullifier-bench {os.getpid()}1 x\n", encoding="utf-8")
            bench.release_lock(lock)
            self.assertTrue(lock.exists())


if __name__ == "__main__":
    unittest.main()
