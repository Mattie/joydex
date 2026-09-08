# Joydex Voice PE UDPPCM prototype

This throwaway prototype asks whether independent, bounded UDP datagrams can sustain the Voice PE's
16 kHz microphone uplink and direct 48 kHz speaker downlink without one direction blocking the
other. The loopback matrix validates packet encoding, session identity, keepalives, sequence
tracking, close-acknowledgement handling, and 10/20 ms pacing. It cannot validate ESP32 Wi-Fi,
mixer backpressure, AEC, or the
physical speaker and microphone; those remain the attended device gate after an explicitly
authorized OTA.

Run the complete host matrix in one command from the repository root:

```powershell
.\scripts\run-udp-pcm-prototype.ps1
```

The default runs speaker-only, microphone-only, and duplex host loopbacks for 30 seconds each and
prints the full host/device state after every case. Increase the duration with `-Seconds 180` for a
longer host gate.

After prototype firmware is separately authorized and deployed, the same command can drive one
physical direction through the lifecycle-aware ESPHome harness:

```powershell
.\scripts\run-udp-pcm-prototype.ps1 -DeviceHost 192.0.2.10 -Direction speaker-only -Seconds 180
.\scripts\run-udp-pcm-prototype.ps1 -DeviceHost 192.0.2.10 -Direction microphone-only -Seconds 180
.\scripts\run-udp-pcm-prototype.ps1 -DeviceHost 192.0.2.10 -Direction duplex -Seconds 180
```

A shorter physical run remains useful as a smoke test. Its result is explicitly labeled `smoke`,
leaves `duration_gate_met` false, and cannot pass `adoption_matrix_case_passed` even when every
transport check succeeds. Each adoption-matrix direction requires at least 180 seconds.

The harness verifies the exact device name, MAC, project, and `0.1.21` version; publishes `Starting`;
waits for wake inference to pause while microphone capture remains active; sets barge-in off for the
speaker-only control and on for microphone/duplex; publishes `Listening` after UDP media is active;
then releases the host's start gate so the three-second media settle begins causally after that
state change. It checks packet, speaker, timeout, and close counters; rejects an uplink queue
high-water mark over 6,400 bytes, downlink over 19,200 bytes, main-loop gap over 100 ms, or uplink
send over 20 ms; and restores `Armed` with wake inference running. Session maxima reset at each
accepted `OPEN`, so a prior smoke run cannot contaminate the next qualification case.

Physical speaker payload uses only an 80 ms, low-amplitude marker every 30 seconds; the rest is
silence. The canary deliberately avoids a continuous test tone. A passing device-mode result still
requires the operator to hear both the ready cue and marker; counters and UDP acknowledgements cannot
prove that the physical transducer was audible.
