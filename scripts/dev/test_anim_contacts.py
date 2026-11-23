import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1] / "re"))
from anim_contacts import default_attack_pools


def clip(name, contact, length=600):
    # ot_components.rig_clips events: (name, ms, position or None)
    return {"name": name, "length": length, "events": [("contact", contact, None)]}


class DefaultAttackPoolTests(unittest.TestCase):
    def test_pool_order_indices_contacts_and_nested_stance_overrides(self):
        text = '''animated_object "test.ao" {
          Melee = "unarmed:attack_time=0.5";
          stance Other { Melee = "unrelated"; }
          stance Wand {
            Melee = "attack1_wand, attack2_wand, attack3_wand, attack4_wand";
            stance WandShield { Melee = "attack1_wand_shield, attack2_wand_shield"; }
            stance WandWand { Melee = "attack1_wand_wand"; OffMelee = "attack1_wand_left"; }
          }
        }'''
        animations = [clip("unrelated", 1), clip("attack1_wand", 300), clip("attack2_wand", 233),
                      clip("attack3_wand", 200), clip("attack4_wand", 366),
                      clip("attack1_wand_shield", 300), clip("attack2_wand_shield", 233),
                      clip("attack1_wand_wand", 300), clip("unarmed", 250)]
        pools = default_attack_pools(text, animations, {})
        self.assertEqual([1, 2, 3, 4], [a["index"] for a in pools["Wand"]])
        self.assertEqual([300, 233, 200, 366], [a["contact"] for a in pools["Wand"]])
        self.assertEqual([5, 6], [a["index"] for a in pools["WandShield"]])
        self.assertEqual([7], [a["index"] for a in pools["WandWand"]])
        self.assertEqual([(8, 0.5)], [(a["index"], a["budget"]) for a in pools["Unarmed"]])
        self.assertTrue(all(a["weight"] == 1 for pool in pools.values() for a in pool))

    def test_a_stance_without_its_own_melee_keeps_its_parents(self):
        text = '''animated_object "templar.ao" { Melee = "unarmed"; stance Wand {
            Melee = "attack1_wand";
            stance WandWand { OffMelee = "attack1_wand_wand_right"; }
        } }'''
        pools = default_attack_pools(text, [clip("unarmed", 250), clip("attack1_wand", 200)], {})
        self.assertEqual(pools["Wand"], pools["WandWand"])
        self.assertEqual("attack1_wand", pools["Wand"][0]["animation"])

    def test_comment_and_quoted_delimiters_do_not_change_nesting(self):
        text = '''animated_object "test{.ao" {
            Melee = "unarmed";
            // stance Wand { Melee = "wrong"; }
            stance Wand { Idle = "brace};"; /* } */ Melee = "attack1_wand"; }
        }'''
        pools = default_attack_pools(text, [clip("unarmed", 250), clip("attack1_wand", 233)], {})
        self.assertEqual("attack1_wand", pools["Wand"][0]["animation"])

    def test_a_view_is_cut_from_its_clip_and_brings_its_pair(self):
        text = 'animated_object "x" { Melee = "attack_sword_01a"; }'
        strike = {"name": "doublestrike_sword", "length": 800, "events": [("contact", 200, None), ("contact", 500, None)]}
        views = {"attack_sword_01a": ("doublestrike_sword", 0, 365), "attack_sword_01b": ("doublestrike_sword", 365, None)}
        first, second = default_attack_pools(text, [strike], views)["Unarmed"]
        self.assertEqual(("attack_sword_01a", 365, 200, True), (first["animation"], first["length"], first["contact"], first["pair"]))
        self.assertEqual(("attack_sword_01b", 435, 135, True), (second["animation"], second["length"], second["contact"], second["pair"]))

    def test_unsupported_expression_or_missing_clip_or_contact_fails_explicitly(self):
        for expression in ("attack1_wand:speed=2", "attack1_wand weight 2", "", "no_such_clip"):
            with self.subTest(expression=expression), self.assertRaises(ValueError):
                default_attack_pools('animated_object "x" { Melee = "' + expression + '"; }', [clip("attack1_wand", 300)], {})
        with self.assertRaises(ValueError):
            default_attack_pools('animated_object "x" { Melee = "attack1_wand"; }',
                                 [{"name": "attack1_wand", "length": 600, "events": []}], {})
        with self.assertRaises(ValueError):
            default_attack_pools('animated_object "x" { stance Wand { Melee = "attack1_wand"; } }', [clip("attack1_wand", 300)], {})


if __name__ == "__main__":
    unittest.main()
