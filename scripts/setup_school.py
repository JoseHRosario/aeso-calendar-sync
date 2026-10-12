#!/usr/bin/env python3
"""Run locally by the owner: masked credential entry, never in chat or argv."""
from getpass import getpass
import json
import os
from pathlib import Path
import sys

RUNTIME = Path('/home/xungaman/.hermes/profiles/meireles/aeso-calendar-sync')


def main() -> int:
    if not sys.stdin.isatty():
        print('Run this interactively in your own terminal. Do not pipe credentials.', file=sys.stderr)
        return 1
    username = input('Inovar login: ').strip()
    password = getpass('Inovar password (hidden): ')
    if not username or not password:
        print('Credentials were not saved.', file=sys.stderr)
        return 1
    RUNTIME.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(RUNTIME, 0o700)
    path = RUNTIME / 'school.env'
    temporary = RUNTIME / 'school.env.new'
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(descriptor, 'w') as stream:
            stream.write('AESO_SCHOOL_USERNAME=' + json.dumps(username) + '\n')
            stream.write('AESO_SCHOOL_PASSWORD=' + json.dumps(password) + '\n')
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)
    print(f'Saved owner-only credentials to {path}. The sync never prints their values.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
