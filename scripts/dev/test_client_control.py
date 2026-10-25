"""Pure protocol/transport checks; no client attach, login, or saves are touched."""
import contextlib
import io
import json
import unittest

from client_control import chat_packet, normalize, run_request


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


if __name__ == "__main__":
    unittest.main()
