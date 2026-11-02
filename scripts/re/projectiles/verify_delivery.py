#!/usr/bin/env python3
"""Offline public reference and synthetic-tool checks. Never loads/attaches a client."""
from pathlib import Path
import subprocess
import sys


def main():
    if not __debug__:
        raise SystemExit('Run without Python -O: retained reference self-tests use assertions.')
    root = Path(__file__).resolve().parents[3]
    folder = 'scripts/re/projectiles/'
    commands = [[sys.executable, folder + name, '--self-test'] for name in (
        'native_tornado_rng.py', 'native_projectile_rng.py', 'native_projectile_raster.py')]
    commands += [[sys.executable, '-m', 'unittest', 'discover', '-s', folder, '-p', pattern] for pattern in (
        'test_delivery_vectors.py', 'test_execution_capture.py', 'test_runner.py')]
    commands += [['node', '--test', folder + 'test_execution_probe.js']]
    for command in commands:
        print('Running:', ' '.join(command), flush=True)
        subprocess.run(command, cwd=root, check=True)
    print('PASS: public scalar/vector and synthetic observer/analyzer checks; no native or live parity claim.')


if __name__ == '__main__':
    main()
