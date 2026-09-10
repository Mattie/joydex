# Joydex production Sendspin session canary

This host-only canary drives the exact `VoicePeSendspinSpeakerSession` used by
Joydex against `tools/Joydex.SendspinModernCanary`. It sends three two-second
responses on one call-scoped stream, with one second of clocked Opus silence
between responses. The responses use increasing tone amplitudes so the modern
fake speaker can require three content-bearing segments in the correct order.

Run the modern client first:

```text
joydex-sendspin-modern-canary --port 8927 --required-streams 1 --required-content-segments 3 --minimum-audible-frames-per-segment 90000 --require-rising-segment-energy 1
```

Then run this project with the repository-local .NET SDK. The device and the
running Joydex application are not involved.

```text
.\.tools\dotnet\dotnet.exe run --project .\tools\Joydex.SendspinProductionCanary\Joydex.SendspinProductionCanary.csproj --no-restore
```
