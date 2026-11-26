import importlib.util
import sys
import unittest
from unittest.mock import patch
from pathlib import Path

HERE = Path(__file__).resolve().parents[1]
sys.path.append(str(HERE))
spec = importlib.util.spec_from_file_location("monster_contacts", HERE / "monster_contacts.py")
mc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mc)


class ActPoolTests(unittest.TestCase):
    """The pools a monster .act maps its animation keys to: root-block clips, validated modifiers, aliases, and views."""

    def test_root_entries_win_over_nested_stances_and_accept_validated_modifiers(self):
        text = (
            'animated_object "Metadata/Monsters/X/X.ao"\r\n{\r\n'
            '\tIdle = "idle";\r\n\tMelee = "attack1, attack2" with_main_hand;\r\n'
            '\tSpellProjectile = "phase_01_mortar_egg_01" effect_locked "a.ao" "anim";\r\n'
            '\tSpellProjectileFire = "dash_bow" item_animation OffHand using "dash_bow" effect "b.ao" "once";\r\n'
            '\tSpellProjectileCold = "@SpellProjectile";\r\n'
            '\tstance phase_02\r\n\t{\r\n\t\tSpellProjectile = "phase_02_web_spray_02";\r\n\t\tMelee = "attack3";\r\n\t}\r\n'
            '\tEmerge = "emerge" effect_detached "x.ao" "animate";\r\n'
            '\tTakeHit = "bad" unknown_modifier "y";\r\n}\r\n')
        pools, ao = mc.act_pools(text, ["Melee", "SpellProjectile", "SpellProjectileFire", "SpellProjectileCold", "Emerge", "Idle", "TakeHit"])
        self.assertEqual("Metadata/Monsters/X/X.ao", ao)
        self.assertEqual([("attack1", None), ("attack2", None)], pools["Melee"])
        self.assertEqual([("phase_01_mortar_egg_01", None)], pools["SpellProjectile"])
        self.assertEqual([("dash_bow", None)], pools["SpellProjectileFire"])
        self.assertEqual([("phase_01_mortar_egg_01", None)], pools["SpellProjectileCold"])
        self.assertEqual([("emerge", None)], pools["Emerge"])
        self.assertEqual([("idle", None)], pools["Idle"])
        self.assertNotIn("TakeHit", pools)

    def test_cycles_conditions_and_comments_are_left_out(self):
        text = ('animated_object "a.ao" { // Melee = "commented";\n'
                ' Melee = "@Melee"; SpellProjectile = "attack1:cond=1"; Throw = "a, b"; }')
        pools, _ = mc.act_pools(text, ["Melee", "SpellProjectile", "Throw"])
        self.assertEqual({"Throw": [("a", None), ("b", None)]}, pools)

    def test_budgets_and_weights_preserve_pool_order_and_reject_unknown_options(self):
        text = ('animated_object "a.ao" { Melee = "attack1:attack_time=0.4,attack2:weight=70";'
                ' Throw = "throw:attack_time=1.5" effect "x" "y" & effect_locked "z" "w"; }')
        pools, _ = mc.act_pools(text, ["Melee", "Throw"], budgets=True, weights=True)
        self.assertEqual([("attack1", 0.4, 1), ("attack2", None, 70)], pools["Melee"])
        self.assertEqual([("throw", 1.5, 1)], pools["Throw"])
        self.assertNotIn("Melee", mc.act_pools(text, ["Melee", "Throw"])[0])
        for value in ("attack1,attack2:condition=1", "attack1:attack_time=0", "attack1:attack_time=-1",
                      "attack1:attack_time=nan", "attack1:attack_time=0.4:attack_time=0.5",
                      "attack1:weight=0", "attack1:weight=abc"):
            with self.subTest(value=value):
                self.assertEqual({}, mc.act_pools('animated_object "a.ao" { Melee = "' + value + '"; }', ["Melee"], budgets=True, weights=True)[0])

    def test_ao_views_and_named_events_merge_with_exclusions(self):
        amd = 'version 1\n1\n"attack1"\nonce\n1000\n2\n"contact"\n416\n"1 2 3"\n"trigger_proj"\n500\n"0 0 0"\n'
        ao = (
            'AnimationController {\n'
            '  metadata = "rig.amd"\n'
            '  view_of = "attack1"\n'
            '    view_name = "attack1_01"\n'
            '    view_exclude_event = "StandardEvent"\n'
            '  animations = \'[{"name":"attack1_01","events":['
            '{"type":"StandardEventType","time":0.359,"name":"contact","position":[0.0,6.4,-57.88]},'
            '{"type":"MoveEventType","time":0.1,"end_time":0.3,"name":"move","data":{"Terrain":true}}'
            ']}]\'\n'
            '}\n'
        )
        with patch.object(mc, "read_text", side_effect=lambda p: amd if p == "rig.amd" else ao if p == "m.ao" else None):
            clips = mc.rig_clips_with_views("rig.amd", "m.ao")
        self.assertIn("attack1", clips)
        self.assertIn("attack1_01", clips)
        idx, view = clips["attack1_01"]
        self.assertEqual(0, idx)
        self.assertEqual(1000, view["length"])
        names = [e[0] for e in view["events"]]
        self.assertEqual(["move", "contact"], names)
        self.assertEqual(359, view["events"][1][1])
        self.assertEqual({"end_ms": 300, "data": {"Terrain": True}, "type": "MoveEventType"}, view["events"][0][3])

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
