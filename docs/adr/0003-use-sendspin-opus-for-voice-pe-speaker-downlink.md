# ADR 0003: Use Sendspin Opus for Voice PE speaker downlink

- Status: Accepted
- Date: 2026-08-28

## Context

Joydex needs simultaneous microphone uplink and speaker downlink on the Home
Assistant Voice Preview Edition. The original custom WebSocket bridge passed
short tests but failed sustained raw-PCM playback. The device had to allocate
large WebSocket payloads and eventually reported allocation failures. A
separate UDP/PCM prototype passed host loopback but failed its first physical
speaker test.

The retail firmware already included a Sendspin v1 player with clock
synchronization, timestamp buffering, Opus decoding, and a path into the stock
speaker mixer. Encoding each 20 ms, 48 kHz mono frame as Opus reduced the
physical-canary payloads to roughly one twentieth of raw PCM while preserving
that device-owned scheduling path.

Initial Opus playback exposed a second problem: tearing down and rebuilding the
device playback task for every assistant response made later responses
unreliable. Host telemetry showed complete model audio and no queue overflow
while the device skipped late chunks or became silent. Tests with a continuous
stream showed that stream continuity and sufficient scheduling lead were both
required.

## Decision

Use two deliberately separate LAN lanes:

- Port `8765` is the Joydex microphone and control connection. Firmware
  advertises `mode: "uplink_only"` and `downlink: null`, rejects binary
  downlink frames, and never owns the speaker lifecycle.
- Port `8927` is the plaintext Sendspin `player@v1` speaker connection.
- Joydex keeps 24 kHz mono PCM as its internal boundary, upsamples it to 48 kHz,
  and encodes one mono Opus packet at 32 kbit/s for each 20 ms Sendspin message.
- One Voice Session owns one Sendspin stream and one contiguous timestamp
  series. Logical response endings leave that stream open; Joydex sends clocked
  Opus silence between responses.
- Packets target a one-second scheduling lead. The host rebases before lead
  falls below 500 ms, above the player's 400 ms rejection threshold.
- A bounded 500-frame dispatcher paces output at 20 ms, serializes writes,
  inserts silence only after playback begins, and fails the session rather than
  dropping assistant audio when its bound is reached.
- Normal session close drains accepted audio through the final device boundary,
  sends `stream/end`, allows the player to settle, and then closes the socket.
  A scheduled-send or connection failure is terminal because reconnecting only
  the remaining suffix could silently lose already accepted speech.
- .NET WebSocket keepalives remain disabled for this endpoint. Sendspin's
  one-second clock exchange already supplies liveness traffic, and the pinned
  player rejected the .NET keepalive control frame.

The microphone sender preserves partial frame tails across callback batches,
allows one async frame in flight, compacts only complete stale frames after a
measured stall, and invalidates queued work across socket generations.

## Rejected approaches and useful failures

- Raw PCM over WebSocket and UDP was rejected after sustained or physical
  playback failures. The problem was below Codex and WebRTC, not missing model
  audio.
- A fixed post-teardown delay reduced one symptom but retained the unstable
  per-response speaker lifecycle.
- Moving the device's synchronized acknowledgement until after playback-task
  creation did not recover all six responses in a deterministic replay.
- Raising the player task above the WebSocket worker, giving media workers equal
  priority, correcting one Wi-Fi lease flag, and disabling Wi-Fi power saving
  each failed a sustained or repeated-session gate. These results moved the
  replacement boundary to the legacy player/synchronizer itself.
- Discarding normal callback remainders caused deterministic microphone loss:
  1,897 callbacks lost exactly `1,897 × (1,024 - 640) = 728,448` bytes. The
  accepted FIFO implementation preserves complete frames and partial tails.

## Consequences

- Speaker traffic on the LAN is compressed Opus; microphone traffic remains
  signed PCM16. LAN media is intentionally plaintext and requires a trusted
  network.
- The device owns jitter buffering, clock synchronization, decoding, and its
  final mixer path. Joydex owns bounded ingestion, response boundaries, and
  call-scoped pacing.
- Joydex depends on Concentus `2.2.2` for managed Opus encoding.
- The supported firmware contains no raw-PCM speaker route. Clear and ordinary
  playback-end operations have no side effect on the microphone/control lane.
- The modern replacement for the unreliable legacy player is specified in
  [ADR 0004](0004-stage-modern-sendspin-player-behind-the-legacy-media-source-abi.md).

## Evidence retained

- A host Opus round trip encoded one 20 ms frame to 112 bytes and decoded it to
  960 samples.
- A physical full-duplex control delivered 1,500 timestamped speaker packets
  while receiving 1,500 microphone frames over 30 seconds. A three-minute soak
  delivered 9,000 in each direction.
- A deterministic six-response test on the legacy player produced all six
  audible phrases only when it used one continuous stream and a one-second
  lead. Fresh per-response streams or a 500 ms lead lost later speech.
- Dispatcher tests cover burst pacing, timer oversleep, timestamp rebasing,
  inter-response silence, queue exhaustion, normal drain, missing-boundary
  synthesis, and fail-closed recovery.
