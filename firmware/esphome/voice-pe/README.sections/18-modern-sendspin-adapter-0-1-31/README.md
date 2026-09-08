### Deployed and rejected: `0.1.31` modern Sendspin adapter

Candidate `0.1.31` replaces only the pinned device-side Sendspin decoder and synchronizer with
`sendspin-cpp` `0.7.2`. The custom component keeps the deployed player-v1 wire contract and port
`8927`, adapts modern decoded PCM to pipeline `0` of the pinned `MediaSource`, and returns actual
speaker progress to the modern clock model. Wake, cues, LEDs, microphone/control port `8765`,
Joydex owner mode, spoken hangup, and speaker hardware remain on their existing paths. Player and
controller roles are compiled; metadata, color, artwork, and visualizer roles are excluded.

The adapter publishes `SYNCHRONIZED` only after pipeline `0` is installed and its pending-frame
counter is reset. Narrow patches over the exact retail mixer and speaker-source commits make active
source and frame accounting atomic across the Sendspin worker, mixer task, speaker callback, and
main loop. A ten-second active-playback summary reports stream starts/ends, writes, zero writes,
accepted bytes, and played frames for the later attended canary.

`prepare-modern-sendspin-canary.ps1` creates only a reproducible source tree. The guarded
`build-modern-sendspin-canary.ps1` performs two compiles and verifies ESPHome `2025.12.2`, station
and recovery credential parity without printing values, tracked/prepared/generated adapter hashes,
stock and patched pinned-component hashes, generated media/speaker ABI hashes, project identity,
Wi-Fi power-save mode `none`, exact Sendspin registry version/hash/upstream commit, role exclusions,
the complete normalized dependency lock, the full 19-file preparation/input closure, ESP-IDF
reproducible-build metadata, ESP32-S3 image integrity, and resource ceilings. It deliberately has no
upload step and stages the final files read-only by OTA hash.

| Built `0.1.31` artifact | Bytes | SHA-256 | Validation hash |
|---|---:|---|---|
| `firmware.ota.bin` | 3,148,912 | `DE0534931DF7E18E24F3A10255B8C7DBE0E9AA5DBB8AF00E945A94821FBDE627` | `82FE6DAF6909EB0744B6421281700FDEBC70A014CF74A15C625F5D67D79AE1E4` |
| `firmware.factory.bin` | 3,214,448 | `C67D29604E2E4A73815B60C60EE783F38A5187F478C377192F16089F7B708EF8` | — |

The final map reports a 3,148,767-byte application image, 46,720 bytes of static RAM, and 134,783
bytes of DIRAM. Relative to a same-toolchain rebuild of `0.1.30`, this adds 39,100 image bytes and
1,088 static-RAM bytes. The immutable manifest is
`.tools\voice-pe-modern-sendspin-0.1.31-closure-staged\DE0534931DF7E18E24F3A10255B8C7DBE0E9AA5DBB8AF00E945A94821FBDE627\manifest.json`
with SHA-256 `CC9F75DB35063E00A9E2CAEB1126F646B92B97293C306EB25B0AF2396C80E104`.

This proves compilation, dependency identity, pinned-ABI compatibility, credential parity, and
static resource fit. On 2026-08-29 the exact staged OTA image above was uploaded over LAN after the
live `0.1.30` identity/service preflight passed. The same name and MAC rejoined on `0.1.31`; HTTP,
native API port `8765`, and Sendspin port `8927` passed postflight.

Physical acceptance rejected the candidate. The first natural wake connected quickly, began a
multi-response Voice Session, then the endpoint red-ringed and rebooted. Joydex's event-stream idle
timeout and later Sendspin close were symptoms of that reboot, not its cause: the device reported
`exception/panic` and its uptime placed the restart at the media failure. A separate host-driven
Sendspin canary reproduced the panic immediately after the first decoded-write/speaker-start
boundary. A second canary explicitly selected `Listening`, verified wake inference stopped while
microphone capture remained active, and reproduced the same panic. The endpoint recovered to Armed
after each reboot, Joydex and Guardian remained running, and HTTP plus ports `8765` and `8927`
recovered. `0.1.31` must not be redeployed unchanged. That USB-serial gate later captured the exact
pthread stack overflow and is documented in the `0.1.32`–`0.1.33` follow-up section.

`upload-staged-modern-sendspin-canary.ps1` now pins the exact `0.1.31` manifest and both images, the
selected credential source, modern Sendspin version/commit/component hash and roles, patched mixer
and speaker-source identities, device name/MAC/project/current `0.1.30` version, recovery endpoint,
ports `8765` and `8927`, and both frozen `0.1.30` recovery images. By default it builds `0.1.31`
twice in a fresh temporary workspace and requires two exact station and recovery credential-parity
passes. `-UseReviewedCandidate` may reuse only the exact pinned, just-completed two-pass stage for an
immediate attended preflight/write. Clean workspace linker layout is not byte-stable, so acceptance
is based on the pinned semantic input closure, complete dependency lock, content-addressed stage,
resource ceilings, and image validation rather than equality between unrelated clean builds.
`rollback-staged-modern-sendspin-canary.ps1` accepts only a live `0.1.31` target. Its
`-UseFrozenArtifact` path directly revalidates and uses the exact known `0.1.30` OTA payload instead
of depending on a clean rebuild to reproduce its bytes. Candidate audio ports are diagnostic during
rollback, so a failure of the feature under test cannot block HTTP recovery.

Those upload, rollback, and regression wrappers are device-bound private operations tooling and are
not distributed in the public source checkpoint. This section records what the attended deployment
validated; it is not an executable public deployment recipe.

Both wrappers require one explicit mode. `-OfflineValidationOnly` checks immutable local inputs and
cannot contact the endpoint. `-PreflightOnly` performs the selected build/artifact and live
identity/service checks without uploading. `-Upload` additionally requires a declared recovery method and a fresh,
case-sensitive authorization keyword: `ADAPTEROTA` for `0.1.31`, or `ROLLBACK130OTA` to restore
`0.1.30`. The recovery-method value is an attended human attestation; the wrapper cannot detect a
USB cable or physically exercise fallback-AP access by itself. The deployed run used the existing
passworded recovery-AP/captive-portal configuration, whose generated credentials passed parity in
both compiles; USB remains the last-resort recovery path.

From the repository root, the offline checks are:

```powershell
.\firmware\esphome\voice-pe\upload-staged-modern-sendspin-canary.ps1 `
  -ManifestPath .\.tools\voice-pe-modern-sendspin-0.1.31-closure-staged\DE0534931DF7E18E24F3A10255B8C7DBE0E9AA5DBB8AF00E945A94821FBDE627\manifest.json `
  -SecretsPath .\firmware\esphome\secrets.yaml `
  -OfflineValidationOnly

.\firmware\esphome\voice-pe\rollback-staged-modern-sendspin-canary.ps1 `
  -ManifestPath .\.tools\voice-pe-wifi-always-on-0.1.30-staged\AEEF8552B5DEF5459A0868DA5A83F89D5AEE1D5287A3AC776C4FE5BCFE2D0685\manifest.json `
  -SecretsPath .\firmware\esphome\secrets.yaml `
  -OfflineValidationOnly
```

The attended physical sequence is defined in `MODERN_SENDSPIN_REGRESSION.md`. It starts with the
live preflight, keeps the separate rollback authorization unused unless needed, and requires the
existing two-call multi-response, interruption, spoken-hangup, and long-call gates before accepting
the adapter.
