using Joydex.Core.Input;

namespace Joydex.App;

/// <summary>Keeps at most one selected-controller observation waiting for the settings UI.</summary>
internal sealed class InputObservationCoalescer
{
    private const int MaximumPendingEvents = 32;
    private readonly object _gate = new();
    private string? _selectedSourceId;
    private InputObservation? _lastAccepted;
    private InputObservation? _pending;
    private bool _dispatchScheduled;

    public void SelectSource(string? sourceId)
    {
        sourceId = string.IsNullOrWhiteSpace(sourceId) ? null : sourceId.Trim();
        lock (_gate)
        {
            if (string.Equals(_selectedSourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedSourceId = sourceId;
            _lastAccepted = null;
            _pending = null;
            _dispatchScheduled = false;
        }
    }

    /// <summary>
    /// Queues the latest useful observation and reports whether the caller must schedule a UI drain.
    /// </summary>
    public bool TryQueue(InputObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            if (_selectedSourceId is null
                || !string.Equals(
                    _selectedSourceId,
                    observation.Source.Descriptor.SourceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (observation.Events.Count == 0
                && _lastAccepted is not null
                && HasSameVisibleState(_lastAccepted, observation))
            {
                return TrySchedulePending();
            }

            _lastAccepted = observation;
            _pending = _pending is null ? observation : Merge(_pending, observation);
            if (_dispatchScheduled)
            {
                return false;
            }

            _dispatchScheduled = true;
            return true;
        }
    }

    public InputObservation? TakePending()
    {
        lock (_gate)
        {
            var observation = _pending;
            _pending = null;
            _dispatchScheduled = false;
            return observation;
        }
    }

    /// <summary>Allows a pending observation to schedule another UI drain after posting failed.</summary>
    public void CancelScheduledDispatch()
    {
        lock (_gate)
        {
            _dispatchScheduled = false;
        }
    }

    private bool TrySchedulePending()
    {
        if (_pending is null || _dispatchScheduled)
        {
            return false;
        }

        _dispatchScheduled = true;
        return true;
    }

    private static bool HasSameVisibleState(InputObservation left, InputObservation right) =>
        left.Source == right.Source
        && left.Snapshot.Buttons.SequenceEqual(right.Snapshot.Buttons)
        && left.Snapshot.PointOfViewControllers.SequenceEqual(right.Snapshot.PointOfViewControllers)
        && left.Snapshot.Axes.SequenceEqual(right.Snapshot.Axes);

    private static InputObservation Merge(InputObservation pending, InputObservation latest)
    {
        if (pending.Events.Count == 0)
        {
            return latest;
        }
        if (latest.Events.Count == 0)
        {
            return latest with { Events = pending.Events };
        }

        return latest with
        {
            Events = pending.Events
                .Concat(latest.Events)
                .TakeLast(MaximumPendingEvents)
                .ToArray(),
        };
    }
}
