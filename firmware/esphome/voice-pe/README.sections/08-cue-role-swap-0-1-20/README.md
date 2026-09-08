### `0.1.20` cue-role swap

Version `0.1.20` keeps the complete `0.1.19` audio bridge and detecting wake profile while swapping
the two lifecycle sounds. The xylophone `ReadyBlip.flac` now acknowledges wake detection, and
`WakeBlip.flac` plays locally when Joydex confirms Listening. Joydex no longer injects a second
copy of the connected cue through Sendspin, so the downlink begins with assistant audio.

| Deployed `0.1.20` artifact | Bytes | SHA-256 | Validation hash |
|---|---:|---|---|
| `firmware.bin` / `firmware.ota.bin` | 3,105,104 | `6329DAEDE109970D36B6370788BA4C1A11EFBD814B162E7C4A7C6F23BCEBE2ED` | `911552D748A6C1418825DB0E6CC3E3382C38146DFFEB2E19882AE910D8F9B88C` |

The final build passed accepted credential parity, source/prepared overlay parity, retained gain
`4`, wake cutoff `0.35`, five-frame window, VAD cutoff `0.10`, and image inspection for ESP32-S3,
16 MB DIO flash, checksum, and validation hash. HTTP web OTA returned `Update Successful!` on
2026-08-27. The endpoint returned with the accepted name and MAC as `Joydex.Voice PE Audio Bridge`
version `0.1.20`, Armed, wake inference and microphone capture running, microphone unmuted, barge-in
on, and Joydex's control stream reconnected. The verified `0.1.19` and OTA-rescue images remain the
rollback path.

