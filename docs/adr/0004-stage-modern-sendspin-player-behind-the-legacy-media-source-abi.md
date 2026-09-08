# ADR 0004: Stage the modern Sendspin player behind the legacy media-source ABI

- Status: Accepted
- Date: 2026-08-29

## Context

The Voice PE speaker path can sound clean at the start of a call, yet the pinned ESPHome Sendspin
player eventually skips most scheduled chunks. Firmware `0.1.27` through `0.1.30` isolated task
priority, microphone pressure, Wi-Fi lease bookkeeping, Wi-Fi power saving, and reconnect delay.
Those experiments improved diagnosis without producing a reliable long-call player. Host telemetry
continued to show complete assistant audio and a bounded Joydex queue while device late-chunk
counters grew. The remaining failure is inside the legacy player/synchronizer lifecycle.

Replacing the whole room-voice system would discard working behavior: Joydex already owns the
Codex session, microphone uplink, wake and ready cues, spoken hangup, transcript, Opus encoding,
clock exchange, and call-scoped stream lifecycle. The useful replacement boundary is only the
device-side Sendspin player.

`sendspin-cpp` `v0.7.2` is Apache-2.0 and retains the plaintext protocol-v1 dialect Joydex uses.
It accepts the same `client/hello`, `server/hello`, `client/time`, `server/time`, `stream/start`,
type-4 timestamped audio, and `stream/end` messages. Its current ESPHome adapter targets a newer,
single-pipeline `MediaSource` interface, while this Voice PE build uses the pinned pipeline-indexed
interface. A small ABI adapter is therefore required even though the wire protocol is compatible.

During the compatibility spike, production Joydex validation was also found to compare
`buffer_capacity` with the one-second timestamp lead. The former is compressed-buffer bytes; the
latter is microseconds. A modern player advertises 800,000 bytes, so the unit mismatch would reject
an otherwise compatible client.

## Decision

- Preserve Joydex's existing owner runtime, port `8765` microphone/control lane, port `8927`,
  48 kHz mono Opus format, 20 ms packet cadence, one-second timestamp lead, and one stream per Voice
  Session.
- Replace only the device-side legacy Sendspin player/synchronizer with `sendspin-cpp` `v0.7.2`,
  pinned at commit `30514d5102c269a0c7fa6a13932d6bf7f2ae1abc`.
- Keep the LAN transport plaintext. Later Sendspin encryption work changes the handshake and frame
  layout and is outside this compatibility contract.
- Bridge the modern `PlayerRoleListener` to pipeline `0` of the pinned ESPHome `MediaSource` ABI:
  decoded PCM writes go to the existing speaker pipeline, and actual speaker progress calls
  `PlayerRole::notify_audio_played` with the completion timestamp.
- Publish `SYNCHRONIZED` only after the speaker orchestrator has installed pipeline `0` and reset
  its pending-frame accounting. Use atomic readiness, active-source, and frame counters across the
  Sendspin worker, mixer task, speaker callback, and main loop. These are narrow backports over mixer
  commit `0fe230114c80b6d3378f04c777baadee178e40ad` and speaker-source commit
  `f5c1a8111df78e32d07d7a7bb800018808cdc0e7`, not an unbounded ESPHome upgrade.
- The host compatibility harness enables only the player role and advertises 48 kHz mono Opus and
  PCM. The firmware adapter retains controller support because the upstream adapter requires it and
  the retail configuration already uses the Sendspin group media player. Metadata, artwork,
  visualizer, and color roles are excluded from this candidate.
- Treat `buffer_capacity` as compressed bytes. Joydex requires the modern ESPHome media-source
  minimum as advertised on the wire: 20,000 bytes, or 80 percent of the 25,000-byte configuration
  minimum, independently of its timestamp lead.
- Keep each deployed fallback and its recovery artifacts unchanged until the next source-only
  adapter compiles and passes its applicable deterministic gates. Every device write remains a
  separate attended, content-addressed decision with verified station and recovery credentials.

## Compatibility proof

`tools/Joydex.SendspinModernCanary` builds the exact upstream library with a bounded realtime fake
speaker. `tools/Joydex.SendspinProductionCanary` then drives the exact production
`VoicePeSendspinSpeakerSession`, including its fresh-state wait, call-scoped stream, scheduled-tail
drain, stream end, and teardown delay. The pair negotiated protocol v1, `player@v1`, 48 kHz mono
16-bit Opus, an 800,000-byte advertised buffer, and clock synchronization. Joydex sent three
two-second responses in one stream with one second of clocked silence between responses: 400 input
packets total. The responses used increasing tone amplitudes to make ordering and non-silent
content observable.

