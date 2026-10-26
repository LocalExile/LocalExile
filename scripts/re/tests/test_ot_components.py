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


if __name__ == "__main__":
    unittest.main()
