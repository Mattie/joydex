"""Check staged/committed public docs; never echo matched private values.

This narrow publication check complements manual review and secret scanning.
Only Git blobs are inspected, never arbitrary local evidence or credentials.
"""
from pathlib import Path, PurePosixPath
import argparse
import re
import subprocess
import sys

RULES = (
    ('absolute user or workstation path', re.compile(
        r'(?i)(?:\b[A-Z]:[\\/]+(?:Users|Dev|Temp|Apps)[\\/]|/(?:home|Users)/[^/\s]+/)')),
    ('live process identifier', re.compile(
        r'(?i)\b(?:PIDs?|process[ _-]IDs?|RuntimeHost|Guardian|VoiceWorker|PebbleWorker)\s*[:=#]?\s*\d{2,}\b')),
    ('literal request/session identifier', re.compile(
        r'(?i)\b(?:request|attempt|session|task)(?:[ _-]?id)?\b[^\n]{0,64}'
        r'\b(?:[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}|[0-9a-f]{32})\b')),
    ('private network address', re.compile(
        r'\b(?:10(?:\.\d{1,3}){3}|192\.168(?:\.\d{1,3}){2}|'
        r'172\.(?:1[6-9]|2\d|3[01])(?:\.\d{1,3}){2})\b')),
)
PRIVATE_ROOTS = {'.todo', '.mstatus', '.tools', 'artifacts'}


def git_bytes(root, *args):
    """Read a Git snapshot, failing closed if Git cannot supply it."""
    return subprocess.check_output(['git', '-C', str(root), *args], stderr=subprocess.DEVNULL)


def inspect(root, ref=None):
    """Yield value-free findings from the complete index or a committed tree."""
    paths = (git_bytes(root, 'ls-tree', '-r', '-z', '--name-only', ref) if ref
             else git_bytes(root, 'ls-files', '-z'))
    for raw in paths.split(b'\0'):
        if not raw:
            continue
        path = raw.decode('utf-8')
        parts = PurePosixPath(path).parts
        name = parts[-1].casefold()
        private_path = parts[0].casefold() in PRIVATE_ROOTS or name in {'agents.local.md', 'agents.override.md'}
        environment = (name == '.env' or name.startswith('.env.')) and name not in {
            '.env.example', '.env.sample', '.env.template'}
        if private_path or environment:
            yield path, 1, 'private local file'
            continue
        if not path.lower().endswith('.md'):
            continue
        content = git_bytes(root, 'show', f'{ref}:{path}' if ref else f':{path}').decode('utf-8-sig')
        for number, line in enumerate(content.splitlines(), 1):
            for description, pattern in RULES:
                if pattern.search(line):
                    yield path, number, description


def main():
    """Return nonzero on publication findings or an unreadable Git snapshot."""
    parser = argparse.ArgumentParser(description=__doc__)
    selection = parser.add_mutually_exclusive_group(required=True)
    selection.add_argument('--staged', action='store_true', help='Inspect the complete Git index')
    selection.add_argument('--ref', help='Inspect a committed tree, as CI does')
    args = parser.parse_args()
    try:
        root = Path(git_bytes(Path.cwd(), 'rev-parse', '--show-toplevel').decode().strip())
        findings = list(inspect(root, args.ref))
    except (subprocess.CalledProcessError, OSError, UnicodeError):
        print('Cannot inspect the requested Git snapshot; publication check failed.', file=sys.stderr)
        return 2
    for path, line, description in findings:
        print(f'{path}:{line}: {description}; replace with portable documentation or keep it local.')
    if findings:
        print('See docs/PUBLIC_DOCUMENTATION.md. Matched values are omitted.')
        return 1
    print('Public documentation privacy check passed.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
