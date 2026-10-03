using Joydex.Contracts;

namespace Joydex.RuntimeHost;

internal sealed class RuntimeEventHub
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Queue<RuntimeEvent> _retained = new();
    private readonly Dictionary<string, Action<RuntimeEvent>> _subscribers = new(StringComparer.Ordinal);
    private long _sequence;

    public RuntimeEventHub(Guid engineEpoch, int capacity = RuntimeProtocol.MaximumRetainedEvents)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        EngineEpoch = engineEpoch;
        _capacity = capacity;
    }

    public Guid EngineEpoch { get; }

    public long Cursor
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    public RuntimeEvent Publish(
        RuntimeEventKind kind,
        SettingsSnapshot? settings = null,
        ApplySettingsResult? operation = null,
        RuntimeIdentitySnapshot? identity = null,
        RuntimeInputSourceSnapshot? inputSources = null,
        RuntimePromptPickerSnapshot? promptPicker = null,
        RuntimeButtonMapVisibility? buttonMapVisibility = null,
        RuntimeControllerStatus? controllerStatus = null,
        RuntimeActionActivity? actionActivity = null,
        RuntimeTaskAlertSnapshot? taskAlerts = null,
        RuntimeVoiceEvent? voice = null,
        RuntimePebbleIndexSnapshot? pebbleIndex = null)
    {
        RuntimeEvent runtimeEvent;
        Action<RuntimeEvent>[] subscribers;
        lock (_gate)
        {
            runtimeEvent = new RuntimeEvent(
                EngineEpoch,
                checked(++_sequence),
                kind,
                settings,
                operation,
                identity,
                inputSources,
                promptPicker,
                buttonMapVisibility,
                controllerStatus,
                actionActivity,
                taskAlerts,
                voice,
                pebbleIndex);
            _retained.Enqueue(runtimeEvent);
            while (_retained.Count > _capacity)
            {
                _retained.Dequeue();
            }
            subscribers = _subscribers.Values.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber(runtimeEvent);
            }
            catch
            {
                // A disconnected or faulty client must not interrupt the committed runtime change
                // or prevent another client from observing it. Connection cleanup removes the sink.
            }
        }
        return runtimeEvent;
    }

    public RuntimeEventSubscription Subscribe(
        string connectionId,
        Guid? previousEpoch,
        long? afterSequence,
        Action<RuntimeEvent> callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            var hasEpoch = previousEpoch is not null;
            var hasCursor = afterSequence is not null;
            var resync = hasEpoch != hasCursor
                         || (previousEpoch is not null && previousEpoch != EngineEpoch);
            RuntimeEvent[] replay = [];
            if (!resync && previousEpoch is not null && afterSequence is { } cursor)
            {
                var oldest = _retained.Count == 0 ? _sequence + 1 : _retained.Peek().Sequence;
                if (cursor < oldest - 1 || cursor > _sequence)
                {
                    resync = true;
                }
                else
                {
                    replay = _retained.Where(item => item.Sequence > cursor).ToArray();
                }
            }

            if (!_subscribers.TryAdd(connectionId, callback))
            {
                throw new InvalidOperationException($"Connection '{connectionId}' is already subscribed.");
            }

            return new RuntimeEventSubscription(
                resync,
                replay,
                _sequence,
                () => Unsubscribe(connectionId, callback));
        }
    }

    private void Unsubscribe(string connectionId, Action<RuntimeEvent> callback)
    {
        lock (_gate)
        {
            if (_subscribers.TryGetValue(connectionId, out var current)
                && ReferenceEquals(current, callback))
            {
                _subscribers.Remove(connectionId);
            }
        }
    }
}

internal sealed record RuntimeEventSubscription(
    bool ResynchronizationRequired,
    RuntimeEvent[] Replay,
    long Cursor,
    Action Unsubscribe) : IDisposable
{
    public void Dispose() => Unsubscribe();
}
