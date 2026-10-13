# Use version-pinned native dot calls for Room Voice

Voice PE wake can call a saved native dot directly because a Codex task does not carry a dot's
identity or conversation. The opt-in Native Dot route reuses our existing hidden WebRTC and
Sendspin media, bypasses Codex task ownership, and pins the tested Codex executable and code-mode
host hashes for cached-login access. Archive inspection is development-only; runtime never reads
`app.asar`.

Room Voice keeps an explicit voice-engine choice between Codex task voice and native dot voice.
Existing profiles retain their selected route; selecting either engine preserves the saved Codex
workspace/task and dot target so switching does not require provisioning them again.

A standard-library Python worker performs authenticated signaling over stdio because the tested
Windows .NET HTTP transport encountered a backend challenge while Python succeeded. Credentials
stay in that worker's memory, and the Voice worker's process job contains it and its auth child.
Each wake verifies the saved account, user, conversation and dot ID. Creation, attachment and stop
are attempted once, microphone input waits for connection and attachment, and local media closes
before the server allocation is stopped. Attachment begins after the answer SDP is applied, in
parallel with WebRTC connection, matching the inspected native client. After local media ends,
room-audio draining and closure may run alongside remote stop. Both must finish before rearming.
The Codex route retains its existing ordering. An uncertain allocation or stop faults the generation;
it must not silently admit another call. A durable allocation marker remains until stop is
acknowledged, so process death and automatic worker recovery cannot erase that uncertainty.
Server changes still require runtime response validation
despite the client version pin.
