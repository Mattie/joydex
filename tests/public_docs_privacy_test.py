"""Exercise publication checks through real Git indexes and the commit hook."""
from pathlib import Path
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

SOURCE = Path(__file__).resolve().parents[1]
CHECKER = SOURCE / 'scripts' / 'check_public_docs.py'


class PublicDocumentationPrivacyTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='joydex-public-docs-')
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.git('init', '-q')
        self.git('config', 'user.name', 'Test Contributor')
        self.git('config', 'user.email', 'contributor@example.invalid')
        self.git('config', 'commit.gpgsign', 'false')

    def git(self, *args, check=True, env=None):
        return subprocess.run(['git', *args], cwd=self.root, text=True,
                              capture_output=True, check=check, env=env)

    def stage(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')
        self.git('add', '-f', '--', name)

    def check(self, *args):
        return subprocess.run([sys.executable, '-I', str(CHECKER), *args],
                              cwd=self.root, text=True, capture_output=True)

    def test_blocks_private_metadata_without_printing_it(self):
        cases = [r'C:\Users\ExampleOperator\workspace', r'D:\Temp\local-run',
                 '/home/example-operator/run', 'App PID 98765',
                 'request 12345678-1234-4321-9876-123456789abc',
                 'Device endpoint: http://192.168.23.45/']
        for text in cases:
            with self.subTest(text=text):
                self.stage('docs/check.md', text)
                result = self.check('--staged')
                self.assertEqual(1, result.returncode, result.stderr)
                self.assertIn('docs/check.md:1:', result.stdout)
                self.assertNotIn(text, result.stdout + result.stderr)

    def test_portable_examples_and_credits_pass(self):
        self.stage('docs/check.md', '%USERPROFILE%/workspace\n$env:LOCALAPPDATA\n'
                   'request $requestId; process ID <pid>\n'
                   'SDK 8.0.423; validated release 2026-10-11\n'
                   'Copyright 2026 Example Contributor, MIT.\n'
                   'Example device: http://192.0.2.10/; loopback: 127.0.0.1\n')
        self.assertEqual(0, self.check('--staged').returncode)

    def test_unstaged_cleanup_cannot_hide_index_leak(self):
        self.stage('docs/check.md', r'C:\Users\ExampleOperator\workspace')
        (self.root / 'docs/check.md').write_text('Portable instructions.', encoding='utf-8')
        self.assertEqual(1, self.check('--staged').returncode)

    def test_unstaged_private_notes_are_not_published_content(self):
        self.stage('docs/check.md', 'Portable instructions.')
        (self.root / 'docs/check.md').write_text(r'C:\Users\ExampleOperator\workspace', encoding='utf-8')
        self.assertEqual(0, self.check('--staged').returncode)

    def test_private_control_files_are_rejected_even_without_bad_text(self):
        self.stage('.todo/tasks.md', 'Local notes.')
        self.assertEqual(1, self.check('--staged').returncode)

    def test_environment_template_name_cannot_exempt_private_root(self):
        self.stage('.mstatus/.env.example', 'EXAMPLE=synthetic')
        self.assertEqual(1, self.check('--staged').returncode)

    def test_private_file_rules_are_case_insensitive(self):
        for name in ['.TODO/tasks.md', 'ARTIFACTS/build.log', 'AGENTS.LOCAL.MD', '.ENV']:
            with self.subTest(name=name):
                self.stage(name, 'Local only.')
                result = self.check('--staged')
                self.assertEqual(1, result.returncode)
                self.assertIn(f'{name}:1: private local file', result.stdout)

    def test_commit_ref_does_not_use_later_worktree(self):
        self.stage('docs/check.md', 'Portable instructions.')
        self.git('-c', 'core.hooksPath=', 'commit', '-qm', 'Fixture')
        (self.root / 'docs/check.md').write_text(r'C:\Users\ExampleOperator\workspace', encoding='utf-8')
        self.assertEqual(0, self.check('--ref', 'HEAD').returncode)
        self.assertNotEqual(0, self.check('--ref', 'missing-ref').returncode)

    def test_real_commit_hook_blocks_then_accepts_clean_index(self):
        for name in ['scripts/check_public_docs.py', '.githooks/pre-commit']:
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(SOURCE / name, target)
            target.chmod(0o755)
        self.git('config', 'core.hooksPath', '.githooks')
        self.stage('docs/check.md', r'C:\Users\ExampleOperator\workspace')
        env = dict(os.environ, JOYDEX_DOCS_PYTHON=sys.executable)
        blocked = self.git('commit', '-qm', 'Fixture', check=False, env=env)
        self.assertNotEqual(0, blocked.returncode)
        self.stage('docs/check.md', 'Portable instructions.')
        self.git('commit', '-qm', 'Fixture', env=env)


if __name__ == '__main__':
    unittest.main()
