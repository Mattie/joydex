using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Windows.Runtime;

namespace Joydex.App;

internal sealed class RuntimeConfigurationConnectionChangedEventArgs(
    int generation,
    bool isConnected,
    Exception? failure) : EventArgs
{
    public int Generation { get; } = generation;

    public bool IsConnected { get; } = isConnected;

    public Exception? Failure { get; } = failure;
}

/// <summary>
/// Projects one connection-scoped runtime input session into the models used by Configuration.
/// The runtime retains hardware ownership; this client owns only capture leases it starts.
/// </summary>
internal sealed class RuntimeConfigurationInputClient : IConfigurationInputSession, IAsyncDisposable
{
    private const int MaximumPendingCaptureCallbacks = RuntimeProtocol.MaximumRetainedEvents;
    private readonly object _gate = new();
    private Connection? _connection;
    private RuntimeInputSourceCatalogEntry[] _sources = [];
    private IReadOnlyDictionary<string, InputSourceState> _sourceStates =
        new Dictionary<string, InputSourceState>(StringComparer.OrdinalIgnoreCase);
    private int _latestGeneration = int.MinValue;
    private bool _disposed;

    public event EventHandler<RuntimeConfigurationConnectionChangedEventArgs>? ConnectionChanged;

    public event EventHandler<InputObservationEventArgs>? InputObserved;

