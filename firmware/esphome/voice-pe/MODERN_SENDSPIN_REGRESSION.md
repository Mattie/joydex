# Voice PE 0.1.31 modern Sendspin attended canary

> Rejected on 2026-08-29. A natural call and two host-driven playback canaries caused an
> `exception/panic` reboot after playback began, including one run with wake inference confirmed
> stopped. Do not repeat or redeploy this image unchanged; capture one USB-serial exception
> backtrace before preparing a replacement.

Use this sequence to decide whether the modern device-side Sendspin player fixes long-call speaker
loss without regressing wake, microphone uplink, cues, spoken hangup, or Joydex ownership. The
candidate changes only the device Sendspin decoder/synchronizer boundary and its adjacent atomic
speaker accounting.

## Before upload

1. Keep Joydex running in its normal owner configuration. Confirm the device is Armed on exact
   version `0.1.30`, and preserve the current device and Joydex logs.
2. Keep the frozen `0.1.30` OTA and factory images available. Use normal LAN OTA while the device is
   healthy; connect USB only if the device cannot boot far enough to expose LAN or its recovery AP.
3. Run the canary wrapper with `-PreflightOnly`. It must freshly build twice, report exactly two
   `NETWORK CREDENTIAL PARITY: PASS` and two `RECOVERY CREDENTIAL PARITY: PASS` lines, identify the
   expected device name/MAC/project/current `0.1.30` version, pass the HTTP recovery endpoint and
   ports `8765`/`8927`, and finish without uploading.
4. Upload only after a fresh `ADAPTEROTA` authorization. The write command must use `-Upload` and
   declare either `FALLBACK_AP_VERIFIED` or `USB_CONNECTED`. By default it repeats the fresh two-pass
   build. `-UseReviewedCandidate` is allowed only for the exact pinned, just-completed two-pass stage
   used in the same attended deployment. Every path accepts only a content-addressed image that
   passes the reviewed input-closure, dependency-lock, credential, ABI, role, resource, geometry,
   checksum, and validation-hash gates.

The attended commands below are retained as operator-history examples. Their device-bound upload
and rollback wrappers are private and are intentionally unavailable in the public source checkpoint.
Do not reconstruct or run them without the reviewed private manifest, target identity, credential
hashes, and recovery evidence.

The historical commands from the repository root were:

```powershell
$canaryManifest = '.\.tools\voice-pe-modern-sendspin-0.1.31-closure-staged\DE0534931DF7E18E24F3A10255B8C7DBE0E9AA5DBB8AF00E945A94821FBDE627\manifest.json'
$secrets = '.\firmware\esphome\secrets.yaml'

.\firmware\esphome\voice-pe\upload-staged-modern-sendspin-canary.ps1 `
  -ManifestPath $canaryManifest `
  -SecretsPath $secrets `
  -PreflightOnly

.\firmware\esphome\voice-pe\upload-staged-modern-sendspin-canary.ps1 `
  -ManifestPath $canaryManifest `
  -SecretsPath $secrets `
  -Upload `
  -AuthorizationKeyword ADAPTEROTA `
  -RecoveryMethod $confirmedRecoveryMethod
```

`$confirmedRecoveryMethod` is intentionally undefined in this document. Use
`FALLBACK_AP_VERIFIED` only after the recovery network, password, captive portal, and direct upload
procedure have been verified; use `USB_CONNECTED` only when Windows shows the physical Voice PE
interface. Generated credential parity alone does not physically exercise the recovery AP.

## Rejoin and rollback readiness

1. Confirm the same device name and MAC now report project `Joydex.Voice PE Audio Bridge`, version
   `0.1.31`, ESPHome `2025.12.2`, and the expected modern Sendspin `0.7.2` startup details.
2. Wait for the safe-boot window to clear. Require Armed, wake inference and microphone capture on,
   mute off, barge-in on, no active Voice Session, and healthy HTTP, native API, `8765`, and `8927`
   endpoints. Confirm Joydex still owns the Dedicated Voice Task and its unrelated controller work
   remains responsive.
3. Keep the frozen `0.1.30` OTA and factory images ready. The older rollback build is not
   byte-reproducible across clean workspaces, so those exact frozen images are the recovery authority.
   Keep `ROLLBACK130OTA` unused unless the canary is rejected and a later user message explicitly
   authorizes rollback.

## Physical acceptance

1. Start one Voice Session from the normal room position using “Okay Computer.” Require the short
   wake cue immediately, the separate connected/listening cue and spinner only when both media lanes
   are ready, and a complete short assistant response.
2. Run the full progression in `TURN_THREE_REGRESSION.md`. Preserve Joydex speaker summaries and the
   device log from wake through rearm.
3. Repeat the full progression in a second fresh Voice Session without rebooting the endpoint,
   restarting Joydex, or clearing counters. This repeat is essential because the legacy player often
   sounded excellent for its first responses and degraded later.
4. In at least one run, interrupt a speaking response and then require a complete later response.
   End both sessions by voice and require a prompt return to Armed.

Accept only when both runs satisfy all of these gates:

- Every response is intelligible from beginning to end, including the sustained third response and
  the response after interruption.
- Wake, ready indication, full-duplex microphone recognition, and spoken hangup all remain usable.
- The endpoint never reboots, red-rings, disconnects, or remains stuck in its listening state.
- Joydex reports no source-frame loss, bounded-queue overflow, forced speaker close, or abnormal
  send pacing, and microphone drop/stall/failure counters do not increase.
- Modern device diagnostics show one stream start per Voice Session, a matching end after hangup,
  continuing writes/accepted bytes/played frames throughout later responses, and no sustained growth
  in zero writes. Played frames must keep advancing after each later assistant response.
- Device logs contain no speaker write timeout, playback queue exhaustion, I2S error, task watchdog,
  panic, or reset evidence.

## Failure and rollback

Preserve the host summary, modern playback diagnostics, microphone counters, device log, and the
last clearly audible response before restarting anything. A canary failure does not itself authorize
a rollback write. After a fresh `ROLLBACK130OTA` message, restore the frozen `0.1.30` OTA image over
the normal LAN or physically verified recovery AP while the web updater is reachable; use the frozen
factory image over USB only when the device cannot expose either network recovery path. Use
`-UseFrozenArtifact` for the network rollback so recovery does not depend on a clean rebuild being
byte-identical.