The modern client reported one stream start, one stream end, 498 decoded writes, 429,372 accepted
and played PCM frames, zero write timeouts, zero format mismatches, zero unattributed audible
frames, three detected content segments, and exit code zero. Per-response audible-frame counts
were 92,772, 93,978, and 94,698 against a 90,000-frame minimum. Per-response absolute-sample energy
increased from 167,633,757 to 373,307,391 to 750,069,122 in the expected order.

This proves the production host wire/lifecycle contract and the modern host decoder/synchronizer
boundary through three later content-bearing responses in one call-scoped stream. A separate
three-stream lifecycle control also passed, but it is not the production transport shape. Neither
host result proves I2S behavior or long-call physical reliability.

## Firmware compile and resource proof

Candidate `0.1.31` implements the pipeline-0 adapter against the pinned Voice PE media and speaker
ABIs while resolving ESP-IDF `5.5.1` and registry component `sendspin-cpp` `0.7.2`. The managed
component records upstream commit `30514d5102c269a0c7fa6a13932d6bf7f2ae1abc` and component hash
`79310684d51ac5c6146765989548aacfab230e368fd5892c2d6124fe791425e8`. The generated configuration
enables only player and controller roles, retains port `8927`, 48 kHz mono Opus/PCM, a 1,000,000-byte
compressed buffer, 50 ms startup silence, and Wi-Fi power-save mode `none`.

The adapter reports starts, ends, write attempts, zero writes, accepted bytes, and played frames once
per ten active seconds so an attended canary can distinguish host silence from a blocked device sink
without per-packet logging.

Two guarded ESPHome `2025.12.2` compiles passed with ESP-IDF reproducible-build metadata enabled. The
final image uses 46,720 bytes of reported static RAM, 134,783 bytes of DIRAM, and 3,148,767 bytes of
application image. Against the rebuilt `0.1.30` baseline, that is an increase of 1,088 static-RAM
bytes and 39,100 image bytes. The final OTA image is 3,148,912 bytes with SHA-256
`DE0534931DF7E18E24F3A10255B8C7DBE0E9AA5DBB8AF00E945A94821FBDE627`; image inspection reports
ESP32-S3, 16 MB DIO flash, a valid checksum, and validation hash
`82FE6DAF6909EB0744B6421281700FDEBC70A014CF74A15C625F5D67D79AE1E4`.

The builder checks credential parity without printing values, downloads the exact pinned mixer and
speaker-source files, verifies their stock hashes, applies the reviewed patches, verifies the patched
and generated ABI hashes (including `audio.h` and `media_source.h`), and checks the complete normalized
dependency lock, full 19-file preparation/input closure, role exclusion, exact library version/commit,
image integrity, and resource ceilings. It freezes the payload under its OTA hash without providing
or invoking an upload path.

## Attended deployment boundary

The canary upload and rollback are separate fail-closed wrappers. The upload wrapper accepts only the
reviewed `0.1.31` manifest and exact deployed `0.1.30` recovery manifest. The rollback wrapper accepts
only that `0.1.30` recovery manifest and a live `0.1.31` target. Both validate the selected credential
source by hash. The normal path rebuilds twice in a fresh temporary workspace; the generated station
and recovery credentials must each pass parity on both compiles. An explicitly selected reviewed-
candidate path may reuse only the exact pinned, just-completed two-pass stage for the immediate
attended deployment. Since clean ESPHome linker layout is not byte-stable, both paths bind the write
to a content-addressed image whose complete semantic inputs and dependency lock are pinned.

Each wrapper requires one explicit execution mode. Offline validation cannot connect to the device;
live preflight cannot upload; upload additionally requires a declared physical USB or verified
fallback-AP recovery path and its own exact authorization keyword. `ADAPTEROTA` authorizes only the
`0.1.31` image, while `ROLLBACK130OTA` authorizes only the frozen `0.1.30` recovery image. On
2026-08-29, live `0.1.30` identity/service preflight passed and the exact staged `0.1.31` image was
uploaded over LAN. The device rejoined under the same name/MAC on `0.1.31`; HTTP, API port `8765`,
and Sendspin port `8927` passed postflight. Physical conversation acceptance later rejected the
candidate as documented below.

Physical acceptance follows `MODERN_SENDSPIN_REGRESSION.md`: verify rejoin and rollback readiness,
then pass two consecutive natural multi-response sessions, including sustained speech,
interruption-and-recovery, voice-only hangup, modern played-frame progress, clean microphone
counters, and no reboot. A failed canary preserves evidence before any separately authorized
rollback.

The 2026-08-29 physical canary rejected `0.1.31`. A natural call connected quickly and reached
multi-response playback before the endpoint red-ringed and rebooted. Two host-driven Sendspin
reproductions then caused the same `exception/panic` reset at the decoded-write/speaker-start
boundary; one explicitly verified that wake inference was stopped and microphone capture remained
active before playback. This rules out wake inference pressure and identifies the event-stream and
speaker-socket failures as reboot symptoms. Network logging cannot retain the panic backtrace across
the reset, so a USB-serial reproduction is the next evidence gate. Do not redeploy `0.1.31`
unchanged.

