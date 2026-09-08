### `0.1.19` socket telemetry and bounded recovery

Version `0.1.19` adds durable socket close/errno telemetry, send/receive/stall/forced-close counters, an audio-WebSocket connection counter, and active-socket ownership guards. ESP-IDF serializes HTTP open/close/frame callbacks; a short critical section now protects those callbacks from the separate microphone-uplink task so a late failure from a replaced socket cannot stop or feed the replacement session. Joydex wraps each owned-session device transport with a three-second send-attempt bound, three connection attempts per recovery cycle, and no more than two successful recovery cycles per logical Voice Session. Invalid PCM/programming errors do not reconnect, playback controls are retried after a socket failure, and persistent flapping fails closed and rearms.

The exact deployed OTA artifact passed source/prepared overlay parity, accepted credential-file parity without printing values, ESP32-S3/16 MB/DIO identity, and image checksum/validation-hash checks:

| Deployed `0.1.19` artifact | Bytes | SHA-256 | Validation hash |
|---|---:|---|---|
| `firmware.bin` / `firmware.ota.bin` | 3,070,336 | `138B273063189E82240991B275C11F5794FAA51F66A05855643DE77B2C4238E1` | `0FE59568ACECABFDD771A4AC04AFB22C593076BEC637231CAA607F8A24A15580` |

HTTP web OTA succeeded on 2026-08-26. The endpoint returned as `joydex-voice-pe`, MAC `02:00:00:00:00:01`, project `Joydex.Voice PE Audio Bridge` version `0.1.19`, ESPHome `2025.12.2`, Armed, wake inference and microphone running, barge-in on, and every new diagnostic entity present.

The healthy physical gate sent 200 frames per case. Both cases delivered 200 downlink frames and 192,000 accepted speaker bytes with zero drops, backpressure, partial writes, WebSocket failures, send stalls, or forced closes. Barge-in off carried zero concurrent microphone frames; barge-in on carried 228. The recovery gate deliberately replaced the socket at frame 100. The same logical transport reconnected on attempt 1, completed 200 downlink frames, accepted 191,040 speaker bytes, carried 198 concurrent microphone frames, and reported exactly three connections: original, injector, and replacement. It then restored Armed, barge-in on, wake inference, and microphone capture.

The final host suite passes 575 main and 29 wireless-panel tests. The recovery-enabled build is `artifacts\Joydex\voice-owner-recovery-win-x64`. After one warned restart it started its Guardian and private App Server, reacquired the dedicated owned task, restored four normal task-alert assignments, reconnected both VIRPIL controllers, and resumed direct LEDs. `Joydex Voice Chat - Owned` is suppressed by task ID and is absent from `task-alert-state.json`. An attended wake, intelligible reply, interruption, and spoken-hangup call remains the final acoustic/end-to-end gate.
