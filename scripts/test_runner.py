"""Hermetic tests for the Hermes launcher (all app calls mocked)."""
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('run_sync', Path(__file__).with_name('run_sync.py'))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(dir=os.environ['TMPDIR'])
        self.runtime = Path(self.directory.name)
        self.scope = patch.object(runner, 'RUNTIME', self.runtime)
        self.scope.start()
        self.addCleanup(self.scope.stop)
        self.addCleanup(self.directory.cleanup)
        self.credentials = self.runtime / 'school.env'
        self.credentials.write_text('AESO_SCHOOL_USERNAME="test-user"\nAESO_SCHOOL_PASSWORD="dummy-test-value"\n')
        self.credentials.chmod(0o600)

    def result(self, created=0, deleted=0):
        return subprocess.CompletedProcess([], 0, json.dumps({
            'status': 'success', 'created': created, 'deleted': deleted, 'kept': 4,
            'actions': [], 'sourceCount': 4, 'futureCount': 4}), '')

    def test_apply_disabled_never_starts_app(self):
        with patch.object(runner.subprocess, 'run') as call, patch('sys.stderr', io.StringIO()):
            self.assertEqual(1, runner.run(False))
            call.assert_not_called()

    def test_preview_never_enables_writes_or_changes_recovery_state(self):
        with patch.object(runner.subprocess, 'run', return_value=self.result(2)) as call, patch('sys.stdout', io.StringIO()) as out:
            self.assertEqual(0, runner.run(True))
            self.assertIn('--dry-run', call.call_args.args[0])
            self.assertNotIn('--apply', call.call_args.args[0])
            self.assertEqual(2, json.loads(out.getvalue())['created'])
            self.assertFalse((self.runtime / 'apply-enabled').exists())
            self.assertFalse((self.runtime / 'status.json').exists())

    def test_noop_is_silent_and_credentials_only_in_child_environment(self):
        (self.runtime / 'apply-enabled').touch()
        with patch.object(runner.subprocess, 'run', return_value=self.result()) as call, patch('sys.stdout', io.StringIO()) as out:
            self.assertEqual(0, runner.run(False))
            self.assertEqual('', out.getvalue())
            self.assertNotIn('dummy-test-value', str(call.call_args.args[0]))
            self.assertEqual('dummy-test-value', call.call_args.kwargs['env']['AESO_SCHOOL_PASSWORD'])
            self.assertFalse(json.loads((self.runtime / 'status.json').read_text())['failed'])

    def test_failures_are_sanitized_and_recovery_is_reported(self):
        (self.runtime / 'apply-enabled').touch()
        failure = subprocess.CompletedProcess([], 1, '', 'sensitive-provider-value')
        with patch.object(runner.subprocess, 'run', return_value=failure), patch('sys.stderr', io.StringIO()) as out:
            self.assertEqual(1, runner.run(False))
            self.assertNotIn('sensitive-provider-value', out.getvalue())
            self.assertTrue(json.loads((self.runtime / 'status.json').read_text())['failed'])
        with patch.object(runner.subprocess, 'run', return_value=self.result()), patch('sys.stdout', io.StringIO()) as out:
            self.assertEqual(0, runner.run(False))
            self.assertIn('recovered', out.getvalue())

    def test_changes_are_reported_without_discord_mentions(self):
        text = runner.summary({'created': 1, 'deleted': 0, 'kept': 4, 'actions': [
            {'kind': 'create', 'summary': '[AESO] @everyone\nTest', 'start': '2026-11-01'}]}, False)
        self.assertNotIn('@everyone', text)
        self.assertIn('Created: 1', text)

    def test_world_readable_credentials_are_refused(self):
        self.credentials.chmod(0o644)
        with self.assertRaises(ValueError):
            runner.credentials(self.credentials)

    def test_wrong_counts_fail_not_silent_success(self):
        (self.runtime / 'apply-enabled').touch()
        bad = subprocess.CompletedProcess([], 0, '{"status":"success","created":true,"deleted":0,"kept":0}', '')
        with patch.object(runner.subprocess, 'run', return_value=bad), patch('sys.stderr', io.StringIO()):
            self.assertEqual(1, runner.run(False))


if __name__ == '__main__':
    unittest.main()
