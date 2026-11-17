import importlib.util
import sys
import unittest
from unittest.mock import patch
from pathlib import Path

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
spec = importlib.util.spec_from_file_location("monster_contacts", HERE / "monster_contacts.py")
mc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mc)


class ActPoolTests(unittest.TestCase):
    """The pools a monster .act maps its animation keys to: the root block's plain clip lists only."""

    def test_root_entries_win_over_nested_stances(self):
        text = (
            'animated_object "Metadata/Monsters/X/X.ao"\r\n{\r\n'
            '\tIdle = "idle";\r\n\tMelee = "attack1, attack2";\r\n\tSpellProjectile = "phase_01_mortar_egg_01";\r\n'
            '\tstance phase_02\r\n\t{\r\n\t\tSpellProjectile = "phase_02_web_spray_02";\r\n\t\tMelee = "attack3";\r\n\t}\r\n'
            '\tEmerge = "emerge" effect_detached "x";\r\n}\r\n')
        pools, ao = mc.act_pools(text, ["Melee", "SpellProjectile", "Emerge", "Idle"])
        self.assertEqual("Metadata/Monsters/X/X.ao", ao)
        self.assertEqual([("attack1", None), ("attack2", None)], pools["Melee"])
        self.assertEqual([("phase_01_mortar_egg_01", None)], pools["SpellProjectile"])
        self.assertEqual([("idle", None)], pools["Idle"])
        self.assertNotIn("Emerge", pools)

    def test_aliases_conditions_and_comments_are_left_out(self):
        text = ('animated_object "a.ao" { // Melee = "commented";\n'
                ' Melee = "@Melee"; SpellProjectile = "attack1:cond=1"; Throw = "a, b"; }')
        pools, _ = mc.act_pools(text, ["Melee", "SpellProjectile", "Throw"])
        self.assertEqual({"Throw": [("a", None), ("b", None)]}, pools)

    def test_budgets_preserve_pool_order_and_reject_unknown_options(self):
        text = ('animated_object "a.ao" { Melee = "attack1:attack_time=0.4,attack2";'
                ' Throw = "throw:attack_time=1.5" effect "x"; }')
        pools, _ = mc.act_pools(text, ["Melee", "Throw"], budgets=True)
        self.assertEqual([("attack1", 0.4), ("attack2", None)], pools["Melee"])
        self.assertEqual([("throw", 1.5)], pools["Throw"])
        self.assertNotIn("Melee", mc.act_pools(text, ["Melee", "Throw"])[0])
        for value in ("attack1,attack2:condition=1", "attack1:attack_time=0", "attack1:attack_time=-1",
                      "attack1:attack_time=nan", "attack1:attack_time=0.4:attack_time=0.5"):
            with self.subTest(value=value):
                self.assertEqual({}, mc.act_pools('animated_object "a.ao" { Melee = "' + value + '"; }', ["Melee"], budgets=True)[0])

    def test_clip_clock_distinguishes_no_span_from_incomplete_or_malformed_events(self):
        clip = {"length": 1166, "events": [("slow_begin", 327, None), ("slow_end", 502, None), ("contact", 566, None)]}
        self.assertEqual({"clock": True, "slow_begin": 327, "slow_end": 502, "budget": 0.55}, mc.clip_clock(clip, 0.55))
        self.assertFalse(mc.clip_clock(clip, None, complete=False)["clock"])
        self.assertTrue(mc.clip_clock({"length": 1766, "events": [("contact", 1099, None)]}, None)["clock"])
        for events in ([('slow_begin', 327, None)], [('slow_end', 502, None)],
                       [('slow_begin', 502, None), ('slow_end', 327, None)],
                       [('slow_begin', 327, None), ('slow_end', 1200, None)],
                       [('slow_begin', 300, None), ('slow_begin', 327, None), ('slow_end', 502, None)]):
            with self.subTest(events=events):
                self.assertFalse(mc.clip_clock({"length": 1166, "events": events}, None)["clock"])
        for payload in ('broken JSON', '{}', '[{}]', '[{"name":"attack1","events":{}}]',
                        '[{"name":"attack1","events":[{"name":"slow_begin","time":"0.3"}]}]',
                        '[{"name":"attack1","events":[{"name":"slow_begin","time":NaN}]}]',
                        '[{"name":"attack1","events":[{"time":0.3}]}]'):
            with self.subTest(payload=payload), patch.object(mc, "read_text", return_value="AnimationController { animations = '" + payload + "' }"):
                self.assertFalse(mc.complete_ao_chain("test.ao"))
                self.assertFalse(mc.clip_clock({"length": 1166, "events": [("contact", 566, None)]}, None,
                                               mc.complete_ao_chain("test.ao"))["clock"])
        with patch.object(mc, "read_text", side_effect=lambda path: 'extends "parent.ao"' if path == "child.ao"
                          else "AnimationController { animations = 'broken JSON' }"):
            self.assertFalse(mc.complete_ao_chain("child.ao"))
        self.assertTrue(mc.complete_ao_events("AnimationController { animations = '[]' }"))
        self.assertFalse(mc.complete_ao_events("AnimationController { animations = '[]'"))

    def test_an_unclosed_block_yields_nothing(self):
        pools, ao = mc.act_pools('animated_object "a.ao" { Melee = "a";', ["Melee"])
        self.assertEqual({}, pools)
        self.assertEqual("a.ao", ao)

    def test_variety_model_overrides_act_model_without_guessing_ambiguous_bindings(self):
        self.assertEqual(("Mummy_no_amb.ao", True),
                         mc.animation_source(["Mummy.act"], ["Mummy_no_amb.ao"], "Mummy.ao"))
        for acts, aos, type_ao in ((["a.act", "b.act"], ["a.ao"], None),
                                  (["a.act"], ["a.ao", "b.ao"], None),
                                  (["a.act"], ["a.ao"], "override.ao"), (["a.act"], [], None)):
            with self.subTest(acts=acts, aos=aos, type_ao=type_ao):
                self.assertFalse(mc.animation_source(acts, aos, "a.ao", type_ao)[1])


if __name__ == "__main__":
    unittest.main()
