"""Runner regression controls using fake Frida objects only; never attach."""
from contextlib import redirect_stdout
import importlib.util
import io
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch


class RunnerTests(unittest.TestCase):
    def run_fake(self, mode='normal', finish_error=False):
        events = []
        class Script:
            def on(self, *args):
                pass
            def load(self):
                if mode == 'load-failure':
                    raise RuntimeError('load failed')
                if mode == 'external-detach':
                    session.callback('process-terminated', None)
            def list_exports_sync(self):
                return ['finish']
            @property
            def exports_sync(self):
                def finish():
                    events.append('finish')
                    if finish_error:
                        raise RuntimeError('finish failed')
                return SimpleNamespace(finish=finish)
        class Session:
            def on(self, event, callback):
                self.callback = callback
            def create_script(self, *args, **kwargs):
                return Script()
            def detach(self):
                events.append('detach')
                self.callback('application-requested', None)
        session = Session()
        def attach(pid):
            self.assertEqual(pid, 123)
            events.append('attach')
            return session
        fake = SimpleNamespace(get_local_device=lambda: SimpleNamespace(attach=attach))
        spec = importlib.util.spec_from_file_location('projectile_fake_runner',
            Path(__file__).resolve().parents[1] / 'frida' / 'run.py')
        runner = importlib.util.module_from_spec(spec)
        with patch.dict(sys.modules, {'frida': fake}):
            spec.loader.exec_module(runner)
        output = io.StringIO()
        with tempfile.TemporaryDirectory() as temp:
            probe = Path(temp) / 'probe.js'
            probe.write_text('// fake', encoding='utf-8')
            log = Path(temp) / 'capture.jsonl'
            argv = ['run.py', '123', str(probe), '--finish-rpc', 'finish', '--log', str(log)]
            with patch.object(sys, 'argv', argv), patch.object(runner.time, 'sleep', side_effect=KeyboardInterrupt), redirect_stdout(output):
                if mode == 'load-failure':
                    with self.assertRaisesRegex(RuntimeError, 'load failed'):
                        runner.main()
                else:
                    runner.main()
            # Reopening for exclusive replacement on Windows also checks closure of the runner's log.
            log.replace(Path(temp) / 'closed.jsonl')
        return events, output.getvalue()

    def test_finish_precedes_normal_detach(self):
        events, _ = self.run_fake()
        self.assertEqual(events, ['attach', 'finish', 'detach'])

    def test_load_failure_still_detaches(self):
        events, _ = self.run_fake('load-failure')
        self.assertEqual(events, ['attach', 'detach'])

    def test_external_detach_stops_and_does_not_request_finish(self):
        events, output = self.run_fake('external-detach')
        self.assertEqual(events, ['attach'])
        self.assertIn('process-terminated', output)

    def test_finish_error_still_detaches(self):
        events, output = self.run_fake(finish_error=True)
        self.assertEqual(events, ['attach', 'finish', 'detach'])
        self.assertIn('finish RPC failed', output)


if __name__ == '__main__':
    unittest.main()
