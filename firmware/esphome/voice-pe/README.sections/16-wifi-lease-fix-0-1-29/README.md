### Deployed and rejected `0.1.29` Sendspin Wi-Fi lease-fix canary

Candidate `0.1.29` keeps the exact `0.1.28` runtime component source except for one Sendspin
close-bookkeeping correction; its overlay also advances the project version to `0.1.29`. After
successfully releasing the clock-sync high-performance Wi-Fi request, its local request flag now
clears to `false`. All priorities, buffers, protocol behavior, wake tuning, cues, LEDs, and Joydex
audio behavior remain unchanged. The candidate therefore tests the confirmed cross-session poison
without mixing in another scheduling hypothesis.

The guarded builder performs two compiles, verifies the pinned source patches and generated
component hashes, proves both station and passworded recovery-AP credentials against the explicitly
selected non-template secrets file without printing them, verifies project identity and all task
priorities, and inspects the ESP32-S3 image geometry, checksum, and validation hash. The reviewed
upload candidate is frozen under its OTA content hash:

| Prepared `0.1.29` artifact | Bytes | SHA-256 | Validation hash |
|---|---:|---|---|
| `firmware.ota.bin` | 3,109,808 | `456B015932A4BF3A9D986A7C507099133E1BFD092E5B136B228DC072A822D90A` | `8490BB3913D65B2E34A7065CBC4BCB4BC993D5B74508E1E13E06131C7406ED53` |
| `firmware.factory.bin` | 3,175,344 | `C7E783155B9DF0E621776795780395645992E5824D9B6C13A5B77F8FD80A69C6` | — |

Its manifest is
`.tools\voice-pe-sendspin-lease-fix-0.1.29-staged\456B015932A4BF3A9D986A7C507099133E1BFD092E5B136B228DC072A822D90A\manifest.json`
with SHA-256 `A37A173C63155BD75B933D9BB12F6904DF7E3655C1C0153F3F0798877B154B73`.
`upload-staged-sendspin-lease-fix-canary.ps1` pins that manifest, both images, the lease-fixed
Sendspin source hash, selected credential source, ESP32-S3 image validation, live recovery endpoint,
exact device name/MAC/project/current version, microphone and Sendspin service ports, and exact
deployed `0.1.28` rollback. It refused an invalid keyword and missing recovery acknowledgement; its
complete live preflight-only path passed before `LEASEFIXOTA` authorized the exact upload.

Acceptance follows `LEASE_FIX_REGRESSION.md`: first reproduce the formerly poisoning clock-close
sequence and require the following 45-second stream to add zero late chunks, then pass two complete
natural `TURN_THREE_REGRESSION.md` calls consecutively without reboot. Passing the direct lease gate
will confirm this correction; it does not pre-accept the separate first-call scheduling behavior
that already failed on a fresh `0.1.28` boot.

The direct post-deployment regression rejected `0.1.29`. After a clean reboot, its 20-second
withheld-clock setup stream and following 45-second control started two playback tasks and added
1,261 late chunks in total, with zero initial-late chunks, microphone drops, send stalls/failures,
or forced closes. This closely reproduces the prior 1,275-chunk cross-session failure. The device
remained connected and Armed; an attended web restart cleared the poisoned state and returned every
boot-local counter to zero. The one-line flag correction is real but insufficient as a remedy, so
no natural-call acceptance was attempted on `0.1.29`.

