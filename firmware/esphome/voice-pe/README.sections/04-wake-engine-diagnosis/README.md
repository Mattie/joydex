## Wake-engine diagnosis and single-model correction

The `DIAGOTA` image made the native API plaintext on the trusted LAN and exposed wake-engine and microphone state. Its exact OTA image was 3,050,752 bytes with SHA-256 `7DDC0184E9B384CAEAE6163D62C7F3DD0E5F435F423369F7212B70B3849E07E6`.

Live logs proved that the XMOS, codec, microphone, and microWakeWord task initialize. The microphone enters capture, then the engine reports `Failed to allocate tensors for the streaming model`, stops, and retries. Three model-allocation failures per attempt, about 121 KB free heap before inference, and an `exception/panic` reset identify simultaneous model allocation as the wake failure and the device-instability source.

Version `0.1.1` disables `Okay Nabu`, `Hey Jarvis`, `Hey Mycroft`, and `Stop` before its two-second delayed start. `Okay Computer` and the VAD remain active. It attempts wake-engine startup once during boot; the diagnostic image's unbounded ten-second retry was removed so a residual allocation failure leaves the diagnostic interfaces available instead of repeatedly driving panic resets. The generated C++ preserves that exact order, the full build log contains no fatal pattern, and the final image reports ESP32-S3, 16 MB DIO, valid checksum and validation hash, ESPHome `2025.12.2`, 44,768 bytes of static RAM, and 3,049,947 bytes of application flash.

| Single-model artifact | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,050,352 | `86BE660B39B2E9C19693E134324F9D271C2CFEDBE30832C24A245DBA3E2CBC2C` |
| `firmware.factory.bin` | 3,115,888 | `41F3E28ED906FE9E7456EFF0F8E288589B7C81642AB7D6099D2488A9D1AC76CA` |

The build workspace is `.tools\voice-pe-single-wake`. The known-working source and prepared `secrets.yaml` both hash to `<redacted-device-credential-file-hash>`; resolved Wi-Fi credential parity passed before compilation and whole-file parity passed afterward. The passworded recovery access point and USB recovery remain available.

`SINGLEWAKEOTA` authorized that exact `0.1.1` OTA image, and the transfer completed successfully on 2026-08-24. The unit rejoined at `192.0.2.10` with MAC `02:00:00:00:00:01`, and ESPHome reported project version `0.1.1` and an OTA-request reset. A live API client then started microWakeWord while logs were attached. The engine and microphone switched on, both remaining streaming models reported tensor-allocation failure, and the engine stopped without a panic. The endpoint stayed reachable and recoverable, but `Okay Computer` could not be tested because inference was no longer running.

