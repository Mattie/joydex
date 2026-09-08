# Joydex Sendspin speaker canary

This source-only canary connects to the stock Sendspin player already exposed by
the accepted Voice PE `0.1.19` image. It exercises the device's supported
timestamped jitter buffer, clock synchronization, high-performance Wi-Fi lease,
decoder, media pipeline, and speaker path without changing firmware or Joydex.

The input file for `--pcm` is headerless little-endian 48 kHz mono signed PCM16.
The canary encodes it as one raw Opus packet per 20 ms Sendspin chunk by
default. The clip plays once, followed by silence, so sustained transport can
be checked with one short audible cue.

```powershell
dotnet run --project tools/Joydex.SendspinCanary -- self-test
dotnet run --project tools/Joydex.SendspinCanary -- device `
  --host 192.0.2.10 `
  --seconds 30 `
  --pcm D:\Temp\xylophone-ring-48k-mono-s16le.pcm
```

Use `--bitrate 32000` to select the Opus bitrate. `--codec pcm` remains
available for reproducing the device's internal-memory failure. PCM can use
`--chunk-ms` values that are multiples of 20 ms from 20 through 200 ms. Opus
accepts its legal 20, 40, and 60 ms frame durations so packet-frequency effects
can be compared by lost audio duration rather than raw packet count.

`--withhold-final-time` is a destructive-state diagnostic for the pinned
Sendspin close path. After all scheduled audio has drained, it deliberately
leaves exactly one `client/time` request unanswered before closing. Firmware
through `0.1.28` can then leave its high-performance Wi-Fi bookkeeping corrupt
until reboot. For `0.1.29` and later diagnostic candidates, use the guarded
`firmware/esphome/voice-pe/run-sendspin-lease-regression.ps1` wrapper so target
identity, the following control stream, counters, and rearm are checked together.
Use the raw option only in an attended canary followed by a device restart.

The WebSocket keepalive is deliberately disabled. This pinned ESPHome endpoint
rejects .NET's keepalive control frame at the configured interval, while
Sendspin's one-second clock exchange already proves the connection is live.

The current Voice PE uses Sendspin's legacy v1 JSON keys (`player_support`). The
canary accepts both legacy and current versioned hello keys while emitting only
the fields this installed player implements.
