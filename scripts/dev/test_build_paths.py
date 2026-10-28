"""Path configuration and the Python-to-.NET extraction boundary, with isolated outputs."""
import importlib.util
import json
import os
from pathlib import Path
import runpy
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
paths = runpy.run_path(str(ROOT / "scripts/dev/client_paths.py"))
spec = importlib.util.spec_from_file_location("ot", ROOT / "scripts/re/ot_components.py")
ot = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ot)


class BuildPathsTests(unittest.TestCase):
    def test_defaults_partial_config_environment_and_invalid_settings(self):
        with tempfile.TemporaryDirectory(prefix="build paths ") as folder:
            repo = Path(folder)
            config = repo / "client.local.json"
            self.assertIsNone(paths["build_directory"](repo, {}))
            self.assertEqual(repo / "Client", paths["client_directory"](repo, {}))
            for settings in ({"clientDirectory": "../Client"}, {"buildDirectory": "../build with spaces"},
                             {"clientDirectory": "../Client", "buildDirectory": "../build"}):
                config.write_text(json.dumps(settings), encoding="utf-8")
                expected = (repo / settings["buildDirectory"]).resolve() if "buildDirectory" in settings else None
                self.assertEqual(expected, paths["build_directory"](repo, {}))
                self.assertEqual(repo / "override", paths["build_directory"](repo, {"Game_BUILD_DIR": "override"}))
            for settings in ([], {}, {"buildDirectory": 42}, {"buildDirectory": None}, {"buildDirectory": " "}):
                config.write_text(json.dumps(settings), encoding="utf-8")
                with self.assertRaises(ValueError):
                    paths["build_directory"](repo, {})
            with self.assertRaises(ValueError):
                paths["build_directory"](repo, {"Game_BUILD_DIR": " "})

    def test_dotted_project_names_and_csproj_layout(self):
        repo = ROOT
        for directory in (None, ROOT.parent / "build with spaces"):
            base = directory / "bin/GameServer.Gateway/debug" if directory else (
                repo / "src/GameServer.Gateway/bin/Debug/net10.0")
            expected = base / "GameServer.Gateway.dll"
            for project in ("src/GameServer.Gateway", "src/GameServer.Gateway/GameServer.Gateway.csproj"):
                self.assertEqual(expected, paths["project_assembly"](repo, project, directory))

    def test_python_extractor_builds_once_runs_selected_dll_and_propagates_failure(self):
        for external in (False, True):
            with self.subTest(external=external), tempfile.TemporaryDirectory(prefix="extract build ") as folder:
                repo = Path(folder)
                directory = repo / "artifacts with spaces" if external else None
                config = {"buildDirectory": str(directory)} if external else {"clientDirectory": "Client"}
                (repo / "client.local.json").write_text(json.dumps(config), encoding="utf-8")
                assembly = paths["project_assembly"](repo, "scripts/re/ggdump", directory)
                todo = ["Metadata/" + "a" * 13000, "Metadata/" + "b" * 13000]
                calls = []

                def run(command, **kwargs):
                    self.assertEqual(str(repo), kwargs["cwd"])
                    self.assertTrue(kwargs["check"])
                    calls.append(command)
                    if command[1] == "build":
                        self.assertEqual(external, "--artifacts-path" in command)
                        assembly.parent.mkdir(parents=True)
                        assembly.write_text("fixture DLL", encoding="utf-8")
                    else:
                        self.assertEqual(str(assembly), command[1])
                        self.assertEqual(["archive with spaces.ggpk", str(repo / "cache")], command[2:4])
                    return subprocess.CompletedProcess(command, 0, stdout="")

                with patch.object(ot, "REPO", str(repo)), patch.object(ot, "CACHE", str(repo / "cache")), \
                        patch.object(ot, "GGPK", "archive with spaces.ggpk"), patch.dict(os.environ, {}, clear=True), \
                        patch.object(ot.subprocess, "run", side_effect=run):
                    ot.fetch(todo)
                    self.assertEqual(3, len(calls))  # one build, two batches
                with patch.object(ot, "REPO", str(repo)), patch.object(ot, "CACHE", str(repo / "cache")), \
                        patch.dict(os.environ, {}, clear=True), patch.object(ot.subprocess, "run",
                        side_effect=subprocess.CalledProcessError(1, "dotnet")) as child:
                    with self.assertRaises(subprocess.CalledProcessError):
                        ot.fetch(["Metadata/Failure"])
                    self.assertEqual(1, child.call_count)


if __name__ == "__main__":
    unittest.main()
