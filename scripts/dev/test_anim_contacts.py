import sys
import unittest
from pathlib import Path

sys.path.append(str(Path(__file__).resolve().parents[1] / "re"))
from anim_contacts import ordinary_wand_pools


def clip(name, contact):
    # ot_components.rig_clips events: (name, ms, position or None)
    return {"name": name, "length": 600, "events": [("contact", contact, None)]}


class OrdinaryWandContactExtractionTests(unittest.TestCase):
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
                      clip("attack1_wand_wand", 300)]
        pools = ordinary_wand_pools(text, animations)
        self.assertEqual([1, 2, 3, 4], [a["index"] for a in pools["Wand"]])
        self.assertEqual([300, 233, 200, 366], [a["contact"] for a in pools["Wand"]])
        self.assertEqual([5, 6], [a["index"] for a in pools["WandShield"]])
        self.assertEqual([7], [a["index"] for a in pools["WandWand"]])
        self.assertTrue(all(a["weight"] == 1 for pool in pools.values() for a in pool))

    def test_templar_dual_wand_inherits_melee_and_does_not_mistake_offhand_for_main(self):
        text = '''animated_object "templar.ao" { stance Wand {
            Melee = "attack1_wand";
            stance WandWand { OffMelee = "attack1_wand_wand_right"; }
        } }'''
        pools = ordinary_wand_pools(text, [clip("attack1_wand", 200)])
        self.assertEqual(pools["Wand"], pools["WandWand"])
        self.assertEqual(pools["Wand"], pools["WandShield"])

    def test_comment_and_quoted_delimiters_do_not_change_nesting(self):
        text = '''animated_object "test{.ao" {
            // stance Wand { Melee = "wrong"; }
            stance Wand { Idle = "brace};"; /* } */ Melee = "attack1_wand"; }
        }'''
        self.assertEqual("attack1_wand", ordinary_wand_pools(text, [clip("attack1_wand", 233)])["Wand"][0]["animation"])

    def test_unsupported_alias_modifier_or_missing_contact_fails_explicitly(self):
        for expression in ("@Melee", "attack1_wand:attack_time=0.5", "attack1_wand weight 2", ""):
            with self.subTest(expression=expression), self.assertRaises(ValueError):
                ordinary_wand_pools('animated_object "x" { stance Wand { Melee = "' + expression + '"; } }',
                                    [clip("attack1_wand", 300)])
        with self.assertRaises(ValueError):
            ordinary_wand_pools('animated_object "x" { stance Wand { Melee = "attack1_wand"; } }',
                                [{"name": "attack1_wand", "length": 600, "events": []}])


if __name__ == "__main__":
    unittest.main()
