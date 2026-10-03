using Joydex.Core.Input;

namespace Joydex.Core.Runtime;

/// <summary>
/// Owns runtime input observation and bounded capture across all controller workers.
/// </summary>
public sealed class RuntimeInputHost : IDisposable
{
    public static readonly TimeSpan DefaultCaptureTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ExpirationPollInterval = TimeSpan.FromMilliseconds(250);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _expirationTimer;
    private readonly Dictionary<string, SourceEntry> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, CaptureState> _captures = [];
    private readonly Dictionary<string, Guid> _captureBySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<SuppressedControl> _suppressedUntilRelease = [];
    private long _nextGeneration;
    private long _nextSequence;
    private bool _disposed;

    public RuntimeInputHost(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _expirationTimer = _timeProvider.CreateTimer(
            static state => ((RuntimeInputHost)state!).ExpireCaptures(),
            this,
            ExpirationPollInterval,
            ExpirationPollInterval);
    }

    public event EventHandler<InputObservationEventArgs>? InputObserved;

    public event EventHandler<InputCaptureChangedEventArgs>? CaptureChanged;

    public IReadOnlyList<InputSourceState> Sources
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _sources.Values
                    .Select(ToSourceState)
                    .OrderBy(source => source.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(source => source.Descriptor.SourceId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Replaces the available-device catalog while retaining entries with a connected runtime owner.
    /// </summary>
    public void PublishAvailableSources(IEnumerable<InputSourceDescriptor> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var descriptors = sources.Select(ValidateDescriptor).ToArray();
        var updates = new List<InputCaptureChangedEventArgs>();
        lock (_gate)
        {
            ThrowIfDisposed();
            ExpireCapturesLocked(_timeProvider.GetUtcNow(), updates);
            var availableIds = descriptors
                .Select(source => source.SourceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var stale in _sources.Values
                         .Where(source => !source.Connected && !availableIds.Contains(source.Descriptor.SourceId))
                         .ToArray())
            {
                CompleteCaptureForSourceLocked(
                    stale.Descriptor.SourceId,
                    InputCaptureStatus.SourceDisconnected,
                    "The input source is no longer available.",
                    updates);
                _sources.Remove(stale.Descriptor.SourceId);
            }

            foreach (var descriptor in descriptors)
            {
                if (_sources.TryGetValue(descriptor.SourceId, out var existing))
                {
                    existing.Descriptor = descriptor;
                }
                else
                {
                    _sources.Add(descriptor.SourceId, new SourceEntry(descriptor));
                }
            }
        }

        PublishCaptureUpdates(updates);
    }

    /// <summary>Registers the current acquisition owner and returns its generation token.</summary>
    public InputSourceSession ConnectSource(
        InputSourceDescriptor source,
        Action prepareForCapture)
    {
        source = ValidateDescriptor(source);
        ArgumentNullException.ThrowIfNull(prepareForCapture);
        var updates = new List<InputCaptureChangedEventArgs>();
        InputSourceSession session;
        lock (_gate)
        {
            ThrowIfDisposed();
            ExpireCapturesLocked(_timeProvider.GetUtcNow(), updates);
            if (!_sources.TryGetValue(source.SourceId, out var entry))
            {
                entry = new SourceEntry(source);
                _sources.Add(source.SourceId, entry);
            }
            else if (entry.Connected)
            {
                CompleteCaptureForSourceLocked(
                    source.SourceId,
                    InputCaptureStatus.GenerationChanged,
                    "The input source owner changed generation.",
                    updates);
                RemoveSuppressionForSourceLocked(source.SourceId);
            }

            entry.Descriptor = source;
            entry.Generation = checked(++_nextGeneration);
            entry.Connected = true;
            entry.LastSnapshot = null;
            entry.PrepareForCapture = prepareForCapture;
            session = new InputSourceSession(source.SourceId, entry.Generation);
        }

        PublishCaptureUpdates(updates);
        return session;
    }

    /// <summary>Ends one exact source generation and every reservation tied to it.</summary>
    public void DisconnectSource(InputSourceSession source)
    {
        var updates = new List<InputCaptureChangedEventArgs>();
        lock (_gate)
        {
            if (_disposed
                || !_sources.TryGetValue(source.SourceId, out var entry)
                || !entry.Connected
                || entry.Generation != source.Generation)
            {
                return;
            }

            entry.Connected = false;
            entry.LastSnapshot = null;
            entry.PrepareForCapture = null;
            CompleteCaptureForSourceLocked(
                source.SourceId,
                InputCaptureStatus.SourceDisconnected,
                "The input source disconnected.",
                updates);
            RemoveSuppressionForSourceLocked(source.SourceId, source.Generation);
        }

        PublishCaptureUpdates(updates);
    }

    /// <summary>
    /// Publishes physical input and consumes only the edges allowed through capture. One source's
    /// dispatch gate stays held through the asynchronous consumer, so capture activation waits for
    /// every previously routed action to finish.
    /// </summary>
    public async Task<T> RouteAsync<T>(
        InputSourceSession source,
        JoystickSnapshot snapshot,
        IReadOnlyList<JoystickEvent> events,
        Func<RoutedInput, Task<T>> consume)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(consume);

        SourceEntry entry;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_sources.TryGetValue(source.SourceId, out entry!)
                || !entry.Connected
                || entry.Generation != source.Generation)
            {
                throw new InvalidOperationException(
                    $"Input source '{source.SourceId}' generation {source.Generation} is not current.");
            }

        }

