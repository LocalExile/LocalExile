"""Linux/Proton transport for walkmaps.py. Third-party binaries stay in ignored Tools/."""
import fcntl
import hashlib
import io
import json
import os
from pathlib import Path
import platform
import shutil
import socket
import stat
import struct
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
VERSION = "17.26.2"
DEVKIT_SHA256 = "b70e79856b609beca80661a8585675042159998eff4fe54e6367b4428091fe4b"
TOOLS = ROOT / "Tools" / "frida" / ("gumjs-" + VERSION)
SOURCE = Path(__file__).with_name("proton-recorder.cpp")
LIBRARY = TOOLS / "proton-recorder.so"
HEADER = struct.Struct("<II")


def socket_path(root):
    folder = Path(tempfile.gettempdir()) / ("localexile-walkmaps-" + str(os.getuid()))
    folder.mkdir(mode=0o700, exist_ok=True)
    info = folder.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
        raise RuntimeError("recorder socket directory must be private and owned by this user")
    key = hashlib.sha256(os.fsencode(root.resolve())).hexdigest()[:16]
    return folder / (key + ".sock")


def prepare():
    if sys.platform != "linux" or platform.machine() != "x86_64":
        raise RuntimeError("the Proton recorder currently supports Linux x86_64")
    TOOLS.mkdir(parents=True, exist_ok=True)
    with (TOOLS / "build.lock").open("w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if not all((TOOLS / name).is_file() for name in ("frida-gumjs.h", "libfrida-gumjs.a")):
            name = f"frida-gumjs-devkit-{VERSION}-linux-x86_64.tar.xz"
            url = f"https://github.com/frida/frida/releases/download/{VERSION}/{name}"
            print("[walkmaps] downloading official Frida GumJS " + VERSION, flush=True)
            with urllib.request.urlopen(url, timeout=60) as response:
                data = response.read()
            if hashlib.sha256(data).hexdigest() != DEVKIT_SHA256:
                raise RuntimeError("Frida development kit checksum does not match")
            with tarfile.open(fileobj=io.BytesIO(data), mode="r:xz") as archive:
                for name in ("frida-gumjs.h", "libfrida-gumjs.a"):
                    member = archive.getmember(name)
                    stream = archive.extractfile(member)
                    if not member.isfile() or stream is None:
                        raise RuntimeError("unexpected development kit contents")
                    (TOOLS / name).write_bytes(stream.read())
        if not LIBRARY.exists() or LIBRARY.stat().st_mtime < SOURCE.stat().st_mtime:
            compiler = shutil.which("g++")
            if not compiler:
                raise RuntimeError("g++ is required to build the Proton recorder")
            temporary = LIBRARY.with_suffix(".so.tmp")
            subprocess.run([compiler, "-shared", "-fPIC", "-O2", "-Wall", "-Wextra", "-Werror",
                            "-ffunction-sections", "-fdata-sections", str(SOURCE), "-I", str(TOOLS),
                            "-L", str(TOOLS), "-lfrida-gumjs", "-ldl", "-lm", "-lrt", "-lresolv",
                            "-latomic", "-pthread", "-static-libstdc++", "-Wl,--gc-sections", "-o", str(temporary)],
                           cwd=ROOT, check=True)
            temporary.replace(LIBRARY)
    return LIBRARY


def process_info(pid, client):
    """Match the recorded Linux PID, start time and mapped client image, including Proton's relocated PE image."""
    folder = Path("/proc") / str(pid)
    try:
        details = (folder / "stat").read_text().rsplit(")", 1)[1].split()
        if details[0] == "Z":
            return None
        boot = next(int(line.split()[1]) for line in Path("/proc/stat").read_text().splitlines()
                    if line.startswith("btime "))
        started = int((boot + int(details[19]) / os.sysconf("SC_CLK_TCK") + 11644473600) * 10_000_000)
        client = str(client.resolve())
        mapped = any(line.split(maxsplit=5)[-1] == client for line in (folder / "maps").read_text().splitlines())
        arguments = (folder / "cmdline").read_bytes().split(b"\0")
        executable = arguments[0].decode(errors="replace").replace("\\", "/").rsplit("/", 1)[-1]
        return (client, started) if mapped and executable == "Game.patched.exe" else None
    except (OSError, ValueError, IndexError, StopIteration):
        return None


def receive_exact(connection, count):
    data = bytearray()
    while len(data) < count:
        chunk = connection.recv(min(count - len(data), 1024 * 1024))
        if not chunk:
            raise EOFError("client capture stream closed")
        data.extend(chunk)
    return bytes(data)


def listen(record, out, callback, wait=600):
    prepare()
    record = Path(record).resolve()
    root = record.parents[2]
    path = socket_path(root)
    with path.with_suffix(".lock").open("a") as owner, socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as server:
        try:
            fcntl.flock(owner, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RuntimeError("this checkout already has a recorder receiver") from None
        path.unlink(missing_ok=True)
        server.bind(str(path))
        os.chmod(path, 0o600)
        server.listen(1)
        server.settimeout(wait)
        print("[walkmaps] waiting for the Proton client capture stream", flush=True)
        try:
            connection, _ = server.accept()
            with connection:
                pid, uid, _ = struct.unpack("3i", connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
                if uid != os.getuid():
                    raise RuntimeError("capture stream belongs to another user")
                deadline = time.monotonic() + 15
                while True:
                    try:
                        saved = json.loads(record.read_text(encoding="utf-8-sig"))
                        info = process_info(pid, root / "Client" / "Game.patched.exe")
                        current = info and saved.get("pid") == pid and abs(info[1] - int(saved.get("started", 0))) < 20_000_000
                    except (OSError, ValueError, TypeError):
                        current = False
                    if current:
                        break
                    if time.monotonic() >= deadline:
                        raise RuntimeError("capture stream does not match this checkout's live client record")
                    time.sleep(0.1)
                print(f"[walkmaps] receiving from verified Proton client pid {pid}", flush=True)
                while True:
                    json_size, binary_size = HEADER.unpack(receive_exact(connection, HEADER.size))
                    if json_size > 64 * 1024 * 1024 or binary_size > 256 * 1024 * 1024:
                        raise RuntimeError("capture frame exceeds maximum size")
                    message = json.loads(receive_exact(connection, json_size))
                    binary = receive_exact(connection, binary_size) if binary_size else None
                    callback(message, binary, out)
        except EOFError:
            print("[walkmaps] client capture stream closed", flush=True)
        finally:
            path.unlink(missing_ok=True)


def run(arguments):
    if len(arguments) < 3:
        raise RuntimeError("recorder runner requires the client and its local patching-server argument")
    client = Path(arguments[0]).resolve()
    if client.name != "Game.patched.exe" or arguments[1] != "--patching-server" or not arguments[2].startswith("127.0.0.1:"):
        raise RuntimeError("the recorder runner only starts the patched client with a local patching server")
    root = client.parents[1]
    if not (root / "AGENTS.md").is_file() or not client.is_file():
        raise RuntimeError("client must be the copied client inside a LocalExile checkout")
    library = prepare()
    env = os.environ.copy()
    env["LD_PRELOAD"] = str(library) + ((":" + env["LD_PRELOAD"]) if env.get("LD_PRELOAD") else "")
    env["LOCALEXILE_CAPTURE_IMAGE"] = str(client)
    env["LOCALEXILE_CAPTURE_AGENT"] = str(Path(__file__).with_name("walkmaps.js"))
    env["LOCALEXILE_CAPTURE_SOCKET"] = str(socket_path(root))
    runner = env.get("Game_RECORDER_CLIENT_RUNNER", "umu-run")
    os.execvpe(runner, [runner, *arguments], env)


if __name__ == "__main__":
    try:
        if sys.argv[1:] == ["prepare"]:
            print(prepare())
        elif len(sys.argv) > 1 and sys.argv[1] == "run":
            run(sys.argv[2:])
        else:
            raise RuntimeError("usage: proton.py prepare | run <patched-client> --patching-server 127.0.0.1:PORT")
    except (RuntimeError, OSError, subprocess.CalledProcessError) as error:
        sys.exit("walkmaps: " + str(error))
