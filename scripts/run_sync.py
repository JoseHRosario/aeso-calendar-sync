#!/usr/bin/env python3
"""Hermes no-agent entry point. Secrets never travel through arguments/stdout."""
from __future__ import annotations

import argparse
import fcntl
import json
import os
from pathlib import Path
import subprocess
import sys
import re
import tempfile


def safe_app_error(stderr: str) -> str:
    """Allow only known app diagnostics; never save arbitrary subprocess output."""
    message = stderr.strip()
    known = {
        'School requires CAPTCHA; manual sign-in required.', 'School login rejected.',
        'School CAPTCHA check invalid.', 'Missing school environment credentials.',
        'School selection lacks enrolment evidence.',
        'Configured enrolment and school year not authorized by selected student.',
        'School year is not current; reconfigure enrolment before continuing.',
        'School exceeded requested source limit.', 'Invalid source fields.',
        'Source dates disagree.', 'Source outside configured school year.',
        'Invalid or ambiguous Lisbon time.', 'Invalid source time interval.',
        'Malformed source list.', 'Source changed during coverage expansion.',
        'Source changed during final confirmation.',
        'Empty source cannot authorize reconciliation.',
        'Source coverage cap reached; reconciliation blocked.',
        'Google pagination did not advance.', 'Google pagination cap reached.',
        'Google write readback did not match requested event.',
        'Google event changed after planning; refusing write.',
        'Google event lacks concurrency evidence.',
        'Google managed event lacks planning concurrency evidence.',
        'Google planned event lacks concurrency evidence.',
        'Google event is protected from writes.',
        'Google replacement target disappeared; rerun preview.',
        'Google insert conflict does not match requested event.',
        'Google delete readback still found active event.',
        'Google credentials must be a service account.',
        'Local configuration or credential file could not be read.',
        'Configuration or credential JSON is invalid.',
        'Operation timed out or was cancelled.',
        'Synchronization failed; no sensitive diagnostics emitted.',
        'Invalid AWS secret configuration.', 'AWS runtime profile is unavailable.',
        'SSM secret retrieval failed.', 'SSM secret snapshot is invalid or incomplete.',
        'SSM secret payload is invalid.',
    }
    if message in known or re.fullmatch(
        r'(School|Google) (request failed \(HTTP [0-9]{3}\)|transport failed|returned malformed JSON)\.', message
    ):
        return message
    return 'Application failed; unrecognized stderr was omitted for credential safety.'

HOME = Path('/home/xungaman/.hermes/profiles/meireles')
RUNTIME = HOME / 'aeso-calendar-sync'
APP = RUNTIME / 'app' / 'Aeso.CalendarSync.dll'


def atomic_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor, name = tempfile.mkstemp(prefix=path.stem + '-', suffix='.tmp', dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, 'w') as stream:
            os.fchmod(stream.fileno(), 0o600)
            json.dump(value, stream)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def summary(result: dict, recovery: bool) -> str:
    created, deleted = result['created'], result['deleted']
    lines = ['[AESO] Calendar sync recovered.' if recovery else '[AESO] Calendar updated.']
    lines.append(f'Created: {created}; deleted: {deleted}; unchanged: {result["kept"]}.')
    for action in result.get('actions', [])[:12]:
        title = str(action.get('summary', '')).replace('\n', ' ').replace('\r', ' ')
        # Never interpret school-controlled text as Discord mentions.
        title = title.replace('@', '@\u200b')
        lines.append(f'{action.get("kind", "change")}: {title} — {action.get("start", "")}')
    remaining = len(result.get('actions', [])) - 12
    if remaining > 0:
        lines.append(f'{remaining} additional changes; see the local reconciliation report.')
    return '\n'.join(lines)[:1800]


def run(preview: bool) -> int:
    RUNTIME.mkdir(parents=True, exist_ok=True, mode=0o700)
    with (RUNTIME / 'run.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            print('[AESO] Another sync is already running; this attempt was skipped.', file=sys.stderr)
            return 1
        if not preview and not (RUNTIME / 'apply-enabled').is_file():
            print('[AESO] Automatic writes are disabled pending a verified live preview.', file=sys.stderr)
            return 1
        env = os.environ.copy()
        # Application secrets are read from SSM by the C# process, never injected here.
        for key in ('AESO_SCHOOL_USERNAME', 'AESO_SCHOOL_PASSWORD'):
            env.pop(key, None)
        diagnostic = {'phase': 'preview' if preview else 'apply', 'message': 'Local setup or launcher failed.'}
        try:
            completed = subprocess.run(
                [str(Path.home() / '.dotnet' / 'dotnet'), str(APP), '--config',
                 str(RUNTIME / 'config.json'), '--dry-run' if preview else '--apply'],
                env=env, capture_output=True, text=True, timeout=180, check=False,
            )
            if completed.returncode:
                # Retain only allowlisted diagnostics, never raw subprocess/provider output.
                diagnostic.update(exitCode=completed.returncode, message=safe_app_error(completed.stderr))
                raise RuntimeError('Sync execution failed. Review last-failure.json.')
            result = json.loads(completed.stdout)
            if result.get('status') != 'success':
                raise RuntimeError('Sync returned an invalid status.')
            for key in ('created', 'deleted', 'kept'):
                if type(result.get(key)) is not int or result[key] < 0:
                    raise RuntimeError('Sync returned invalid reconciliation counts.')
            if not isinstance(result.get('actions', []), list):
                raise RuntimeError('Sync returned invalid actions.')
            if preview:
                print(json.dumps(result, ensure_ascii=False, indent=2))
                return 0
            state_path = RUNTIME / 'status.json'
            previous = json.loads(state_path.read_text()) if state_path.exists() else {}
            recovered = previous.get('failed', False)
            atomic_json(RUNTIME / 'last-result.json', result)
            atomic_json(state_path, {'failed': False})
            if recovered or result['created'] or result['deleted']:
                print(summary(result, recovered))
            return 0
        except Exception:
            try:
                atomic_json(RUNTIME / 'last-failure.json', diagnostic)
                if not preview:
                    atomic_json(RUNTIME / 'status.json', {'failed': True})
            except OSError:
                pass  # Reporting remains safe even if the local report cannot be written.
            print('[AESO] Sync failed; calendar may be stale. Authentication, source coverage, '
                  'Google permissions, or local setup require attention. '
                  'No failed source read is treated as an empty schedule. '
                  'A Google write failure may leave a partially applied plan; the next run reconciles it.',
                  file=sys.stderr)
            return 1


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--preview', action='store_true', help='Read-only live school/calendar preview.')
    args = parser.parse_args()
    sys.exit(run(args.preview))