## Post-rejection diagnosis and corrected deployment

USB serial captured the exact `0.1.31` fault: `A stack overflow in task pthread has been detected`.
The `sendspin-cpp` `v0.7.2` thread helper supplies `MALLOC_CAP_SPIRAM` without the
`MALLOC_CAP_8BIT` capability required by ESP-IDF `5.5.1`, ignores the rejected
`esp_pthread_set_cfg()` result, and therefore leaves the 3,072-byte default pthread stack active.
Firmware `0.1.32` avoids that upstream failure by configuring only the modern player worker's
6,192-byte stack in internal RAM. Large media allocations remain in PSRAM, and the independent
Sendspin HTTP-server worker may retain its PSRAM stack. Two direct 40-second full-duplex controls
completed without a panic or reboot.

A subsequent natural call stayed booted and delivered complete user and assistant events, but the
first real speaker frame arrived while the modern stream was still synchronizing. Synchronization
took about 2.86 seconds against the recovery wrapper's three-second per-send deadline; cancellation
then ended the session with 99 accepted frames waiting in the host queue. Joydex now sends one silent
20 ms priming frame while opening the speaker lane and reports readiness only after that send returns.
The per-attempt deadline is eight seconds, and an internal deadline reports an explicit timeout.
A scheduled-send failure remains terminal because reconnecting only a suffix would lose audio.

Serial also distinguished an unrelated red ring from a media crash. ESPHome logged
`No clients; rebooting` exactly at its default 15-minute native-API timeout. This Dedicated Voice
Endpoint is Joydex-owned and intentionally has no Home Assistant API client, so firmware `0.1.33`
sets `api.reboot_timeout: 0s`; the Wi-Fi reboot timeout remains available for actual network loss.
The build log now names the independent `HTTP server task stack in PSRAM` setting so it is not
mistaken for the modern player worker setting.

The guarded two-pass `0.1.33` build passed credential parity, generated-code assertions for the
internal-RAM player worker and zero API-client timeout, dependency and ABI closure, image validation,
and resource ceilings. It produced a 3,149,344-byte OTA image with SHA-256
`17DA5649C5C0C16318EBD66A38D55ECD5387575B0D6B87B155B45230E4214061`. The exact image was written
over USB on 2026-08-29 and every block hash-verified. The endpoint rejoined, exposed
HTTP plus ports `6053`, `8765`, and `8927`, reported Armed with wake and microphone active, and was
reclaimed by Joydex. It remained Armed at 922 seconds with the USB reset reason unchanged, passing
the former 15-minute no-client reboot boundary.

The primed Joydex host then completed an approximately 321-second natural session with 24 assistant
playback boundaries, 8,173 assistant frames, 15,982 Sendspin frames, host queue high-water two, zero
host overflow, and maximum pacing lateness 11.8 ms. Spoken Hangup ended the session and the endpoint
rearmed without rebooting. This establishes the first long-call checkpoint for the modern adapter.

A later physically inaudible short reply still reached the host scheduler: 105 frames total, 30
non-silent frames, no overflow or significant arrival gap. Its onset coincided with restoration of
the 1,000 ms Sendspin timeline lead after a host delivery stall, while a longer following response
was audible. Playback onset and resynchronization for sub-second responses remain an isolated
acceptance gap; the working scheduler should be preserved until that boundary has direct telemetry.

## Consequences

- The work is an adapter, not a voice-stack restart. Wake, Codex ownership, full-duplex microphone
  uplink, chat UX, cues, and spoken hangup remain unchanged.
- The modern line has one narrow new boundary: decoded PCM into the existing pipeline-0 speaker
  sink, plus speaker progress back into the modern player. The adjacent readiness and
  progress-accounting backports exist only to make that cross-task boundary defined. The `0.1.32`
  worker-stack correction and `0.1.33` API-watchdog correction do not broaden that boundary.
- Existing 0.1.30 telemetry remains the baseline. A candidate must improve physical long-call
  playback without regressing microphone loss, wake responsiveness, hangup, rearm, recovery, or
  the rest of Joydex.
- An attended upload normally includes a fresh two-pass rebuild. The immediate reviewed-candidate
  path avoids repeating a just-completed two-pass build while retaining exact artifact, semantic
  input, dependency, credential, and image-integrity gates.
- The passing host canary and source-only firmware build prevent physical work from being blocked by
  a hidden protocol, ABI, dependency, or static-resource mismatch. They cannot substitute for an
  attended physical long-call acceptance run.
