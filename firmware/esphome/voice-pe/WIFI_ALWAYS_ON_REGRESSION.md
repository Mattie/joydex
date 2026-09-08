# Voice PE 0.1.30 Wi-Fi always-on regression (rejected)

Use this attended sequence before accepting firmware `0.1.30`. The candidate preserves the
rejected `0.1.29` audio stack and task priorities while setting ESPHome Wi-Fi power saving to
`none`. This isolates runtime Wi-Fi lease transitions from the speaker path.

`ALWAYSONOTA` deployed the frozen candidate. It passed identity, recovery, safe-boot, state,
service, zero-counter, Joydex-process, and exact rollback-preflight gates. The direct regression
then rejected it before natural-call testing.

## Before upload

1. Use only the content-addressed OTA image named by the reviewed `0.1.30` manifest.
2. Verify the selected secrets file, passworded `Joydex Voice PE Recovery` AP, live web recovery
   endpoint, and exact deployed `0.1.29` rollback image.
3. Run `upload-staged-wifi-always-on-canary.ps1` with `-PreflightOnly`. Its live checks must match
   the expected device name, MAC, project, current `0.1.29` version, web endpoint, microphone port
   `8765`, and Sendspin port `8927`. It must report that no upload was attempted.
4. Capture device identity, safe-boot state, Armed/wake/microphone state, service ports, and all
   cumulative audio counters.

## Direct post-upload gate

1. Confirm project `Joydex.Voice PE Audio Bridge`, version `0.1.30`, ESPHome `2025.12.2`, the
   expected device name and MAC, HTTP recovery, native API, microphone port `8765`, and Sendspin
   port `8927`.
2. Wait for the safe-boot counter to clear. Require Armed, wake inference and microphone capture
   on, mute off, barge-in on, no active playback task, and zero boot-local audio failure counters.
3. Run `rollback-staged-wifi-always-on-canary.ps1` with `-PreflightOnly` against the exact frozen
   `0.1.29` manifest. This must identify the live `0.1.30` endpoint and report that no upload was
   attempted. A later rollback write requires its own fresh exact `ROLLBACK129OTA` authorization.
4. Run:

   ```powershell
   .\run-sendspin-lease-regression.ps1 -ExpectedVersion 0.1.30
   ```

   The script measures the withheld-final-clock setup stream separately from the following normal
   45-second control. Each phase must start exactly one playback task and add zero late chunks,
   initial-late chunks, microphone drops, send stalls, send failures, and forced closes. The device
   must remain responsive and Armed.

## Natural-call gate

After the direct gate passes, run the complete progression in `TURN_THREE_REGRESSION.md` twice in
consecutive fresh Voice Sessions without rebooting, restarting Joydex, or resetting counters
between calls.

## Decision

- Accept `0.1.30` only if the direct gate and both natural calls pass every counter and UX check.
- If the direct gate fails, preserve the split setup/control deltas and restart the endpoint to clear
  poisoned runtime state. Restore the exact frozen `0.1.29` image only after fresh exact
  `ROLLBACK129OTA` authorization.
- If the direct gate passes and a natural call fails, preserve host and device evidence before
  rollback. That result would clear runtime Wi-Fi lease transitions while leaving a separate
  full-duplex or playback-lifecycle fault.

## Result

- Two-second gap: setup `LateChunks=0`; following control `LateChunks=565`.
- Ten-second gap after a clean restart: setup `LateChunks=104`; following control
  `LateChunks=191`.
- Both runs: one playback task per phase and zero initial-late chunks, microphone drops, send
  stalls/failures, and forced closes.
- Both recovery restarts returned `0.1.30` to Armed with zero boot-local counters.

This rejects `0.1.30`. The longer gap did not remove the failure, and loss appearing in the second
run's setup phase shows that the condition is variable rather than a fixed second-stream-only count.
These two failure records did not preserve host maximum send lateness/interval, so they do not alone
assign the remaining fault wholly to the device. The harness now emits those host metrics on future
success and failure paths.
