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

HOME = Path('/home/xungaman/.hermes/profiles/meireles')
RUNTIME = HOME / 'aeso-calendar-sync'
APP = RUNTIME / 'app' / 'Aeso.CalendarSync.dll'


def atomic_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = path.with_suffix('.tmp')
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, 'w') as stream:
        json.dump(value, stream)
    os.replace(temporary, path)


def credentials(path: Path) -> dict[str, str]:
    if path.stat().st_mode & 0o077:
        raise ValueError('School credential file must not be group/world accessible.')
    values = {}
    for line in path.read_text().splitlines():
        if not line.strip() or line.startswith('#'):
            continue
        key, encoded = line.split('=', 1)
        if key not in {'AESO_SCHOOL_USERNAME', 'AESO_SCHOOL_PASSWORD'}:
            raise ValueError('Unexpected school credential setting.')
        value = json.loads(encoded)
        if not isinstance(value, str) or not value:
            raise ValueError('Missing school credential.')
        values[key] = value
    if set(values) != {'AESO_SCHOOL_USERNAME', 'AESO_SCHOOL_PASSWORD'}:
        raise ValueError('Missing school credential.')
    return values


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
        try:
            env.update(credentials(RUNTIME / 'school.env'))
            completed = subprocess.run(
                [str(Path.home() / '.dotnet' / 'dotnet'), str(APP), '--config',
                 str(RUNTIME / 'config.json'), '--dry-run' if preview else '--apply'],
                env=env, capture_output=True, text=True, timeout=180, check=False,
            )
            if completed.returncode:
                # Do not echo provider bodies, tokens, private keys or raw subprocess stderr.
                raise RuntimeError('Sync execution failed. Review sanitized local app diagnostics.')
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
            if not preview:
                atomic_json(RUNTIME / 'status.json', {'failed': True})
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