        await entry.DispatchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var captureUpdates = PreparePendingCapture(source, entry, snapshot);
            PublishCaptureUpdates(captureUpdates);

            InputObservation observation;
            RoutedInput routedInput;
            captureUpdates = [];
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!entry.Connected || entry.Generation != source.Generation)
                {
                    throw new InvalidOperationException(
                        $"Input source '{source.SourceId}' generation {source.Generation} is not current.");
                }

                ExpireCapturesLocked(_timeProvider.GetUtcNow(), captureUpdates);
                entry.LastSnapshot = CloneSnapshot(snapshot);
                var routedEvents = RouteEventsLocked(source, events, captureUpdates);
                routedInput = new RoutedInput(
                    CreateRoutedSnapshotLocked(source, snapshot),
                    routedEvents);
                observation = new InputObservation(
                    checked(++_nextSequence),
                    ToSourceState(entry),
                    CloneSnapshot(snapshot),
                    events.ToArray());
            }

            PublishObservation(new InputObservationEventArgs(observation));
            PublishCaptureUpdates(captureUpdates);
            return await consume(routedInput).ConfigureAwait(false);
        }
        finally
        {
            entry.DispatchGate.Release();
        }
    }

    public InputCaptureStartResult BeginCapture(InputCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ConnectionId))
        {
            return InputCaptureStartResult.Rejected("A capture connection ID is required.");
        }
        if (string.IsNullOrWhiteSpace(request.SourceId))
        {
            return InputCaptureStartResult.Rejected("A capture source ID is required.");
        }
        if (string.IsNullOrWhiteSpace(request.Purpose))
        {
            return InputCaptureStartResult.Rejected("A capture purpose is required.");
        }

        var timeout = request.Timeout ?? DefaultCaptureTimeout;
        if (timeout <= TimeSpan.Zero || timeout > DefaultCaptureTimeout)
        {
            return InputCaptureStartResult.Rejected(
                $"Capture timeout must be greater than zero and no longer than {DefaultCaptureTimeout.TotalSeconds:0} seconds.");
        }

        var updates = new List<InputCaptureChangedEventArgs>();
        InputCaptureLease? lease = null;
        string? error = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            var now = _timeProvider.GetUtcNow();
            ExpireCapturesLocked(now, updates);
            if (!_sources.TryGetValue(request.SourceId, out var source))
            {
                error = $"Input source '{request.SourceId}' is not available.";
            }
            else if (_captureBySource.ContainsKey(request.SourceId))
            {
                error = $"Input source '{request.SourceId}' is already reserved for capture.";
            }
            else if (request.ExpectedGeneration is { } expected
                && (!source.Connected || source.Generation != expected))
            {
                error = $"Input source '{request.SourceId}' changed generation.";
            }
            else
            {
                var capture = new CaptureState(
                    Guid.NewGuid(),
                    request.ConnectionId.Trim(),
                    source.Descriptor.SourceId,
                    request.Purpose.Trim(),
                    now + timeout);
                _captures.Add(capture.CaptureId, capture);
                _captureBySource.Add(capture.SourceId, capture.CaptureId);

                    lease = AdvanceLease(capture);
                    updates.Add(new InputCaptureChangedEventArgs(lease));
            }
        }

        PublishCaptureUpdates(updates);
        return error is null
            ? InputCaptureStartResult.Started(lease!)
            : InputCaptureStartResult.Rejected(error);
    }

    public bool RenewCapture(Guid captureId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        var updates = new List<InputCaptureChangedEventArgs>();
        var renewed = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            var now = _timeProvider.GetUtcNow();
            ExpireCapturesLocked(now, updates);
            if (!_captures.TryGetValue(captureId, out var capture)
                || !string.Equals(capture.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                renewed = false;
            }
            else
            {
                capture.ExpiresAt = now + DefaultCaptureTimeout;
                updates.Add(new InputCaptureChangedEventArgs(AdvanceLease(capture)));
                renewed = true;
            }
        }

        PublishCaptureUpdates(updates);
        return renewed;
    }

    public bool CancelCapture(Guid captureId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        InputCaptureChangedEventArgs? update;
        lock (_gate)
        {
            if (_disposed
                || !_captures.TryGetValue(captureId, out var capture)
                || !string.Equals(capture.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                return false;
            }

            update = CompleteCaptureLocked(
                capture,
                InputCaptureStatus.Cancelled,
                "Capture was cancelled.");
        }

        PublishCaptureUpdate(update);
        return true;
    }

    public void DisconnectClient(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        var updates = new List<InputCaptureChangedEventArgs>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var capture in _captures.Values
                         .Where(capture => string.Equals(
                             capture.ConnectionId,
                             connectionId,
                             StringComparison.Ordinal))
                         .ToArray())
            {
                updates.Add(CompleteCaptureLocked(
                    capture,
                    InputCaptureStatus.ClientDisconnected,
                    "The capture client disconnected."));
            }
        }

        PublishCaptureUpdates(updates);
    }

    public void Dispose()
    {
        List<InputCaptureChangedEventArgs> updates = [];
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var capture in _captures.Values.ToArray())
            {
                updates.Add(CompleteCaptureLocked(
                    capture,
                    InputCaptureStatus.Cancelled,
                    "The input runtime stopped."));
            }
            _sources.Clear();
            _suppressedUntilRelease.Clear();
        }

        _expirationTimer.Dispose();
        PublishCaptureUpdates(updates);
    }

    internal void ExpireCaptures(DateTimeOffset now)
    {
        var updates = new List<InputCaptureChangedEventArgs>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            ExpireCapturesLocked(now, updates);
        }
        PublishCaptureUpdates(updates);
    }

    private void ExpireCaptures() => ExpireCaptures(_timeProvider.GetUtcNow());

    private IReadOnlyList<JoystickEvent> RouteEventsLocked(
        InputSourceSession source,
        IReadOnlyList<JoystickEvent> events,
        List<InputCaptureChangedEventArgs> updates)
    {
        _captureBySource.TryGetValue(source.SourceId, out var captureId);
        _captures.TryGetValue(captureId, out var capture);
        var routed = new List<JoystickEvent>(events.Count);
        foreach (var input in events)
        {
            if (input.Kind is not (JoystickEventKind.ButtonPressed or JoystickEventKind.ButtonReleased))
            {
                routed.Add(input);
                continue;
            }

            var suppressed = new SuppressedControl(source.SourceId, source.Generation, input.ControlIndex);
            if (_suppressedUntilRelease.Contains(suppressed))
            {
                if (input.Kind == JoystickEventKind.ButtonReleased)
                {
                    _suppressedUntilRelease.Remove(suppressed);
                }
                continue;
            }

            if (capture is null || capture.Status != InputCaptureStatus.Active)
            {
                routed.Add(input);
                continue;
            }

            if (capture.IgnoredHeldControls.Contains(input.ControlIndex))
            {
                if (input.Kind == JoystickEventKind.ButtonReleased)
                {
                    capture.IgnoredHeldControls.Remove(input.ControlIndex);
                }
                continue;
            }

            // Capture reserves all button edges on the selected source. The first eligible press
            // completes the lease, and a tombstone suppresses its matching release.
            if (input.Kind == JoystickEventKind.ButtonPressed)
            {
                _suppressedUntilRelease.Add(suppressed);
                updates.Add(CompleteCaptureLocked(
                    capture,
                    InputCaptureStatus.Completed,
                    "A controller button was captured.",
                    input));
                capture = null;
            }
        }

        return routed;
    }

    private JoystickSnapshot CreateRoutedSnapshotLocked(
        InputSourceSession source,
        JoystickSnapshot snapshot)
    {
        var routed = CloneSnapshot(snapshot);
        foreach (var suppression in _suppressedUntilRelease.Where(suppression =>
                     suppression.Generation == source.Generation
                     && string.Equals(suppression.SourceId, source.SourceId, StringComparison.OrdinalIgnoreCase)))
        {
            if (suppression.ControlIndex >= 0 && suppression.ControlIndex < routed.Buttons.Length)
            {
                routed.Buttons[suppression.ControlIndex] = false;
            }
        }

        if (_captureBySource.TryGetValue(source.SourceId, out var captureId)
            && _captures.TryGetValue(captureId, out var capture)
            && capture.Status == InputCaptureStatus.Active
            && capture.SourceGeneration == source.Generation)
        {
            foreach (var controlIndex in capture.IgnoredHeldControls)
            {
                if (controlIndex >= 0 && controlIndex < routed.Buttons.Length)
                {
                    routed.Buttons[controlIndex] = false;
                }
            }
        }
        return routed;
    }

    private List<InputCaptureChangedEventArgs> PreparePendingCapture(
        InputSourceSession session,
        SourceEntry source,
        JoystickSnapshot currentSnapshot)
    {
        CaptureState? capture;
        JoystickSnapshot? baseline;
        var updates = new List<InputCaptureChangedEventArgs>();
        lock (_gate)
        {
            if (_disposed
                || !source.Connected
                || source.Generation != session.Generation
                || !_captureBySource.TryGetValue(source.Descriptor.SourceId, out var captureId)
                || !_captures.TryGetValue(captureId, out capture)
                || capture.Status != InputCaptureStatus.Pending
                || capture.Preparing)
            {
                return updates;
            }

            var now = _timeProvider.GetUtcNow();
            if (capture.ExpiresAt <= now)
            {
                updates.Add(CompleteCaptureLocked(
                    capture,
                    InputCaptureStatus.TimedOut,
                    "Capture timed out."));
                return updates;
            }

            capture.Preparing = true;
            capture.SourceGeneration = source.Generation;
            baseline = CloneSnapshot(currentSnapshot);
        }

        string? error = null;
        try
        {
            source.PrepareForCapture?.Invoke();
        }
        catch (Exception exception)
        {
            error = $"The input source could not prepare for capture: {exception.Message}";
        }

        lock (_gate)
        {
            if (!_captures.TryGetValue(capture.CaptureId, out var current)
                || !ReferenceEquals(current, capture)
                || !source.Connected
                || source.Generation != session.Generation)
            {
                return updates;
            }

            if (error is not null)
            {
                updates.Add(CompleteCaptureLocked(capture, InputCaptureStatus.Failed, error));
                return updates;
            }

            capture.Status = InputCaptureStatus.Active;
            capture.Preparing = false;
            capture.IgnoredHeldControls.Clear();
            for (var index = 0; index < baseline.Buttons.Length; index++)
            {
                if (baseline.Buttons[index])
                {
                    capture.IgnoredHeldControls.Add(index);
                }
            }
            updates.Add(new InputCaptureChangedEventArgs(AdvanceLease(capture)));
        }
        return updates;
    }

    private void ExpireCapturesLocked(
        DateTimeOffset now,
        List<InputCaptureChangedEventArgs> updates)
    {
        foreach (var capture in _captures.Values
                     .Where(capture => capture.ExpiresAt <= now)
                     .ToArray())
        {
            updates.Add(CompleteCaptureLocked(
                capture,
                InputCaptureStatus.TimedOut,
                "Capture timed out."));
        }
    }

    private void CompleteCaptureForSourceLocked(
        string sourceId,
        InputCaptureStatus status,
        string detail,
        List<InputCaptureChangedEventArgs> updates)
    {
        if (_captureBySource.TryGetValue(sourceId, out var captureId)
            && _captures.TryGetValue(captureId, out var capture))
        {
            updates.Add(CompleteCaptureLocked(capture, status, detail));
        }
    }

    private InputCaptureChangedEventArgs CompleteCaptureLocked(
        CaptureState capture,
        InputCaptureStatus status,
        string detail,
        JoystickEvent? capturedInput = null)
    {
        capture.Status = status;
        if (capture.SourceGeneration is { } generation)
        {
            foreach (var controlIndex in capture.IgnoredHeldControls)
            {
                _suppressedUntilRelease.Add(new SuppressedControl(
                    capture.SourceId,
                    generation,
                    controlIndex));
            }
        }
        var lease = AdvanceLease(capture);
        RemoveCaptureLocked(capture);
        return new InputCaptureChangedEventArgs(lease, capturedInput, detail);
    }

    private void RemoveCaptureLocked(CaptureState capture)
    {
        _captures.Remove(capture.CaptureId);
        if (_captureBySource.TryGetValue(capture.SourceId, out var current)
            && current == capture.CaptureId)
        {
            _captureBySource.Remove(capture.SourceId);
        }
    }

    private void RemoveSuppressionForSourceLocked(string sourceId, long? generation = null) =>
        _suppressedUntilRelease.RemoveWhere(control =>
            string.Equals(control.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
            && (generation is null || control.Generation == generation));

    private void PublishCaptureUpdates(IEnumerable<InputCaptureChangedEventArgs> updates)
    {
        foreach (var update in updates)
        {
            PublishCaptureUpdate(update);
        }
    }

    private void PublishObservation(InputObservationEventArgs update)
    {
        var subscribers = InputObserved?.GetInvocationList();
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<InputObservationEventArgs> subscriber in subscribers)
        {
            try
            {
                subscriber(this, update);
            }
            catch
            {
                // A settings client can disappear while the runtime continues observing input.
            }
        }
    }

    private void PublishCaptureUpdate(InputCaptureChangedEventArgs update)
    {
        var subscribers = CaptureChanged?.GetInvocationList();
        if (subscribers is null)
        {
            return;
        }

        foreach (EventHandler<InputCaptureChangedEventArgs> subscriber in subscribers)
        {
            try
            {
                subscriber(this, update);
            }
            catch
            {
                // A settings client can disappear while the runtime continues managing leases.
            }
        }
    }

    private static InputSourceDescriptor ValidateDescriptor(InputSourceDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.DisplayName);
        return source with
        {
            SourceId = source.SourceId.Trim(),
            DisplayName = source.DisplayName.Trim(),
            HardwareId = string.IsNullOrWhiteSpace(source.HardwareId) ? null : source.HardwareId.Trim(),
        };
    }

    private static JoystickSnapshot CloneSnapshot(JoystickSnapshot snapshot) => new(
        snapshot.Timestamp,
        [.. snapshot.Buttons],
        [.. snapshot.PointOfViewControllers],
        [.. snapshot.Axes]);

    private static InputSourceState ToSourceState(SourceEntry source) => new(
        source.Descriptor,
        source.Generation == 0 ? null : source.Generation,
        source.Connected);

    private static InputCaptureLease AdvanceLease(CaptureState capture) => new(
        capture.CaptureId,
        capture.ConnectionId,
        capture.SourceId,
        capture.Purpose,
        capture.SourceGeneration,
        capture.ExpiresAt,
        capture.Status,
        checked(++capture.Revision));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class SourceEntry(InputSourceDescriptor descriptor)
    {
        public InputSourceDescriptor Descriptor { get; set; } = descriptor;
        public long Generation { get; set; }
        public bool Connected { get; set; }
        public JoystickSnapshot? LastSnapshot { get; set; }
        public Action? PrepareForCapture { get; set; }
        public SemaphoreSlim DispatchGate { get; } = new(1, 1);
    }

    private sealed class CaptureState(
        Guid captureId,
        string connectionId,
        string sourceId,
        string purpose,
        DateTimeOffset expiresAt)
    {
        public Guid CaptureId { get; } = captureId;
        public string ConnectionId { get; } = connectionId;
        public string SourceId { get; } = sourceId;
        public string Purpose { get; } = purpose;
        public long? SourceGeneration { get; set; }
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public InputCaptureStatus Status { get; set; } = InputCaptureStatus.Pending;
        public bool Preparing { get; set; }
        public long Revision { get; set; }
        public HashSet<int> IgnoredHeldControls { get; } = [];
    }

    private readonly record struct SuppressedControl(string SourceId, long Generation, int ControlIndex);
}
