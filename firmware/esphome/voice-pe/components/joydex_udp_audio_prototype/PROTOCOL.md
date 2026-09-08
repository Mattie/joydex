# Joydex UDPPCM prototype protocol v1

This throwaway protocol answers one question: can the Voice PE carry several minutes of independent
microphone and speaker PCM without the sustained stalls seen in the current WSLANPCM stack? A pass
earns an attended product-integration trial. It does not by itself prove which WebSocket-era layer
caused the earlier failures.

The Voice PE binds one unencrypted IPv4 UDP socket on port `8766`. This is a trusted-LAN prototype.
The host creates a fresh nonzero 32-bit session ID for every open attempt and sends all multibyte
fields in network byte order.

## Datagram header

Every datagram begins with this 16-byte header:

| Offset | Bytes | Field |
|---:|---:|---|
| 0 | 4 | Magic `JDXU` (`0x4A445855`) |
| 4 | 1 | Protocol version `1` |
| 5 | 1 | Packet type |
| 6 | 2 | Payload byte count |
| 8 | 4 | Session ID |
| 12 | 4 | Per-direction sequence number |

Packet types are `OPEN=1`, `READY=2`, `CLOSE=3`, `CLOSED=4`, `PING=5`, `PONG=6`,
`PLAYBACK_END=7`, `SPEAKER=16`, and `MICROPHONE=17`.

`OPEN` pins the sender address and session ID, resets both bounded audio queues, and begins waiting
for the local wake acknowledgement to drain. The device returns `READY` only after the direct
48 kHz speaker path is running. Repeating the same `OPEN` is idempotent. A different `OPEN` replaces
the previous prototype session and discards its audio.

The host sends `PING` once per second throughout the session. The device
returns `PONG` and expires the session after 3.5 seconds without any valid host datagram. `CLOSE`
ends the session and returns a three-datagram `CLOSED` acknowledgement burst so a single lost final
packet does not make the host report a leaked session. The host treats repeated `CLOSED` packets as
idempotent. `PLAYBACK_END` carries the exclusive next-speaker sequence as its boundary. The device
applies an early/reordered boundary only after it receives or accounts for every preceding speaker
sequence. It holds an early boundary for 30 ms to admit ordinary LAN reordering, then counts any
still-missing trailing frames before applying the boundary. The boundary drives the non-barge-in
microphone gate. This sustained-stream prototype
intentionally has no speaker-flush command: interruption needs
an acknowledged control/data ordering boundary and remains an integration gate after the physical
transport matrix.

## PCM payloads

- `MICROPHONE`: exactly 640 bytes, representing 20 ms of 16 kHz mono signed PCM16 little-endian.
- `SPEAKER`: exactly 960 bytes, representing 10 ms of 48 kHz mono signed PCM16 little-endian.

Each audio direction has its own sequence starting at zero. Receivers discard duplicates and late
packets, count sequence gaps, and continue with the newest valid packet. Datagram loss never blocks
a later packet. The device uses a 400 ms oldest-drop queue per direction; audio is never persisted.

Direct 48 kHz speaker packets feed `joydex_voice_mixing_input`, bypassing the host 48→24 kHz and
device 24→48 kHz resampling pair used by WSLANPCM. The prototype therefore tests a candidate product
path rather than isolating one historical root cause.

`Joydex Audio Barge In` remains remotely configurable and persisted. Its fresh-install default is
off; it must be on for the attended duplex case so microphone packets continue during speaker
playback. Local cue playback always suppresses and flushes microphone audio regardless of that
switch, preventing the wake/ready acknowledgement from being sent back to Codex.
