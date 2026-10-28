"""Client-control folder selection; same precedence as client-path.ps1."""
import json
import os
from pathlib import Path


def client_directory(repository, environ=None):
    environ = os.environ if environ is None else environ
    directory = environ.get("Game_CLIENT_DIR")
    if not directory:
        config_file = Path(repository) / "client.local.json"
        if config_file.exists():
            config = json.loads(config_file.read_text(encoding="utf-8-sig"))
            directory = config.get("clientDirectory") if isinstance(config, dict) else None
            if not isinstance(directory, str) or not directory.strip():
                raise ValueError(f"{config_file} must be a JSON object with a nonempty clientDirectory string")
        else:
            directory = "Client"
    if not directory.strip():
        raise ValueError("Game_CLIENT_DIR must not be whitespace")
    path = Path(directory)
    return (path if path.is_absolute() else Path(repository) / path).resolve()
