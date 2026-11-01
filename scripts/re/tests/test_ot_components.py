import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("ot_components", Path(__file__).resolve().parents[1] / "ot_components.py")
ot = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ot)


class LifeMetadataTests(unittest.TestCase):
    def merged(self, *texts):
        files = {f"Type{i}": ot.parse(text) for i, text in enumerate(texts)}
        return ot.merge(f"Type{len(texts) - 1}", files, {})

    def test_empty_life_uses_proven_native_defaults(self):
        result = self.merged('extends "nothing" Life {}')
        self.assertEqual({"corpse_usable": True, "prevent_corpse_sinking": False,
                          "corpse_use_veto": False}, result["life"])

    def test_child_false_overrides_parent_true_and_empty_child_keeps_false(self):
        result = self.merged('Life { corpse_usable = true prevent_corpse_sinking = true }',
                             'extends "Type0" Life { corpse_usable = false }',
                             'extends "Type1" Life {}')
        self.assertFalse(result["life"]["corpse_usable"])
        self.assertTrue(result["life"]["prevent_corpse_sinking"])

    def test_child_true_overrides_parent_false(self):
        result = self.merged('Life { corpse_usable = false }',
                             'extends "Type0" Life { corpse_usable = true }')
        self.assertTrue(result["life"]["corpse_usable"])

    def test_nested_scripts_client_and_server_properties_do_not_override_common_life(self):
        result = self.merged('''Life { corpse_usable = false
            on_death = { corpse_usable = true prevent_corpse_sinking = true }
            }
            client { Life { corpse_usable = true } }
            server { Life { corpse_usable = true } }''')
        self.assertFalse(result["life"]["corpse_usable"])
        self.assertFalse(result["life"]["prevent_corpse_sinking"])

    def test_missing_parent_does_not_invent_defaults_or_absent_veto(self):
        result = self.merged('extends "Missing" Life {}')
        self.assertEqual(["Missing"], result["missing"])
        self.assertEqual(dict.fromkeys(("corpse_usable", "prevent_corpse_sinking", "corpse_use_veto")),
                         result["life"])

    def test_explicit_child_false_survives_missing_parent(self):
        result = self.merged('extends "Missing" Life { corpse_usable = false }')
        self.assertFalse(result["life"]["corpse_usable"])
        self.assertIsNone(result["life"]["prevent_corpse_sinking"])
        self.assertIsNone(result["life"]["corpse_use_veto"])

    def test_later_missing_parent_invalidates_earlier_parent_flags(self):
        result = self.merged('Life { corpse_usable = false }',
                             'extends "Type0" extends "Missing" Life {}')
        self.assertIsNone(result["life"]["corpse_usable"])

    def test_player_component_sets_veto_even_when_another_parent_is_missing(self):
        result = self.merged('Player {}', 'extends "Type0" extends "Missing" Life {}')
        self.assertTrue(result["life"]["corpse_use_veto"])

    def test_unknown_literal_stays_unknown(self):
        result = self.merged('Life { corpse_usable = unknown }')
        self.assertIsNone(result["life"]["corpse_usable"])

    def test_emergence_actions_and_type_order_survive_life_extraction(self):
        result = self.merged('Life {} Actor { basic_action = "Move" }', '''extends "Type0"
            Actor { basic_action = "Emerge" } Transitionable { num_states = 3 }
            client { Targetable { targetable = false }
                Transitionable { on_transition = { EnableTargetable(); } } }''')
        self.assertEqual(["Positioned", "Life", "Actor", "Transitionable", "Targetable"], result["components"])
        self.assertEqual(4, result["read"])
        self.assertEqual(["Move", "Emerge"], result["basic_actions"])
        self.assertEqual(3, result["transitionable"]["num_states"])
        self.assertFalse(result["client_targetable"])
        self.assertIn("EnableTargetable", result["client_transitionable"]["on_transition"])

    def test_item_output_does_not_acquire_life_data(self):
        result = ot.merge("Item", {"Item": ot.parse('Base {} Mods {}')}, {}, first=())
        self.assertNotIn("life", result)

    def test_collision_metadata_and_life_flags_survive_the_same_inheritance_chain(self):
        result = self.merged('''Positioned { object_size = 5 blocking = true }
            Animated { animated_object = "base.ao" }
            Life { corpse_usable = true prevent_corpse_sinking = true }''', '''extends "Type0"
            Positioned { object_size = 8 static = true block_league_flag = "test_flag" }
            Animated { animated_object = "child.ao" } Life { corpse_usable = false }
            client { Positioned { object_size = 99 blocking = false }
                Animated { animated_object = "client.ao" } Life { corpse_usable = true } }''')
        self.assertEqual({"object_size": "8", "blocking": "true", "static": "true",
                          "block_league_flag": "test_flag"}, result["positioned"])
        self.assertEqual("child.ao", result["animated_object"])
        self.assertEqual({"corpse_usable": False, "prevent_corpse_sinking": True,
                          "corpse_use_veto": False}, result["life"])
        self.assertEqual(["Positioned", "Animated", "Life"], result["components"])
        self.assertEqual(3, result["read"])

    def test_pinned_cached_summon_inheritance_forbids_corpse_reuse(self):
        types = ["Metadata/Monsters/RaisedZombies/RaisedZombieFour",
                 "Metadata/Monsters/SummonedSpider/SummonedSpider"]
        files, pending = {}, list(types)
        while pending:
            path = pending.pop()
            if path in files or path == "nothing":
                continue
            text = ot.read_text(path + ".ot")
            if text is None:
                self.skipTest("Pinned GGPK OT cache is not available")
            files[path] = ot.parse(text)
            pending.extend(files[path]["extends"])
        for path in types:
            result = ot.merge(path, files, {})
            self.assertEqual([], result["missing"])
            self.assertFalse(result["life"]["corpse_usable"], path)


