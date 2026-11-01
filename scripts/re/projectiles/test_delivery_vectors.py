"""Published vectors projected from preexisting ignored evidence, not a live replay."""
import json
from pathlib import Path
import unittest

from native_tornado_rng import TerminalRandom, angle_from_sample, float_bits


class DeliveryVectors(unittest.TestCase):
    def test_retained_terminal_vectors(self):
        fixture = json.loads((Path(__file__).parent / 'fixtures' / 'terminal-rng.json').read_text(encoding='utf-8'))
        self.assertEqual(fixture['evidence_type'], 'historical-instruction-interpretation-projection')
        self.assertGreaterEqual(len(fixture['vectors']), 4)
        for case in fixture['vectors']:
            with self.subTest(words=case['words']):
                stream = TerminalRandom(case['words'])
                self.assertEqual(stream.state, case['warm_state'])
                self.assertEqual([stream.next_u32() for _ in range(5)], case['raw_outputs'])
                stream = TerminalRandom(case['words'])
                self.assertEqual(stream.bounded(7001), case['angle_sample'])
                self.assertEqual(float_bits(angle_from_sample(case['angle_sample'])), case['angle_bits'])
                self.assertEqual(stream.steps - 8, case['sample_transitions'])


if __name__ == '__main__':
    unittest.main()
