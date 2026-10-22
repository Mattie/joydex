# Public documentation and private evidence

Publish product behavior, reusable procedures, source revisions, dependency
versions, validation scope and unresolved limitations. Keep personal and
machine-specific evidence in ignored local notes or outside the checkout.

Before staging documentation, commit messages or PR text:

- Replace operator names, account/device identities, workstation names, absolute
  home/workspace/scratch paths, private endpoints and personal conversation
  excerpts with role names, variables or clearly fictional examples.
- Summarize test outcomes instead of copying session transcripts, live process
  IDs, request/attempt/task identifiers, per-run timestamps or local backup and
  deployment inventories. Keep exact correlation in private evidence.
- Use environment variables such as `%LOCALAPPDATA%`, repository-relative paths
  or placeholders such as `$requestId`. A synthetic test fixture is acceptable
  when it cannot be mistaken for a real credential or person's account.
- Preserve required license/copyright attribution and public project references.
  Do not treat those credits as private operator notes.
- Never commit real secret values, configured environment files, credential
  stores, local agent overrides, `.todo`, `.mstatus`, `.tools` or build artifacts.

## Checks

Run `python -I scripts/check_public_docs.py --staged` to inspect the complete Git
index, including partially staged files. CI checks the committed tree with
`--ref HEAD`. Findings identify the file, line and category without copying the
matched value into logs.

Enable the repository's local pre-commit check with:

```console
git config --local core.hooksPath .githooks
```

If a checkout already uses hooks, review and integrate that setup rather than
overwriting it. The hook needs Python 3; `JOYDEX_DOCS_PYTHON` may specify an
executable. The existing repository-local Python environment is used when
available. New checkouts must opt in; Git does not install hooks on clone.

The guard rejects common workstation paths, private IPv4 addresses, live process/
request identifiers in tracked Markdown, and private local file locations. It does not identify all
personal information or replace secret scanning. Review the entire staged diff,
commit message and PR text manually before publishing. Local hooks can be
bypassed; CI supplies a separate check, not a guarantee of repository settings.

Ordinary cleanup changes current files. Previously published commits, diffs and
copies retain their earlier content; changing history requires a separate,
explicitly authorized recovery operation.