The resolved dependency lock explained why reducing the model count was insufficient. ESPHome `2025.12.2` declares `esp-nn ^1.1.0`, which resolved to `1.3.0` in the 2026 build; its newer kernels need larger tensor arenas than the pinned 2025 model manifests declare. This matches upstream [ESPHome issue #15603](https://github.com/esphome/esphome/issues/15603) and the analysis in [ESPHome PR #15628](https://github.com/esphome/esphome/pull/15628). Version `0.1.2` pins `espressif/esp-nn==1.1.2`, reproducing the compatible 2025 dependency set without mixing a newer ESPHome component API into the pinned base.

The clean `0.1.2` build completed on 2026-08-24. Its lock file resolves exactly `esp-nn 1.1.2`; generated C++ disables the four unused models before a single delayed start and contains no retry path. Source and prepared Wi-Fi secrets remain byte-identical. Both ESP images report ESP32-S3, 16 MB DIO flash, and valid checksums and validation hashes.

| Pinned-`esp-nn` artifact | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,039,392 | `D0B55234D746D26DEAF220FAFA18E4EC43940DE447E33F4892610E6ADECEDF45` |
| `firmware.factory.bin` | 3,104,928 | `B3476C799011B3FB41310DD60EB5C1376F2D6DA500E08F6D1675A975DA2FCB76` |

`PINNEDNNOTA` authorized that exact `0.1.2` image, and the OTA completed successfully on 2026-08-24. The first upload command mistakenly named the prepared base YAML; ESPHome rejected it during configuration because the Joydex pulse script comes from the overlay, and it sent no firmware bytes. The successful command used `.tools\voice-pe-arena-probe\joydex-voice-pe-canary.yaml`, which resolves to the verified build above.

The unit rejoined at `192.0.2.10` with MAC `02:00:00:00:00:01` and reported project version `0.1.2`. HTTP, native OTA, and native API responded; wake-engine and microphone-capture diagnostics remained ON; microphone mute remained OFF. The live log contained no tensor-allocation, panic, exception, or watchdog failure. The configured VAD cutoff remains the manifest default `0.50`. A field report that this dependency repair may require lowering VAD to `0.10` is retained as the first tuning experiment if an armed engine still produces no wake detections; it was deliberately excluded from this one-change deployment.

Version `0.1.3` applied that VAD experiment and made the local wake engine, rather than a Home Assistant API client, the readiness source for the idle LED state. Its final USB recovery deployment used a 3,039,616-byte OTA image with SHA-256 `28BFEC758FF42FF55C3E611F77E1C403B4494EB80FEB36B263E624B886881717` and a 3,105,152-byte factory image with SHA-256 `1A75CFB441659C903DF4B34EEDE42137E94E20BF5D310FC4D1C7A7E071D12F23`. Serial and native-API diagnostics proved the engine and microphone stayed active without tensor failures. Room speech repeatedly toggled `Joydex VAD Active`, proving the `0.10` VAD path, while `Okay Computer` at its manifest `0.97` cutoff produced no wake.

Version `0.1.4` lowered only the `Okay Computer` cutoff to `0.85`. Its 3,039,616-byte OTA image has SHA-256 `D749FC425632345CA823C35387A8D1094500FB7FBC4A3AE7CCF7F22ED4DFB0D1`; its 3,105,152-byte factory image has SHA-256 `5D5DCC49E22E8672B6CE5902E6B54BAC917583E7ECDDD891FB03A27AE3C8B28B`. OTA, identity, engine, microphone, and VAD checks passed. Live logs continued to show room-speech VAD edges without an `Okay Computer` detection.

Version `0.1.5` uses `0.56`, matching the pinned retail firmware's existing **Very sensitive** precedent for `Okay Nabu`, while retaining VAD `0.10`. Generated C++ resolves the custom model cutoff to `142/255`, VAD to `25/255`, and disables the four unused wake models before one engine start. The dependency lock remains `esp-nn 1.1.2`; the build log has no fatal pattern; source, overlay, and known-working Wi-Fi credential parity all pass. The images report ESP32-S3, 16 MB DIO, valid checksums and validation hashes:

| Very-sensitive artifact | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,039,616 | `47B3B93C86E7A03FCC0E6709E203B1E5817B4A28DAD3365FEF568529136AC256` |
| `firmware.factory.bin` | 3,105,152 | `E871B8142B1F9CBFAFBB2F58C69AB036F6B9308589FC29A61B6C40A7BA122EF4` |

The `0.1.5` OTA completed on 2026-08-24. The endpoint rejoined with the accepted MAC and reported version `0.1.5`; wake engine and microphone capture were ON, mute was OFF, and Joydex re-established its outbound event stream. A natural close-range “Okay Computer” subsequently produced a wake edge and started native Codex Voice after several attempts. Couch-distance pickup remained unreliable.

Version `0.1.6` corrected the retained sensitivity control: its three options now set the experimental `Okay Computer` cutoff to `0.56`, `0.45`, or `0.35`, with **Very sensitive** (`0.35`) restored by default. The OTA image is 3,039,744 bytes with SHA-256 `F1E7DA88E6BD10BAC905C7BCB2AB43EA6D2293F1002DB313ED7B752AD93D3358`; the factory image is 3,105,280 bytes with SHA-256 `F99D4D40138DB3DB9C7FF40E8F3F99ED4110D5604AA6B4B5364A50814627F9EB`. The exact endpoint rejoined as `0.1.6`, and native diagnostics confirmed engine ON, microphone capture ON, mute OFF, and **Very sensitive** selected. Room speech reached VAD without a couch-distance wake edge during the captured trial.

Version `0.1.7` makes that menu a complete room profile. **Slightly**, **Moderately**, and **Very sensitive** pair the three cutoffs with wake-microphone gains `4`, `6`, and `8`; only the experimental `Okay Computer` averaging window changes from five frames to three. The build used the same pinned model, ESPHome, Voice PE, and `esp-nn 1.1.2` revisions. Pre- and post-compile Wi-Fi parity, generated-code invariants, fallback AP, artifact metadata, checksum, validation hash, exact-device identity, and vendor-sidecar recovery hashes all passed. The deployed artifacts are:

| Crisp-profile artifact | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,039,664 | `A01277F1920A55594D3397C3A76DD5350ACCD25F8D84B4BEC9B4744798E5E8A4` |
| `firmware.factory.bin` | 3,105,200 | `7004BC9CC30764B1268502B95686EA41362BE343A4DCD27236C553380664528B` |

The endpoint rejoined with accepted MAC `02:00:00:00:00:01` and reported `0.1.7`; engine and microphone capture are ON, mute is OFF, and **Very sensitive** is restored. A repeatable test played three synthesized “Okay Computer” phrases from the Windows default speaker; VAD was active but no wake edge appeared. A natural-voice couch retest and false-wake observation remain the acceptance gate. This control path performs wake inference locally; it does not yet carry room microphone or speaker audio to native Codex Voice.

`0.1.7` was rejected after near and far natural attempts stopped producing wake events. The preceding `0.1.6` log contained a successful `Okay Computer` detection with a `0.35` sliding average and `0.92` maximum, so the device was rolled back to the exact `0.1.6` OTA image above. Before that write, example/placeholder rejection, required Wi-Fi values, credential parity, fallback provisioning, artifact hash, and the gain-`4`/cutoff-`0.35`/window-`5` invariants all passed. After reboot, exact MAC and project-version verification passed; engine and microphone capture are ON, mute is OFF, **Very sensitive** is selected, and Joydex again holds an established connection.

The subsequent user field retest found restored `0.1.6` usable enough to proceed. Version `0.1.8` is therefore a control-surface change around those accepted defaults. It passed source pin/hash checks, `esphome config`, and a host-only compile on 2026-08-24. No `0.1.8` upload was attempted. Later diagnostic and sound images retain the same fixed inference values rather than incorporating the `0.1.8` remote setters.

| Remote-tuning candidate | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,049,152 | `95E24B8C5055F6EF3C9B907486F00154E2E14A42C0C9416C558456E6E290C2E8` |
| `firmware.factory.bin` | 3,114,688 | `35396DE16177DCEB6CDDC1980D42CEABFB25C99A6F40A4391AA3897287403F86` |

The `0.1.9` diagnostic passed pinned-source hashes, `esphome config`, credential parity, and a clean host compile on 2026-08-24. The tracked Voice PE source differs only in `home-assistant-voice.yaml`; the pinned ESPHome checkout differs only in `micro_wake_word.h` and `micro_wake_word.cpp`. The final build resolved `esp-nn 1.1.2`, used 44,912 bytes of RAM, and used 3,041,959 bytes of application flash. OTA then succeeded against the accepted endpoint at `192.0.2.10`. It rejoined as project `Joydex.Voice PE Score Diagnostic` version `0.1.9`, MAC `02:00:00:00:00:01`, with the wake engine and microphone ON and mute OFF. All score entities published, the reset button advanced generation `0` to `1`, and TCP 80, 3232, and 6053 remained reachable.

| Score-diagnostic candidate | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,042,368 | `1C0A9A6093BBD2807D1C372969D2B03021398E770D9C7F4D7EE633186B7D18BF` |
| `firmware.factory.bin` | 3,107,904 | `35CF3640ECBEEED118990742C0144761091668A9D848263E8CF8DE38329A09C9` |

Prepare this isolated diagnostic with `prepare-score-canary.ps1`; its default ignored workspace is `.tools\voice-pe-score-diagnostic-0.1.9`. On Windows, keep the documented Git `usr\bin` addition on `PATH` for the host compile because the pinned `micro-opus` component invokes `patch.exe` by name.

The first isolated natural-phrase sample on live `0.1.9` produced wake average `0.467`, wake maximum `0.820`, and VAD average/maximum `1.000`. The wake classifier therefore crossed the configured `0.35` cutoff. The reset generation had returned to `0` from its earlier nonzero value, which is consistent with a device reboot between snapshots; the cause is not established, and the user did not separately confirm whether this sample produced a wake edge.

The `0.1.10` sound canary passed pinned-source parity, `esphome config`, credential parity, generated-code inspection, and a clean host compile on 2026-08-24. The source MP3 is 17,900 bytes, 48 kHz stereo, approximately 0.552 seconds, with SHA-256 `FB316F3829BD224CE2E1EF5CFF31A11E452F080902E3D5F355A231EAA8D18369`. Generated C++ embeds exactly 17,900 bytes with that same hash and binds the wake acknowledgement as `AudioFileType::MP3`. The accepted gain `4`, wake cutoff `0.35`, five-frame window, VAD cutoff `0.10`, and `esp-nn 1.1.2` remain unchanged. The build uses 44,912 bytes of RAM and 3,043,223 bytes of application flash.

| Sound-canary candidate | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,043,632 | `A9BEB91D34B536A11CC66E251D46F3BB739D4502F4EE58D4BA387CBA416AE3B8` |
| `firmware.factory.bin` | 3,109,168 | `4B75003025D68E450C6083C900150CA022D9BB5ABE0961AD3AEAE10460E25BA1` |

Prepare this sound variant with `prepare-score-sound-canary.ps1`; its default ignored workspace is `.tools\voice-pe-score-sound-diagnostic-0.1.10`.

`SOUNDOTA` authorized a fresh post-parity build in `.tools\voice-pe-score-sound-diagnostic-0.1.10-soundota`. The explicit secrets source matched the previously deployed whole-file hash, differed from both in-scope example/template files, contained every required non-placeholder value, and remained byte-identical in the prepared workspace after compilation. The passworded recovery AP, captive portal, native OTA, exact `0.1.9` rollback image, and both official `25.12.4` recovery assets passed before the write. Generated C++ matched the reviewed candidate after normalizing workspace and `#line` source paths, retained all inference invariants, and embedded the exact MP3 payload. The final OTA image reports ESP32-S3, 16 MB DIO, a valid checksum, and a valid validation hash:

| Deployed sound canary | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,043,632 | `8F6B3B5434856D4EE8E16F1785CF40B54B5160D7552D5CA56F97B4E8230D9CE3` |
| `firmware.factory.bin` | 3,109,168 | `DC80C0120F432F3F19E786DA21B0DF2B799485F8242170A769E2A8ED060BE90C` |

OTA completed at 100%. The exact endpoint rejoined as `joydex-voice-pe`, MAC `02:00:00:00:00:01`, project `Joydex.Voice PE Score Diagnostic` version `0.1.10`; TCP 80/3232/6053 responded, every required diagnostic entity published, the wake engine and microphone were ON, and mute was OFF. Reset generation advanced to `1`. A 55-second observation window recorded no wake score or wake edge while VAD reached average `0.475` and maximum `0.580`; because the user did not confirm speaking during that window, audible WakeBlip playback was unverified at that point.

The subsequent attended wake trial did trigger Joydex but remained silent. `0.1.10` is therefore retained only as failure evidence and should not be redeployed as the sound profile.

`FIXSOUNDLEDOTA` authorized `0.1.11`. The source MP3 is 48 kHz stereo with a 0.552-second container duration. The converted FLAC is 48 kHz mono, 16-bit, 0.523333 seconds, 27,147 bytes, and SHA-256 `B18BF0B411954580C9E511E15F49B7FC8A9A76C559AC454E44DB343699C8DA0B`; the roughly 29 ms difference is removed MP3 encoder padding, not a sample-rate or playback-speed change. Generated C++ reconstructed to the exact FLAC bytes and bound them as `AudioFileType::FLAC`. Gain `4`, wake cutoff `0.35`, five-frame window, VAD cutoff `0.10`, and `esp-nn 1.1.2` remained unchanged.

The fresh short-path build passed exact credential parity, placeholder rejection, recovery-AP/captive-portal checks, `esphome config`, clean compilation, generated-audio parity, and final artifact hashing. Git for Windows `usr\bin` was added to the compile process `PATH` because the pinned `micro-opus` dependency invokes `patch.exe`. The resulting images were:

| Deployed sound-and-LED canary | Bytes | SHA-256 |
|---|---:|---|
| `firmware.bin` / `firmware.ota.bin` | 3,054,480 | `085A3DEDEA0524BCE13222F1FF3561BC45F353CDA933F5490503C8204BF47767` |
| `firmware.factory.bin` | 3,120,016 | `C3BF7CC662E6E6C83084FAD531D269E6409167F606C61BFCD7731C5D25930647` |

OTA reached 100% and reported success. The accepted endpoint rejoined with the same name and MAC and reported project version `0.1.11` on ESPHome `2025.12.2`. A native-API playback of `file://wake_word_triggered_sound` reported 48 kHz, mono, 16-bit stream information; its decoder, announcement speaker, and I2S pipelines ran to completion without an error, and the user heard the acknowledgement. Direct state checks rendered the stock `Waiting for Command` and `Listening For Command` effects, and `Armed` turned the ring off. The canonical Joydex package is wired to report `Listening` and `Armed` from confirmed, wake-latched Codex Voice lifecycle markers; unrelated Voice tasks cannot change this endpoint's ring. A natural wake followed by Spoken Hangup remains the end-to-end acceptance check for that host-driven transition.

During the preflight, an overly broad local YAML search mistakenly included the prepared `secrets.yaml` and emitted its values to tool output. No firmware write had occurred at that point, and the subsequent OTA used the verified known-working file. The values are not repeated here; rotate them if this task output is accessible beyond the trusted machine. Remaining inspection commands explicitly excluded secret files.

After a separately authorized diagnostic OTA, record a quiet-room baseline. Then, for each natural couch-distance attempt, reset the peaks, confirm the generation increment, say “Okay Computer” once, wait for the one-second sensors, and record all four values. Repeat at least five times before changing any tuning. A wake-average peak just below `0.35` points toward cutoff/gain margin; a VAD-average peak below `0.10` identifies VAD gating; healthy VAD with low wake scores points toward microphone placement or model mismatch. Per-attempt score profiles that vary sharply indicate runtime or audio-pipeline flakiness worth logging before another parameter change. The diagnostic performs a few relaxed atomic high-water updates during inference, so it is suitable for diagnosis and should not become the production profile without a latency and wake-rate check.
