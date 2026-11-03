"""Temporary-file mutation and crash fixtures; no SSH, desktop or live profile access."""
import contextlib
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "src/Joydex.Windows/T3/t3-projector.py"
SPEC = importlib.util.spec_from_file_location("t3_shortcut_projector", SCRIPT)
PROJECTOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PROJECTOR)
LOCK_CONTEXT = PROJECTOR.shortcut_mutation_lock


class ShortcutMutationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.home = pathlib.Path(self.directory.name)
        self.root = self.home / ".t3/userdata"
        self.root.mkdir(parents=True)
        self.path = self.root / "keybindings.json"
        self.manifest = self.root / ".joydex-keybinding-additions.json"
        self.transaction = self.root / ".joydex-keybindings-transaction.json"
        (self.root / "environment-id").write_text("environment")
        if os.name == "nt":
            lock_patch = patch.object(PROJECTOR, "shortcut_mutation_lock", lambda _: contextlib.nullcontext())
            lock_patch.start()
            self.addCleanup(lock_patch.stop)

    def config(self):
        return {"issues": [], "keybindings": []}

    def setup(self, remove=False):
        return PROJECTOR.setup_shortcuts(self.root, remove, self.config())

    def test_cas_conflict_does_not_claim_matching_user_rule_or_destroy_prior_ownership(self):
        original_rule = {"key": "alt+x", "command": "chat.new"}
        self.path.write_text(json.dumps([original_rule]))
        prior_manifest = json.dumps({"rules": [original_rule]}).encode()
        self.manifest.write_bytes(prior_manifest)
        write = PROJECTOR.write_private
        added_by_user = []
        def race(path, raw):
            write(path, raw)
            if path.name.startswith(".joydex-keybindings-write-") and isinstance(json.loads(raw), list):
                new_rules = json.loads(raw)
                added_by_user.append(next(rule for rule in new_rules if rule["command"] == "thread.previous"))
                self.path.write_text(json.dumps([original_rule, added_by_user[0]]))
        with patch.object(PROJECTOR, "write_private", side_effect=race), self.assertRaises(PROJECTOR.SafeError) as error:
            self.setup()
        self.assertEqual(error.exception.kind, "BindingsChanged")
        self.assertEqual(self.manifest.read_bytes(), prior_manifest)
        self.assertFalse(self.transaction.exists())
        self.setup(True)
        self.assertEqual(json.loads(self.path.read_text()), added_by_user)

    def test_manifest_cas_conflict_preserves_external_manifest_and_original_profile(self):
        original = b'[{"key":"alt+x","command":"custom.command","unknown":{"private":true}}]'
        self.path.write_bytes(original)
        external = b'{"rules":[] ,"external":true}'
        write = PROJECTOR.write_private
        def race(path, raw):
            write(path, raw)
            if path.name.startswith(".joydex-keybindings-write-") and isinstance(json.loads(raw), list):
                self.manifest.write_bytes(external)
        with patch.object(PROJECTOR, "write_private", side_effect=race), self.assertRaises(PROJECTOR.SafeError):
            self.setup()
        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual(self.manifest.read_bytes(), external)
        self.assertFalse(self.transaction.exists())

    def test_crash_before_key_replace_leaves_old_manifest_and_can_recover_without_claims(self):
        original = b'[{"key":"alt+x","command":"custom.command"}]'
        self.path.write_bytes(original)
        replace = PROJECTOR.os.replace
        def crash(source, destination):
            if pathlib.Path(destination) == self.path:
                raise OSError("simulated crash before profile commit")
            return replace(source, destination)
        with patch.object(PROJECTOR.os, "replace", side_effect=crash), self.assertRaises(OSError):
            self.setup()
        self.assertEqual(self.path.read_bytes(), original)
        self.assertFalse(self.manifest.exists())
        self.assertTrue(self.transaction.exists())
        self.setup(True)
        self.assertEqual(self.path.read_bytes(), original)
        self.assertFalse(self.manifest.exists())
        self.assertFalse(self.transaction.exists())

    def test_crash_after_key_replace_recovers_owned_rules_then_undoes_only_additions(self):
        original = [{"key": "alt+x", "command": "custom.command", "extra": {"preserve": True}}]
        self.path.write_text(json.dumps(original))
        replace = PROJECTOR.os.replace
        def crash(source, destination):
            if pathlib.Path(destination) == self.manifest:
                raise OSError("simulated crash before manifest commit")
            return replace(source, destination)
        with patch.object(PROJECTOR.os, "replace", side_effect=crash), self.assertRaises(OSError):
            self.setup()
        self.assertFalse(self.manifest.exists())
        self.assertTrue(self.transaction.exists())
        self.assertEqual(len(json.loads(self.path.read_text())), 5)
        self.setup(True)
        self.assertEqual(json.loads(self.path.read_text()), original)
        self.assertEqual(json.loads(self.manifest.read_text()), {"rules": []})
        self.assertFalse(self.transaction.exists())

    def test_ambiguous_post_crash_external_edit_is_never_removed_or_claimed(self):
        self.path.write_text("[]")
        replace = PROJECTOR.os.replace
        def crash(source, destination):
            if pathlib.Path(destination) == self.manifest:
                raise OSError("simulated crash")
            return replace(source, destination)
        with patch.object(PROJECTOR.os, "replace", side_effect=crash), self.assertRaises(OSError):
            self.setup()
        user_rules = json.loads(self.path.read_text()) + [{"key": "alt+z", "command": "user.addition"}]
        user_bytes = json.dumps(user_rules).encode()
        self.path.write_bytes(user_bytes)
        with self.assertRaises(PROJECTOR.SafeError) as error:
            self.setup(True)
        self.assertEqual(error.exception.kind, "BindingsChanged")
        self.assertEqual(self.path.read_bytes(), user_bytes)
        self.assertFalse(self.manifest.exists())
        self.assertTrue(self.transaction.exists())

    def test_crash_after_manifest_commit_keeps_recoverable_transaction(self):
        self.path.write_text("[]")
        unlink = pathlib.Path.unlink
        def crash(path, *args, **kwargs):
            if path == self.transaction:
                raise OSError("simulated crash after manifest commit")
            return unlink(path, *args, **kwargs)
        with patch.object(pathlib.Path, "unlink", crash), self.assertRaises(OSError):
            self.setup()
        self.assertTrue(self.manifest.exists())
        self.assertTrue(self.transaction.exists())
        self.setup(True)
        self.assertEqual(json.loads(self.path.read_text()), [])
        self.assertFalse(self.transaction.exists())

    def test_edited_or_ambiguous_rule_releases_ownership_before_later_user_edits(self):
        self.setup()
        rules = json.loads(self.path.read_text())
        remembered = dict(rules[0])
        rules[0]["key"] = "alt+x"
        rules.append(dict(rules[1]))
        self.path.write_text(json.dumps(rules))
        self.setup(True)
        kept = json.loads(self.path.read_text())
        self.assertEqual(len(kept), 3)
        self.assertEqual(json.loads(self.manifest.read_text()), {"rules": []})
        self.path.write_text(json.dumps([remembered]))
        self.setup(True)
        self.assertEqual(json.loads(self.path.read_text()), [remembered])

    def test_noop_setup_prunes_missing_claims_without_rewriting_profile(self):
        self.setup()
        rules = json.loads(self.path.read_text())
        old = dict(rules[0])
        rules[0]["key"] = "alt+x"
        user_bytes = json.dumps(rules, separators=(",", ":")).encode()
        self.path.write_bytes(user_bytes)
        result = self.setup()
        self.assertEqual(result["addedCommands"], [])
        self.assertEqual(self.path.read_bytes(), user_bytes)
        self.assertNotIn(old, json.loads(self.manifest.read_text())["rules"])

    def test_unsupported_config_cannot_repair_or_change_prior_transaction(self):
        self.path.write_bytes(b"[]")
        self.manifest.write_bytes(b'{"rules":[]}')
        self.transaction.write_bytes(b"invalid pending transaction")
        result = PROJECTOR.setup_shortcuts(self.root, False, None)
        self.assertFalse(result["success"])
        self.assertEqual(self.path.read_bytes(), b"[]")
        self.assertEqual(self.manifest.read_bytes(), b'{"rules":[]}')
        self.assertEqual(self.transaction.read_bytes(), b"invalid pending transaction")
        self.assertEqual(list(self.root.glob(".joydex-keybindings-backup-*")), [])

    def test_environment_change_before_commit_prevents_profile_and_manifest_writes(self):
        self.path.write_text("[]")
        write = PROJECTOR.write_private
        def race(path, raw):
            write(path, raw)
            if path.name.startswith(".joydex-keybindings-write-") and isinstance(json.loads(raw), list):
                (self.root / "environment-id").write_text("another-environment")
        with patch.object(PROJECTOR, "read_effective_bindings", return_value=self.config()), \
                patch.object(PROJECTOR, "write_private", side_effect=race), self.assertRaises(PROJECTOR.SafeError) as error:
            PROJECTOR.main({"mode": "setup", "environmentId": "environment"}, self.home)
        self.assertEqual(error.exception.kind, "EnvironmentMismatch")
        self.assertEqual(self.path.read_text(), "[]")
        self.assertFalse(self.manifest.exists())
        self.assertFalse(self.transaction.exists())

    def test_main_holds_mutation_lock_during_capability_read(self):
        entered = []
        @contextlib.contextmanager
        def locked(_):
            self.assertEqual(entered, [])
            entered.append(True)
            try:
                yield
            finally:
                entered.pop()
        def config(*_):
            self.assertEqual(entered, [True])
            return self.config()
        with patch.object(PROJECTOR, "shortcut_mutation_lock", locked), patch.object(PROJECTOR, "read_effective_bindings", side_effect=config):
            self.assertTrue(PROJECTOR.main({"mode": "setup", "environmentId": "environment"}, self.home)["success"])

    def test_missing_linux_locking_support_fails_closed(self):
        with patch.dict(sys.modules, {"fcntl": None}), self.assertRaises(PROJECTOR.SafeError) as error:
            with LOCK_CONTEXT(self.root):
                self.fail("unsupported host acquired a mutation lock")
        self.assertEqual(error.exception.kind, "UnsupportedHost")

    @unittest.skipUnless(os.name == "posix", "Real flock is exercised on the Linux metadata host")
    def test_linux_flock_is_nonblocking_across_processes_and_keeps_stable_lock_inode(self):
        code = "import fcntl,os,sys\nf=os.open(sys.argv[1],os.O_RDWR)\ntry:\n fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB)\nexcept BlockingIOError:\n sys.exit(42)\n"
        lock_path = self.root / ".joydex-keybindings.lock"
        with LOCK_CONTEXT(self.root):
            held = subprocess.run([sys.executable, "-c", code, str(lock_path)], timeout=5, capture_output=True)
            self.assertEqual(held.returncode, 42)
        inode = lock_path.stat().st_ino
        with LOCK_CONTEXT(self.root):
            self.assertEqual(lock_path.stat().st_ino, inode)
        free = subprocess.run([sys.executable, "-c", code, str(lock_path)], timeout=5, capture_output=True)
        self.assertEqual(free.returncode, 0)


if __name__ == "__main__":
    unittest.main()
