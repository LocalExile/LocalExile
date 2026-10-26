"""Pure protocol/transport checks; no client attach, login, or saves are touched."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest

from client_control import chat_packet, collect_replies, flask_packet, log_mark, normalize, read_replies, run_request, tp_text


class ClientControlTests(unittest.TestCase):
    def test_command_alias_keeps_character_name_intact(self):
        self.assertEqual(normalize("character:play:OfflineWitch"),
                         {"command": "character:play", "name": "OfflineWitch"})
        source = {"command": "login:offline", "password": "test-only"}
        self.assertEqual(normalize(source)["account"], "offline")
        self.assertNotIn("account", source)

    def test_chat_matches_client_compact_wide_string_encoding(self):
        self.assertEqual(bytes(chat_packet("/hit physical 1")), b"\x00\x15\x00\x0f/hit physical 1\x00")
        self.assertEqual(bytes(chat_packet("/\u00e9\U0001f600")),
                         b"\x00\x15\x00\x03/\x80\xe9\x00\xc0\x00\xf6\x01\x00\x00")

    def test_rejects_invalid_request_and_oversized_text(self):
        for value in [None, [], 3, {"command": 7}]:
            with self.assertRaises(ValueError):
                normalize(value)
        for value in ["", "a" * 1025, None]:
            with self.assertRaises(ValueError):
                chat_packet(value)

    def test_result_does_not_echo_password_or_request(self):
        class Reject:
            def execute(self, request):
                raise RuntimeError("Local login rejected (result 6)")
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            result = run_request(Reject(), {"command": "login", "password": "do-not-echo"})
        self.assertFalse(result["ok"])
        self.assertNotIn("do-not-echo", json.dumps(result))
        self.assertEqual("", output.getvalue())

    def test_tp_requests_become_tp_commands(self):
        self.assertEqual(tp_text({"x": 120, "y": 340}), "/tp 120 340")
        self.assertEqual(tp_text({"dx": 5, "dy": -3}), "/tp +5 -3")
        self.assertEqual(tp_text({"id": 0x1234}), "/tp 0x1234")
        self.assertEqual(tp_text({"target": "RhoaChest", "index": 2}), "/tp RhoaChest 2")
        self.assertEqual(tp_text({"text": "GlyphWall"}), "/tp GlyphWall")
        self.assertEqual(tp_text({"list": "chest"}), "/tp list chest")
        self.assertEqual(tp_text({"list": True}), "/tp list")
        for bad in [{}, {"x": 1}, {"x": "1", "y": 2}, {"x": True, "y": 2}, {"target": " "}]:
            with self.assertRaises((ValueError, KeyError)):
                tp_text(bad)

    def test_flask_use_sends_the_flask_key_message(self):
        self.assertEqual(flask_packet({"key": 1}), ([0x00, 0x70, 0, 0, 0, 0], 0))
        self.assertEqual(flask_packet({"slot": 4}), ([0x00, 0x70, 0, 0, 0, 4], 4))
        self.assertEqual(flask_packet({"key": 5})[1], 4)
        for bad in [{}, {"slot": 1, "key": 2}, {"slot": 5}, {"key": 0}, {"key": "1"}, {"slot": True}]:
            with self.assertRaises(ValueError):
                flask_packet(bad)

    def test_replies_are_the_gateway_chat_lines_after_the_mark(self):
        nl = chr(10)
        with tempfile.TemporaryDirectory() as folder:
            log = Path(folder) / "live-gateway.log"
            log.write_text('[gateway] 127.0.0.1:1 => 0x0017 chat line "old": 0017' + nl, encoding="utf-8")
            start = log_mark(log)
            with log.open("a", encoding="utf-8", newline="") as out:
                out.write('[gateway] 127.0.0.1:1 <= 0x0015 chat "/pos"' + nl)
                out.write('[gateway] 127.0.0.1:1 => 0x0017 chat line "(10, 20) in 1_1_3": 00170000' + nl)
                out.write('[gateway] 127.0.0.1:1 => 0x0017 chat line "a "quoted": line": 0017' + nl)
                out.write('[gateway] 127.0.0.1:1 => 0x0017 chat line "partial')
            lines, offset = read_replies(log, start)
            self.assertEqual(lines, ["(10, 20) in 1_1_3", 'a "quoted": line'])
            self.assertLess(offset, log.stat().st_size)  # the unfinished line is left for the next read
            self.assertEqual(collect_replies(start, wait=0.3, quiet=0.05, path=log), lines)
            self.assertIsNone(collect_replies(None))
            self.assertIsNone(log_mark(Path(folder) / "missing.log"))


if __name__ == "__main__":
    unittest.main()
