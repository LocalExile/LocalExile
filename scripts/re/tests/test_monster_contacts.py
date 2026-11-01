import importlib.util
import sys
import unittest
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
        self.assertEqual(["attack1", "attack2"], pools["Melee"])
        self.assertEqual(["phase_01_mortar_egg_01"], pools["SpellProjectile"])
        self.assertEqual(["idle"], pools["Idle"])
        self.assertNotIn("Emerge", pools)

    def test_aliases_conditions_and_comments_are_left_out(self):
        text = ('animated_object "a.ao" { // Melee = "commented";\n'
                ' Melee = "@Melee"; SpellProjectile = "attack1:cond=1"; Throw = "a, b"; }')
        pools, _ = mc.act_pools(text, ["Melee", "SpellProjectile", "Throw"])
        self.assertEqual({"Throw": ["a", "b"]}, pools)

    def test_an_unclosed_block_yields_nothing(self):
        pools, ao = mc.act_pools('animated_object "a.ao" { Melee = "a";', ["Melee"])
        self.assertEqual({}, pools)
        self.assertEqual("a.ao", ao)


if __name__ == "__main__":
    unittest.main()
