"""Standard-library stdio MCP adapter to the running PlayerBots module. No AI SDK/provider is required."""
import argparse
import json
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True, type=Path, help="LocalExile repository root")
    parser.add_argument("--port", type=int, default=6194)
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535:
        parser.error("port must be 1024..65535")
    key_file = args.root / "data" / "modules" / "PlayerBots-mcp.key"
    endpoint = f"http://127.0.0.1:{args.port}/mcp/"
    # Never follow redirects: an MCP bearer key belongs only to this local module.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, headers, newurl):
            return None
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    version = "2025-11-25"
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    while True:
        line = sys.stdin.buffer.readline(65538)
        if not line:
            break
        request_id = None
        has_id = False
        try:
            if len(line) > 65536:
                while not line.endswith(b"\n"):
                    line = sys.stdin.buffer.readline(65538)
                    if not line:
                        break
                raise ValueError("MCP request exceeds 64 KiB")
            try:
                message = json.loads(line)
            except (json.JSONDecodeError, UnicodeDecodeError):
                print(json.dumps({"jsonrpc": "2.0", "id": None, "error": {"code": -32700,
                      "message": "Invalid JSON."}}), flush=True)
                continue
            if not isinstance(message, dict):
                raise ValueError("MCP message must be an object")
            has_id = "id" in message
            request_id = message.get("id")
            token = key_file.read_text(encoding="ascii").strip()
            if len(token) != 64 or any(c not in "0123456789abcdefABCDEF" for c in token):
                raise ValueError("Invalid local MCP key file")
            metadata = message.get("params", {}).get("_meta", {})
            request_version = metadata.get("io.modelcontextprotocol/protocolVersion", version)
            data = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            headers = {"Authorization": "Bearer " + token, "Content-Type": "application/json",
                       "Accept": "application/json, text/event-stream"}
            if message.get("method") != "initialize":
                headers["MCP-Protocol-Version"] = request_version
            request = urllib.request.Request(endpoint, data=data, headers=headers, method="POST")
            try:
                response = opener.open(request, timeout=10)
            except urllib.error.HTTPError as error:
                if error.code == 400 and error.headers.get_content_type() == "application/json":
                    response = error  # Modern protocol-version errors are ordinary JSON-RPC responses.
                else:
                    raise RuntimeError(f"PlayerBots MCP HTTP {error.code}") from None
            with response:
                payload = response.read(4 * 1024 * 1024 + 1)
                if len(payload) > 4 * 1024 * 1024:
                    raise ValueError("MCP response exceeds 4 MiB")
                if payload:
                    reply = json.loads(payload)
                    if message.get("method") == "initialize" and "result" in reply:
                        version = reply["result"]["protocolVersion"]
                    print(json.dumps(reply, ensure_ascii=False, separators=(",", ":")), flush=True)
        except Exception as error:
            # stdout is exclusively JSON-RPC. Error details never include headers or key contents.
            if has_id:
                print(json.dumps({"jsonrpc": "2.0", "id": request_id, "error": {"code": -32000,
                      "message": f"PlayerBots MCP unavailable: {type(error).__name__}. Check the running module and its local settings."}}), flush=True)
            else:
                print(f"PlayerBots MCP transport error: {type(error).__name__}", file=sys.stderr, flush=True)


if __name__ == "__main__":
    main()
