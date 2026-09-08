### `0.1.19` hybrid microphone plus Sendspin Opus path

The restored `0.1.19` image already exposes ESPHome's stock legacy Sendspin v1 player at
`ws://<device>:8927/sendspin`. It advertises 48 kHz mono Opus, owns the playback clock filter and
jitter buffer, requests high-performance Wi-Fi while playing, and feeds the stock decoder/mixer.
This allows a replacement downlink without another firmware write: the custom port `8765` remains
the clear microphone/control lane, while Sendspin handles only speaker output.

The first Sendspin PCM canary reached the speaker. Sustained duplex then logged
`Failed to calloc memory for buffer` in `sendspin.hub`; a 500 ms raw-PCM lead is roughly 48 KB before
decoder and mixer allocations. Increasing PCM chunk duration did not remove that pressure. A
separate close at exactly 15 seconds was .NET's WebSocket keepalive frame hitting this legacy HTTPD
endpoint; disabling that redundant frame fixed the deterministic close because Sendspin already
exchanges clock messages every second.

The adopted source path encodes one 20 ms, 48 kHz mono Opus packet at 32 kbit/s for each timestamped
Sendspin message. Physical validation produced these results without changing firmware:

| Physical `0.1.19` hybrid run | Joydex speaker packets sent | Speaker payload | Concurrent microphone frames received | Result |
|---|---:|---:|---:|---|
| 12-second audible speaker smoke | 600 | 27,356 bytes | — | device sound heard; connection synchronized |
| 30-second duplex | 1,500 | 66,027 bytes | 1,500 | synchronized clean close |
| 180-second duplex soak | 9,000 | 388,206 bytes | 9,000 | synchronized clean close; no allocation error logged |
| Production clear/restart canary | 600 | 21,676 bytes | 624 during send window | clear/restart/end exercised |

The largest observed Opus payload in the independent soak was 98 bytes, versus 1,920 bytes for one
raw 20 ms PCM frame. Device logs contained no allocation failure or unexpected transport close.
The production canary also sent `stream/clear`, started a fresh decoder stream, reset encoder and
timeline state, continued playback, drained `stream/end`, and closed cleanly. Joydex source now uses
this hybrid transport, while the running Joydex process was deliberately left untouched. A planned
restart and natural wake-driven conversation remain the acoustic, interruption, Spoken Hangup,
rearm, and repeated-session gate.

The hybrid owner route requires **Joydex Audio Barge In** to remain on; the deployed device was
read back as on after the Sendspin integration. Port `8765` has no `playback_start` control in
`0.1.19`, so it cannot pause microphone forwarding around speaker audio that arrives independently
through Sendspin. Supporting the persisted off mode needs an explicit firmware protocol addition
and a separately authorized OTA. The shared Joydex host dispatcher still bounds decoded audio,
preserves lifecycle order, and preempts queued stale frames on clear without adding host pacing.

