"""Development path selection; same precedence and Debug layout as client-path.ps1."""
import json
import os
from pathlib import Path


def _directory(repository, setting, variable, default, environ):
    environ = os.environ if environ is None else environ
    directory = environ.get(variable)
    if not directory:
        config_file = Path(repository) / "client.local.json"
        if config_file.exists():
            config = json.loads(config_file.read_text(encoding="utf-8-sig"))
            names = ("clientDirectory", "buildDirectory")
            if not isinstance(config, dict) or not any(name in config for name in names):
                raise ValueError(f"{config_file} must contain clientDirectory and/or buildDirectory strings")
            for name in names:
                if name in config and (not isinstance(config[name], str) or not config[name].strip()):
                    raise ValueError(f"{config_file} must contain a nonempty {name} string")
            directory = config.get(setting)
        directory = directory or default
    if directory is None:
        return None
    if not directory.strip():
        raise ValueError(f"{variable} must not be whitespace")
    path = Path(directory)
    return (path if path.is_absolute() else Path(repository) / path).resolve()


def client_directory(repository, environ=None):
    return _directory(repository, "clientDirectory", "Game_CLIENT_DIR", "Client", environ)


def build_directory(repository, environ=None):
    return _directory(repository, "buildDirectory", "Game_BUILD_DIR", None, environ)


def build_arguments(project, directory):
    return ["build", str(project), "--configuration", "Debug"] + (
        ["--artifacts-path", str(directory)] if directory else [])


def project_assembly(repository, project, directory):
    project = Path(project)
    if not project.is_absolute():
        project = Path(repository) / project
    name = project.name
    if project.suffix == ".csproj":
        name = project.stem
        project = project.parent
    if directory:
        return Path(directory) / "bin" / name / "debug" / f"{name}.dll"
    return project / "bin" / "Debug" / "net10.0" / f"{name}.dll"
