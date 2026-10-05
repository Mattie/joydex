# Isolate Pebble Index in a worker process

Status: accepted for PEBBLEPLUGIN implementation, 2026-09-12.

Run the existing transcript receiver in bundled `Joydex.PebbleWorker.exe`,
registered as `joydex.pebble-index`. Preserve the loopback HTTP parser, sender
authentication, duplicate identity, response codes, settings paths and exact
existing secret bytes. This extraction introduces no secret migration or new
delivery recovery UI.

RuntimeHost retains active settings, activation rollback and the shared Desktop
broker lease. It starts each exact child inside a kill-on-close Windows job and
authenticates a current-user pipe before allowing receiver startup. The worker
receives only its active preferences, paths and broker endpoint. Existing Pebble
settings remain the control surface; generic plugin Restart and Reload are not
enabled for this first extraction.

Validate enabled preferences before accessing the secret or inbox. Acquire
exclusive inbox ownership and bind the listener before creating a missing token.
Keep inbox ownership through completed request and delivery cleanup. The lease
coordinates participating receivers; an older binary does not honor this lease.
Updating from an older runtime still requires stopping that runtime first.

Startup succeeds only after the receiver is ready. A failed activation candidate
must fail settings Apply and preserve rollback behavior. Once committed, worker
failure stays local to Pebble. Confirm worker and job cleanup before replacement,
retain cleanup uncertainty as blocked, and use the existing capped restart backoff
and stability reset. Fence status updates by committed generation and publish
bounded delivery metadata without transcript or token content.

Worker recovery never replays inbox records. An accepted record keeps its original
task, host and transcript. Persist Delivery Uncertain before attempting a Desktop
send; an unconfirmed result remains uncertain. Previously Received records also
remain held for review because their delivery history may be ambiguous.

Reuse the narrow Windows job containment implementation established for Voice in
[ADR 0011](0011-isolate-voice-media-in-a-worker-process.md). These are trusted
same-user workers, not a hostile-code sandbox. Synthetic loopback and process
tests establish ownership and recovery behavior; actual phone-to-Desktop delivery
requires separate attended evidence.
