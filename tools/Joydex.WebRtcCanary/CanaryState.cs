using System.Text.Json;

namespace Joydex.WebRtcCanary;

internal sealed class CanaryState
{
    private readonly object _gate = new();
    private CanarySnapshot _snapshot = new(
        Phase: "starting",
        Detail: "Starting the isolated Codex App Server.",
        CodexVersion: null,
        AuthMode: null,
        AttestationMode: null,
        AttestationRequested: false,
        FixtureAvailable: false,
        CaptureAvailable: false,
        FixturePlayed: false,
        RemoteTrackReceived: false,
        CaptureWritten: false,
        ThreadTitle: null,
        ThreadId: null,
        RealtimeStarted: false,
        SdpReceived: false,
        MediaConnected: false,
        AudioReceived: false,
        Closed: false,
        Error: null);

    public CanarySnapshot Snapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(Snapshot(), JsonOptions);

    public void Configure(string codexVersion, string authMode, string attestationMode)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                CodexVersion = codexVersion,
                AuthMode = authMode,
                AttestationMode = attestationMode,
            };
        }
    }

    public void ConfigureMediaFixture(bool fixtureAvailable, bool captureAvailable) => Update(
        phase: "media-fixture-configured",
        detail: $"Deterministic microphone fixture: {(fixtureAvailable ? "available" : "off")}; remote capture: {(captureAvailable ? "available" : "off")}.",
        fixtureAvailable: fixtureAvailable,
        captureAvailable: captureAvailable);

    public void AttestationRequested() => Update(
        phase: "attestation-requested",
        detail: "App Server requested a first-party attestation token; the canary returned an explicit unsupported response.",
        attestationRequested: true);

    public void HostReady() => Update(
        phase: "ready",
        detail: "App Server initialized. Start the browser media canary.");

    public void ResolvingThread(string title) => Update(
        phase: "resolving-thread",
        detail: $"Resolving Dedicated Voice Task: {title}",
        threadTitle: title);

    public void ThreadResolved(string title, string id) => Update(
        phase: "thread-resolved",
        detail: $"Resolved Dedicated Voice Task: {title}",
        threadTitle: title,
        threadId: id);

    public void ResumingThread() => Update(
        phase: "resuming-thread",
        detail: "Resuming the Dedicated Voice Task in the isolated App Server.");

    public void StartingRealtime()
    {
        const string phase = "starting-realtime";
        const string detail = "Sending the browser SDP offer to thread/realtime/start.";

        lock (_gate)
        {
            // A host can run more than one canary session. Reset only per-session evidence so a
            // second run cannot inherit green checks or an earlier capture failure.
            _snapshot = _snapshot with
            {
                Phase = phase,
                Detail = detail,
                FixturePlayed = false,
                RemoteTrackReceived = false,
                CaptureWritten = false,
                RealtimeStarted = false,
                SdpReceived = false,
                MediaConnected = false,
                AudioReceived = false,
                Closed = false,
                Error = null,
            };
        }

        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {phase}: {detail}");
    }

    public void RealtimeStarted() => Update(
        phase: "realtime-started",
        detail: "Codex accepted the Realtime session.",
        realtimeStarted: true);

    public void SdpReceived() => Update(
        phase: "sdp-received",
        detail: "Codex returned the WebRTC SDP answer.",
        sdpReceived: true);

    public void MediaConnected() => Update(
        phase: "media-connected",
        detail: "WebRTC media is connected. Speak, or wait for the audio canary phrase.",
        mediaConnected: true);

    public void FixturePlayed() => Update(
        phase: "fixture-played",
        detail: "Deterministic microphone fixture was sent through the WebRTC audio track.",
        fixturePlayed: true);

    public void RemoteTrackReceived() => Update(
        phase: "remote-track-received",
        detail: "Browser received the remote WebRTC audio track.",
        remoteTrackReceived: true);

    public void CaptureWritten() => Update(
        phase: "capture-written",
        detail: "Browser remote-audio capture was written to disk.",
        captureWritten: true);

    public void AudioReceived() => Update(
        phase: "audio-received",
        detail: "Remote model audio reached the Windows speaker path.",
        audioReceived: true);

    public void Closed(string? reason) => Update(
        phase: "closed",
        detail: string.IsNullOrWhiteSpace(reason)
            ? "Codex closed the Realtime session."
            : $"Codex closed the Realtime session: {reason}",
        closed: true);

    public void Failed(string message) => Update(
        phase: "error",
        detail: message,
        error: message);

    private void Update(
        string phase,
        string detail,
        bool? attestationRequested = null,
        bool? fixtureAvailable = null,
        bool? captureAvailable = null,
        bool? fixturePlayed = null,
        bool? remoteTrackReceived = null,
        bool? captureWritten = null,
        string? threadTitle = null,
        string? threadId = null,
        bool? realtimeStarted = null,
        bool? sdpReceived = null,
        bool? mediaConnected = null,
        bool? audioReceived = null,
        bool? closed = null,
        string? error = null)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Phase = phase,
                Detail = detail,
                AttestationRequested = attestationRequested ?? _snapshot.AttestationRequested,
                FixtureAvailable = fixtureAvailable ?? _snapshot.FixtureAvailable,
                CaptureAvailable = captureAvailable ?? _snapshot.CaptureAvailable,
                FixturePlayed = fixturePlayed ?? _snapshot.FixturePlayed,
                RemoteTrackReceived = remoteTrackReceived ?? _snapshot.RemoteTrackReceived,
                CaptureWritten = captureWritten ?? _snapshot.CaptureWritten,
                ThreadTitle = threadTitle ?? _snapshot.ThreadTitle,
                ThreadId = threadId ?? _snapshot.ThreadId,
                RealtimeStarted = realtimeStarted ?? _snapshot.RealtimeStarted,
                SdpReceived = sdpReceived ?? _snapshot.SdpReceived,
                MediaConnected = mediaConnected ?? _snapshot.MediaConnected,
                AudioReceived = audioReceived ?? _snapshot.AudioReceived,
                Closed = closed ?? _snapshot.Closed,
                Error = error ?? _snapshot.Error,
            };
        }

        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {phase}: {detail}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

internal sealed record CanarySnapshot(
    string Phase,
    string Detail,
    string? CodexVersion,
    string? AuthMode,
    string? AttestationMode,
    bool AttestationRequested,
    bool FixtureAvailable,
    bool CaptureAvailable,
    bool FixturePlayed,
    bool RemoteTrackReceived,
    bool CaptureWritten,
    string? ThreadTitle,
    string? ThreadId,
    bool RealtimeStarted,
    bool SdpReceived,
    bool MediaConnected,
    bool AudioReceived,
    bool Closed,
    string? Error);