class AnimationEventTests(unittest.TestCase):
    """The .ao chain's animation events merged over the rig's (rig_clips), as the client plays them."""

    AMD = '"1"\n2\n"attack1"\nonce\n1000\n1\n"contact"\n416\n0.89871 24.6111 -57.8849\n\n\n"idle"\nloop\n1000\n0\n\n\n'

    def with_files(self, files, body):
        saved = ot.read_text, ot.fetch
        ot.read_text, ot.fetch = (lambda path: files.get(path)), (lambda paths: None)
        try:
            return body()
        finally:
            ot.read_text, ot.fetch = saved

    def test_block_keeps_braces_inside_quoted_lists(self):
        text = 'AnimationController\n{\n\tanimations = \'[{"a": {}}]\'\n}\nclient\n{\n}\n'
        self.assertEqual('\n\tanimations = \'[{"a": {}}]\'\n', ot._block(text, "AnimationController"))
        self.assertIsNone(ot._block(text, "Missing"))

    def test_block_skips_comments_and_escaped_quotes(self):
        text = "AnimationController\n{\n\t// it's a comment\n\tmetadata = \"a\\\"b\"\n}\nclient\n{\n}\n"
        self.assertEqual("\n\t// it's a comment\n\tmetadata = \"a\\\"b\"\n", ot._block(text, "AnimationController"))

    def test_exclude_metadata_events_replaces_the_rig_events(self):
        files = {"m.ao": 'version 3\nextends "Metadata/Parent"\n\nAnimationController\n{\n\texclude_metadata_events = true\n\tmetadata = "rig.amd"\n\tanimations = \'[{"name": "attack1", "events": [{"type": "StandardEventType", "time": 0.3745180070400238, "name": "contact", "position": [1.33, 45.89, -71.56]}]}]\'\n}\n', "Metadata/Parent.ao": 'version 2\nextends "nothing"\n', "rig.amd": self.AMD}
        clips = self.with_files(files, lambda: ot.rig_clips("rig.amd", "m.ao", fetch_missing=False))
        self.assertEqual([("contact", 375, [1.33, 45.89, -71.56])], clips[0]["events"])
        self.assertEqual((375, [1.33, 45.89, -71.56]), ot.first_contact(clips[0]["events"]))
        self.assertEqual([], clips[1]["events"])

    def test_without_the_flag_the_ao_events_are_added_in_time_order(self):
        files = {"m.ao": 'AnimationController\n{\n\tmetadata = "rig.amd"\n\tanimations = \'[{"name": "attack1", "events": [{"type": "StandardEventType", "time": 0.1, "name": "melee_trail"}]}]\'\n}\n', "rig.amd": self.AMD}
        clips = self.with_files(files, lambda: ot.rig_clips("rig.amd", "m.ao", fetch_missing=False))
        self.assertEqual([("melee_trail", 100, None), ("contact", 416, [0.89871, 24.6111, -57.8849])], clips[0]["events"])
        self.assertEqual((416, [0.89871, 24.6111, -57.8849]), ot.first_contact(clips[0]["events"]))
        self.assertIsNone(ot.first_contact(clips[1]["events"]))


if __name__ == "__main__":
    unittest.main()
