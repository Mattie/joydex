# Joydex Voice PE LAN PCM protocol v1

The Voice PE hosts one trusted-LAN WebSocket at:

```text
ws://<voice-pe-address>:8765/joydex/audio
```

Joydex connects outbound to that endpoint. The server permits one client and uses no TLS or application authentication.

## Handshake and lifecycle

After the WebSocket upgrade, a raw-PCM diagnostic configuration sends:

```json
{"type":"hello","protocol":1,"path":"/joydex/audio","uplink":{"encoding":"pcm_s16le","sampleRate":16000,"channels":1,"frameMs":20},"downlink":{"encoding":"pcm_s16le","sampleRate":24000,"channels":1,"frameMs":20}}
```

The production microphone/control configuration sends:

```json
{"type":"hello","protocol":1,"path":"/joydex/audio","mode":"uplink_only","uplink":{"encoding":"pcm_s16le","sampleRate":16000,"channels":1,"frameMs":20},"downlink":null}
```

The Joydex Sendspin hybrid transport requires the second form. It rejects a protocol-v1 device
that lacks `mode: "uplink_only"` or advertises any downlink on port `8765`; this prevents an older
firmware image from silently reacquiring the raw speaker lifecycle.

Joydex sends `{"type":"open"}` after Codex Realtime media is ready. The device waits for the local wake acknowledgement and its 300 ms playback tail to drain, flushes old PCM, opens microphone forwarding, and replies `{"type":"opened"}`.

In diagnostic duplex mode, Joydex sends `{"type":"playback_end"}` after the last downlink frame for
one assistant response, and `{"type":"flush"}` drops queued PCM without ending the session. In
uplink-only mode those messages are accepted for protocol compatibility and have no speaker
lifecycle effect. `{"type":"close"}` ends either mode, discards queued microphone audio, and replies
`{"type":"closed"}`. `ping`/`pong` are available for a lightweight liveness check.

For the WebRTC route, Joydex derives `playback_end` and `flush` from the Realtime data channel's
`output_audio_buffer.stopped` and `output_audio_buffer.cleared` events. It forwards downlink PCM only
between the matching `output_audio_buffer.started` and stopped/cleared boundary, so browser silence
frames do not keep a non-barge-in microphone paused.

A disconnect always closes the session and discards queued audio. Reconnecting never resumes an earlier session automatically.

## Binary audio

- Device to Joydex: little-endian signed 16-bit PCM, mono, 16 kHz. The device emits 640-byte/20 ms frames from the exact `wake_word_mic` source.
- Joydex to device, diagnostic duplex mode only: little-endian signed 16-bit PCM, mono, 24 kHz. A normal 20 ms frame is 960 bytes; the device accepts up to four frames in one WebSocket message.
- Uplink-only mode rejects every binary downlink frame, allocates no raw downlink queue, and owns no speaker.
- The uplink queue is bounded to 400 ms. Diagnostic duplex adds an independent 400 ms downlink queue. If a producer outruns its consumer, the oldest aligned PCM is discarded and a counter advances.
- In diagnostic duplex mode, ordinary speaker backpressure retains the current downlink frame and drains it only as the dedicated resampler accepts bytes; it is not counted as an overflow drop.
- Frames are not persisted.

`Joydex Audio Barge In` is persisted and defaults off. In diagnostic duplex mode, turning it off
pauses microphone forwarding during raw-PCM playback and for 250 ms after that path drains; turning
it on keeps the microphone open. Uplink-only mode receives no Sendspin playback-start signal and
therefore keeps microphone forwarding open regardless of this switch. Production keeps the switch
on as an explicit full-duplex operating policy and for rollback compatibility.
