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
import time
import unittest
from unittest.mock import patch

import nullifier

spec = importlib.util.spec_from_file_location("nullifier_bench", Path(__file__).with_name("nullifier-bench.py"))
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


class NullifierTests(unittest.TestCase):
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

    def test_the_shared_client_folder_is_refused(self):
        with self.assertRaises(SystemExit):
            nullifier.main(["--cwd", str(nullifier.CLIENT.parent), "--patch-port", "1", "--login-port", "2"])

    def test_record_merges_atomically(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "s.json"
            path.write_text(json.dumps({"pid": 0, "started": 1, "session": "s", "folder": "f"}), encoding="ascii")
            nullifier.write_record(path, {"pid": 7, "started": 8, "launcher": 9, "launcherStarted": 10})
            self.assertEqual(json.loads(path.read_text()), {"pid": 7, "started": 8, "session": "s", "folder": "f",
                                                            "launcher": 9, "launcherStarted": 10})
            self.assertEqual([p.name for p in Path(folder).iterdir()], ["s.json"])

    def test_run_mutex_prefix_is_session_local_and_unique(self):
        a = nullifier.run_mutex_prefix(Path("data/clients/bot1"))
        b = nullifier.run_mutex_prefix(Path("data/clients/bot2"))
        self.assertTrue(a.startswith("Local\\LocalExile-") and a.endswith("-"), a)
        self.assertNotIn("Global", a)
        self.assertNotEqual(a, b)
        self.assertNotIn("\\", a[len("Local\\"):])  # one namespace separator only
        self.assertRegex(nullifier.run_mutex_prefix(Path("x y/a b")), r"^Local\\LocalExile-[A-Za-z0-9_-]+-$")

    def test_options_and_client_command(self):
        with patch.object(nullifier.client_control, "stack_port", side_effect=lambda name: {"patch": 21000, "login": 21002}[name]):
            options = nullifier.parse(["--cwd", "data/clients/s1"])
        self.assertEqual((options.patch_port, options.login_port, options.renderer), (21000, 21002, "configured"))
        self.assertEqual(nullifier.client_argv(options),
                         [str(nullifier.CLIENT), "--patching-server", "127.0.0.1:21000", "--nologo", "--nosound"])
        agent = nullifier.agent_options(options, Path("data/clients/s1"))
        self.assertFalse(agent["nullRenderer"])
        self.assertEqual(agent["runMutexPrefix"], nullifier.run_mutex_prefix(Path("data/clients/s1")))
        self.assertIsNone(agent["crashLog"])
        options = nullifier.parse(["--cwd", "x", "--patch-port", "1", "--login-port", "2", "--renderer", "null",
                                   "--trim-seconds", "10", "--sound"])
        self.assertTrue(nullifier.agent_options(options, Path("x"))["nullRenderer"])
        self.assertEqual(nullifier.client_argv(options)[-1], "--nologo")

    def test_preset_and_agent_exist(self):
        text = (nullifier.ROOT / "config" / "nullifier_Config.ini").read_text(encoding="utf-8")
        self.assertIn("[DISPLAY]", text)
        self.assertNotIn("password", text.lower())
        self.assertNotIn("[LOGIN]", text)  # no saved login for whichever login server the gateway list names

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


class BenchTests(unittest.TestCase):
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

    def test_bridge_calls_time_out(self):
        self.assertEqual(bench.with_timeout(1, lambda x: x + 1, 1), 2)
        with self.assertRaises(TimeoutError):
            bench.with_timeout(0.05, time.sleep, 1)
        with self.assertRaises(KeyError):
            bench.with_timeout(1, {}.__getitem__, "x")

    def test_client_handle_must_be_the_recorded_process(self):
        with patch.object(bench.client_control, "process_info", return_value=("x", 1_000_000_000)):
            with self.assertRaises(RuntimeError):
                bench.Client(os.getpid(), 5_000_000_000)  # another start time: not the recorded client
        own = bench.client_control.process_info(os.getpid())
        client = bench.Client(os.getpid(), own[1])
        self.assertTrue(client.alive())
        client.close()
        self.assertFalse(client.alive())


if __name__ == "__main__":
    unittest.main()