    public event EventHandler<InputCaptureChangedEventArgs>? CaptureChanged;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return !_disposed && _connection is not null;
            }
        }
    }

    public IReadOnlyList<RuntimeInputSourceCatalogEntry> Sources
    {
        get
        {
            lock (_gate)
            {
                return [.. _sources];
            }
        }
    }

    public InputSourceState? GetSourceState(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        lock (_gate)
        {
            return _sourceStates.GetValueOrDefault(sourceId);
        }
    }

    /// <summary>Accepts a strictly newer connection generation.</summary>
    public bool BeginConnection(int generation, IRuntimeRpcServer rpc, RuntimeClientState state)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(state);
        Connection? previous;
        InputCaptureChangedEventArgs[] disconnectedCaptures;
        RuntimeSnapshot? initialSnapshot;
        lock (_gate)
        {
            if (_disposed || generation <= _latestGeneration)
            {
                return false;
            }

            previous = _connection;
            disconnectedCaptures = previous is null
                ? []
                : DisconnectCapturesLocked(previous);
            _latestGeneration = generation;
            var connection = new Connection(generation, rpc);
            _connection = connection;
            initialSnapshot = state.Current.Snapshot;
            if (initialSnapshot is not null)
            {
                ApplySourcesLocked(initialSnapshot.Input.Sources);
                SeedCapturesLocked(connection, initialSnapshot.Input.Captures);
            }
        }

        Cancel(previous);
        RaiseCaptureChanges(disconnectedCaptures);
        if (previous is not null)
        {
            ConnectionChanged?.Invoke(
                this,
                new RuntimeConfigurationConnectionChangedEventArgs(
                    previous.Generation,
                    isConnected: false,
                    failure: null));
        }
        ConnectionChanged?.Invoke(
            this,
            new RuntimeConfigurationConnectionChangedEventArgs(
                generation,
                isConnected: true,
                failure: null));
        return true;
    }

    /// <summary>
    /// Applies a state callback already attributed to a connection generation by the owner.
    /// </summary>
    public bool ApplyStateChange(int generation, RuntimeClientStateChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Kind == RuntimeClientChangeKind.Disconnected || change.State.IsDisconnected)
        {
            return EndConnection(generation, change.State.DisconnectFailure);
        }

        InputObservationEventArgs? observation = null;
        InputCaptureChangedEventArgs? capture = null;
        lock (_gate)
        {
            if (!TryGetCurrentLocked(generation, out var connection))
            {
                return false;
            }

            if (change.State.Snapshot is { } snapshot)
            {
                ApplySourcesLocked(snapshot.Input.Sources);
                if (change.Kind == RuntimeClientChangeKind.Initialized)
                {
                    SeedCapturesLocked(connection, snapshot.Input.Captures);
                }
            }

            if (change.InputEvent?.Capture is { } captureUpdate)
            {
                capture = ApplyCaptureCallbackLocked(connection, captureUpdate);
            }
            if (change.InputEvent?.Observation is { } inputObservation)
            {
                observation = MapObservationLocked(inputObservation);
            }
        }

        if (capture is not null)
        {
            CaptureChanged?.Invoke(this, capture);
        }
        if (observation is not null)
        {
            InputObserved?.Invoke(this, observation);
        }
        return true;
    }

    /// <summary>Ends only the matching live generation and preserves its last source catalog.</summary>
    public bool EndConnection(int generation, Exception? failure)
    {
        Connection connection;
        InputCaptureChangedEventArgs[] disconnectedCaptures;
        lock (_gate)
        {
            if (!TryGetCurrentLocked(generation, out connection))
            {
                return false;
            }
            _connection = null;
            disconnectedCaptures = DisconnectCapturesLocked(connection);
        }

        Cancel(connection);
        RaiseCaptureChanges(disconnectedCaptures);
        ConnectionChanged?.Invoke(
            this,
            new RuntimeConfigurationConnectionChangedEventArgs(
                generation,
                isConnected: false,
                failure));
        return true;
    }

    public async Task<IReadOnlyList<RuntimeInputSourceCatalogEntry>> RefreshSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = GetConnection();
        using var linked = Link(connection, cancellationToken);
        var snapshot = await connection.Rpc.RefreshInputSourcesAsync(linked.Token).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            EnsureCurrentLocked(connection);
            ApplySourcesLocked(snapshot.Sources);
            SeedCapturesLocked(connection, snapshot.Captures);
            return [.. _sources];
        }
    }

    public async Task<InputCaptureStartResult> BeginCaptureAsync(
        string sourceId,
        string purpose,
        long? expectedGeneration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (!TryGetConnection(out var connection))
        {
            return InputCaptureStartResult.Rejected("The runtime input connection is unavailable.");
        }

        RuntimeCaptureStartResult result;
        try
        {
            using var linked = Link(connection, cancellationToken);
            result = await connection.Rpc.BeginInputCaptureAsync(
                    new RuntimeCaptureRequest(sourceId, purpose, expectedGeneration),
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                EnsureCurrentLocked(connection);
            }
            return InputCaptureStartResult.Rejected(
                "The capture start result was unavailable and the request was not retried: "
                + exception.Message);
        }

        InputCaptureChangedEventArgs? replay = null;
        InputCaptureLease lease;
        lock (_gate)
        {
            EnsureCurrentLocked(connection);
            if (!result.Accepted || result.Lease is null)
            {
                return InputCaptureStartResult.Rejected(result.Error ?? "Capture could not start.");
            }
            ValidateCaptureLease(result.Lease, sourceId, purpose);
            var acknowledged = MapCapture(connection, new RuntimeCaptureUpdate(result.Lease));
            connection.KnownCaptureIds.Add(result.Lease.CaptureId);
            connection.Captures[result.Lease.CaptureId] = acknowledged;
            lease = acknowledged.Lease;
            if (connection.PendingCallbacks.Remove(result.Lease.CaptureId, out var pending)
                && pending.Lease.Revision > result.Lease.Revision)
            {
                replay = MapCapture(connection, pending);
                connection.Captures[result.Lease.CaptureId] = replay;
                lease = replay.Lease;
            }
        }

        if (replay is not null)
        {
            CaptureChanged?.Invoke(this, replay);
        }
        return InputCaptureStartResult.Started(lease);
    }

    public async Task<InputCaptureLease?> RenewCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetOwnedCapture(captureId, out var connection))
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("The runtime input connection is unavailable.");
            }
            return null;
        }

        RuntimeCaptureCommandResult result;
        try
        {
            using var linked = Link(connection, cancellationToken);
            result = await connection.Rpc.RenewInputCaptureAsync(captureId, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAmbiguousFailure(exception))
        {
            var reconciled = await ReconcileCaptureAsync(
                    connection,
                    captureId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (reconciled is not null
                && !IsTerminal(reconciled.Lease.Status))
            {
                return reconciled.Lease;
            }
            throw new InvalidOperationException(
                "The capture renewal could not be confirmed and was not retried.",
                exception);
        }

        if (!result.Succeeded || result.Lease is null)
        {
            throw new InvalidOperationException(result.Error ?? "The capture could not be renewed.");
        }
        ValidateCaptureIdentity(result.Lease, captureId);
        return ApplyCommandLease(connection, result.Lease).Lease;
    }

    public async Task<bool> CancelCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetOwnedCapture(captureId, out var connection))
        {
            return false;
        }

        RuntimeCaptureCommandResult result;
        try
        {
            using var linked = Link(connection, cancellationToken);
            result = await connection.Rpc.CancelInputCaptureAsync(captureId, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAmbiguousFailure(exception))
        {
            var reconciled = await ReconcileCaptureAsync(
                    connection,
                    captureId,
                    cancellationToken)
                .ConfigureAwait(false);
            return reconciled is not null && IsTerminal(reconciled.Lease.Status);
        }

        if (!result.Succeeded || result.Lease is null)
        {
            return false;
        }
        ValidateCaptureIdentity(result.Lease, captureId);
        _ = ApplyCommandLease(connection, result.Lease);
        return true;
    }

    public async Task<InputCaptureChangedEventArgs?> GetCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetConnection(out var connection))
        {
            return null;
        }
        return await ReadCaptureAsync(
                connection,
                captureId,
                publishChange: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Connection? connection;
        Guid[] activeCaptureIds;
        InputCaptureChangedEventArgs[] disconnectedCaptures;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            connection = _connection;
            _connection = null;
            if (connection is null)
            {
                return;
            }
            activeCaptureIds = connection.Captures.Values
                .Where(capture => !IsTerminal(capture.Lease.Status))
                .Select(capture => capture.Lease.CaptureId)
                .ToArray();
            disconnectedCaptures = DisconnectCapturesLocked(connection);
        }

        connection.Cancellation.Cancel();
        foreach (var captureId in activeCaptureIds)
        {
            try
            {
                _ = await connection.Rpc.CancelInputCaptureAsync(captureId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Closing the transport also releases the connection-scoped lease.
            }
        }
        connection.Cancellation.Dispose();
        RaiseCaptureChanges(disconnectedCaptures);
        ConnectionChanged?.Invoke(
            this,
            new RuntimeConfigurationConnectionChangedEventArgs(
                connection.Generation,
                isConnected: false,
                failure: null));
    }

    private InputCaptureChangedEventArgs? ApplyCaptureCallbackLocked(
        Connection connection,
        RuntimeCaptureUpdate update)
    {
        ValidateCaptureIdentity(update.Lease, update.Lease.CaptureId);
        if (!connection.KnownCaptureIds.Contains(update.Lease.CaptureId))
        {
            if (!connection.PendingCallbacks.TryGetValue(update.Lease.CaptureId, out var current)
                || update.Lease.Revision > current.Lease.Revision)
            {
                if (connection.PendingCallbacks.Count == MaximumPendingCaptureCallbacks)
                {
                    connection.PendingCallbacks.Remove(connection.PendingCallbacks.Keys.First());
                }
                connection.PendingCallbacks[update.Lease.CaptureId] = update;
            }
            return null;
        }

        var mapped = MapCapture(connection, update);
        if (connection.Captures.TryGetValue(update.Lease.CaptureId, out var previous)
            && previous.Lease.Revision >= mapped.Lease.Revision)
        {
            return null;
        }
        connection.Captures[update.Lease.CaptureId] = mapped;
        return mapped;
    }

    private async Task<InputCaptureChangedEventArgs?> ReconcileCaptureAsync(
        Connection connection,
        Guid captureId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCaptureAsync(
                    connection,
                    captureId,
                    publishChange: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAmbiguousFailure(exception))
        {
            throw new InvalidOperationException(
                "The capture state could not be reconciled and the side effect was not retried.",
                exception);
        }
    }

    private async Task<InputCaptureChangedEventArgs?> ReadCaptureAsync(
        Connection connection,
        Guid captureId,
        bool publishChange,
        CancellationToken cancellationToken)
    {
        using var linked = Link(connection, cancellationToken);
        var lookup = await connection.Rpc.GetInputCaptureAsync(captureId, linked.Token)
            .ConfigureAwait(false);
        InputCaptureChangedEventArgs? mapped;
        var changed = false;
        lock (_gate)
        {
            EnsureCurrentLocked(connection);
            if (lookup.Status == RuntimeCaptureLookupStatus.NotFound)
            {
                return null;
            }
            var update = lookup.Capture
                ?? throw new InvalidDataException("The runtime omitted the requested capture state.");
            ValidateCaptureIdentity(update.Lease, captureId);
            connection.KnownCaptureIds.Add(captureId);
            mapped = MapCapture(connection, update);
            if (connection.Captures.TryGetValue(captureId, out var current)
                && current.Lease.Revision >= mapped.Lease.Revision)
            {
                return current;
            }
            connection.Captures[captureId] = mapped;
            changed = true;
        }
        if (changed && publishChange)
        {
            CaptureChanged?.Invoke(this, mapped);
        }
        return mapped;
    }

    private InputCaptureChangedEventArgs ApplyCommandLease(
        Connection connection,
        RuntimeCaptureLease lease)
    {
        InputCaptureChangedEventArgs mapped;
        lock (_gate)
        {
            EnsureCurrentLocked(connection);
            connection.KnownCaptureIds.Add(lease.CaptureId);
            mapped = MapCapture(connection, new RuntimeCaptureUpdate(lease));
            if (connection.Captures.TryGetValue(lease.CaptureId, out var current)
                && current.Lease.Revision >= mapped.Lease.Revision)
            {
                return current;
            }
            connection.Captures[lease.CaptureId] = mapped;
        }
        CaptureChanged?.Invoke(this, mapped);
        return mapped;
    }

    private static InputCaptureChangedEventArgs MapCapture(
        Connection connection,
        RuntimeCaptureUpdate update) =>
        new(
            new InputCaptureLease(
                update.Lease.CaptureId,
                connection.PresentationConnectionId,
                update.Lease.SourceId,
                update.Lease.Purpose,
                update.Lease.SourceGeneration,
                update.Lease.ExpiresAt,
                update.Lease.Status,
                update.Lease.Revision),
            update.CapturedInput,
            update.Detail);

    private InputObservationEventArgs MapObservationLocked(RuntimeInputObservation observation)
    {
        var source = _sourceStates.GetValueOrDefault(observation.SourceId);
        var descriptor = source?.Descriptor
            ?? new InputSourceDescriptor(observation.SourceId, observation.SourceId);
        return new InputObservationEventArgs(new InputObservation(
            observation.Sequence,
            new InputSourceState(
                descriptor,
                observation.SourceGeneration,
                source?.Connected ?? true),
            observation.Snapshot,
            observation.Events));
    }

    private void ApplySourcesLocked(IReadOnlyList<RuntimeInputSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.Select(MapSource).ToArray();
        _sourceStates = sources.ToDictionary(
            source => source.SourceId,
            MapSourceState,
            StringComparer.OrdinalIgnoreCase);
    }

    private static RuntimeInputSourceCatalogEntry MapSource(RuntimeInputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ = Guid.TryParse(source.HardwareId, out var instanceGuid);
        _ = Guid.TryParse(source.ProductId, out var productGuid);
        return new RuntimeInputSourceCatalogEntry(
            new InputSourceDescriptor(source.SourceId, source.DisplayName, source.HardwareId),
            new DeviceSelector
            {
                ProductNameContains = source.DisplayName,
                InstanceGuid = source.HardwareId,
                ProductGuid = source.ProductId,
            },
            instanceGuid,
            productGuid,
            source.ConfiguredDeviceId);
    }

    private static InputSourceState MapSourceState(RuntimeInputSource source) =>
        new(
            new InputSourceDescriptor(source.SourceId, source.DisplayName, source.HardwareId),
            source.Generation,
            source.Connected);

    private static void SeedCapturesLocked(
        Connection connection,
        IEnumerable<RuntimeCaptureLease> captures)
    {
        foreach (var lease in captures)
        {
            if (IsTerminal(lease.Status))
            {
                continue;
            }
            connection.KnownCaptureIds.Add(lease.CaptureId);
            var mapped = MapCapture(connection, new RuntimeCaptureUpdate(lease));
            if (!connection.Captures.TryGetValue(lease.CaptureId, out var current)
                || current.Lease.Revision < mapped.Lease.Revision)
            {
                connection.Captures[lease.CaptureId] = mapped;
            }
        }
    }

    private static InputCaptureChangedEventArgs[] DisconnectCapturesLocked(Connection connection)
    {
        var disconnected = connection.Captures.Values
            .Where(capture => !IsTerminal(capture.Lease.Status))
            .Select(capture => new InputCaptureChangedEventArgs(
                capture.Lease with
                {
                    ExpiresAt = DateTimeOffset.UtcNow,
                    Status = InputCaptureStatus.ClientDisconnected,
                    Revision = capture.Lease.Revision == long.MaxValue
                        ? long.MaxValue
                        : capture.Lease.Revision + 1,
                },
                detail: "The runtime input connection ended."))
            .ToArray();
        connection.Captures.Clear();
        connection.KnownCaptureIds.Clear();
        connection.PendingCallbacks.Clear();
        return disconnected;
    }

    private bool TryGetConnection(out Connection connection)
    {
        lock (_gate)
        {
            if (_disposed || _connection is null)
            {
                connection = null!;
                return false;
            }
            connection = _connection;
            return true;
        }
    }

    private Connection GetConnection()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _connection
                ?? throw new InvalidOperationException("The runtime input connection is unavailable.");
        }
    }

    private bool TryGetOwnedCapture(Guid captureId, out Connection connection)
    {
        lock (_gate)
        {
            if (_disposed
                || _connection is null
                || !_connection.KnownCaptureIds.Contains(captureId)
                || !_connection.Captures.TryGetValue(captureId, out var capture)
                || IsTerminal(capture.Lease.Status))
            {
                connection = null!;
                return false;
            }
            connection = _connection;
            return true;
        }
    }

    private bool TryGetCurrentLocked(int generation, out Connection connection)
    {
        if (_disposed || _connection is null || _connection.Generation != generation)
        {
            connection = null!;
            return false;
        }
        connection = _connection;
        return true;
    }

    private void EnsureCurrentLocked(Connection connection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(_connection, connection))
        {
            throw new OperationCanceledException(
                "The runtime input connection changed before the operation completed.");
        }
    }

    private static CancellationTokenSource Link(
        Connection connection,
        CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            connection.Cancellation.Token);

    private static void ValidateCaptureLease(
        RuntimeCaptureLease lease,
        string sourceId,
        string purpose)
    {
        ValidateCaptureIdentity(lease, lease.CaptureId);
        if (!string.Equals(lease.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(lease.Purpose, purpose, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The runtime returned a capture for a different request.");
        }
    }

    private static void ValidateCaptureIdentity(RuntimeCaptureLease lease, Guid captureId)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (captureId == Guid.Empty || lease.CaptureId != captureId)
        {
            throw new InvalidDataException("The runtime returned a mismatched capture identity.");
        }
    }

    private static bool IsTerminal(InputCaptureStatus status) => status is not
        (InputCaptureStatus.Pending or InputCaptureStatus.Active);

    private static bool IsAmbiguousFailure(Exception exception) => exception is
        IOException or TimeoutException or InvalidOperationException;

    private void RaiseCaptureChanges(InputCaptureChangedEventArgs[] captures)
    {
        foreach (var capture in captures)
        {
            CaptureChanged?.Invoke(this, capture);
        }
    }

    private static void Cancel(Connection? connection)
    {
        if (connection is not null)
        {
            connection.Cancellation.Cancel();
        }
    }

    private sealed class Connection(
        int generation,
        IRuntimeRpcServer rpc)
    {
        public int Generation { get; } = generation;

        public IRuntimeRpcServer Rpc { get; } = rpc;

        public string PresentationConnectionId { get; } = $"runtime-settings:{generation}";

        public CancellationTokenSource Cancellation { get; } = new();

        public HashSet<Guid> KnownCaptureIds { get; } = [];

        public Dictionary<Guid, InputCaptureChangedEventArgs> Captures { get; } = [];

        public Dictionary<Guid, RuntimeCaptureUpdate> PendingCallbacks { get; } = [];
    }
}
