# Voice PE 0.1.29 lease-fix regression (rejected)

This is the preserved attended sequence that rejected firmware `0.1.29`. The corrected Sendspin
close bookkeeping did not prevent the cross-session late-audio failure.

## Before upload

1. Use only the content-addressed OTA image named by the reviewed `0.1.29` manifest.
2. Verify the selected secrets file, passworded `Joydex Voice PE Recovery` AP, live web
   recovery endpoint, and exact deployed `0.1.28` rollback image.
3. Run `upload-staged-sendspin-lease-fix-canary.ps1` in `-PreflightOnly` mode. It must
   refuse any authorization other than the exact deployment keyword and must report that
   no upload was attempted. Its live checks must match the expected device name, MAC, project,
   current `0.1.28` version, web endpoint, microphone port `8765`, and Sendspin port `8927`.
4. Capture the device identity, safe-boot state, Armed/wake/microphone state, service ports,
   and cumulative audio counters.

## After upload

1. Confirm project `Joydex.Voice PE Audio Bridge`, version `0.1.29`, ESPHome `2025.12.2`,
   the expected device name and MAC, the passworded recovery page, native API, microphone
   port `8765`, and Sendspin port `8927`.
2. Wait for the safe-boot counter to clear. Require Armed, wake inference and microphone
   capture on, mute off, barge-in on, no active playback task, and zero boot-local audio
   failure counters.
3. Run the deterministic lease test: withhold one final `server/time` response only after a
   clean stream has drained, close it, then run the ordinary 45-second speaker-only stream.
   The second stream must add zero late or initial-late chunks and the device must stay
   responsive and Armed. `run-sendspin-lease-regression.ps1` performs this sequence, verifies
   it is running against `0.1.29`, and fails closed on counter or readiness changes. This is the
   direct regression for the fixed flag.
4. Run the complete progression in `TURN_THREE_REGRESSION.md` twice in consecutive fresh
   Voice Sessions without rebooting, restarting Joydex, or resetting counters between calls.

## Acceptance gate

- The deterministic lease test passes with zero new late chunks on its second stream.
- Both natural calls satisfy every gate in `TURN_THREE_REGRESSION.md`, including a complete
  long response, interruption followed by another complete response, Spoken Hangup, and rearm.
- Starting-to-ending deltas remain zero for late/initial-late Sendspin chunks, microphone
  dropped bytes, microphone send stalls/failures, forced closes, panics, resets, and watchdogs.
- Joydex uses the existing `call-scoped-sendspin-win-x64` package, keeps its Dedicated Voice
  Task and normal controller/task-alert responsibilities, and reports no host source-frame
  loss or overflow.

The direct lease test failed: the setup and following control added 1,261 late chunks in total,
with zero initial-late chunks, microphone drops, send stalls/failures, or forced closes. No natural
call was attempted. A web restart returned `0.1.29` to Armed with zero late chunks. Candidate
`0.1.30` and its current gate are documented in `WIFI_ALWAYS_ON_REGRESSION.md`.
