using System.Threading.Channels;
using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Windows.Input;

namespace Joydex.Windows.Runtime;

/// <summary>
/// Polls one acquired controller independently of action dispatch. Every buffered edge and changed
/// state is copied in order. A full queue ends this generation rather than losing a release.
/// </summary>
internal sealed class DirectInputAcquisition(IJoystickSource source, PollingOptions polling)
{
    internal const int Capacity = 256;
    private readonly Channel<AcquiredInput> _frames = Channel.CreateBounded<AcquiredInput>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, SingleWriter = true });

    internal ChannelReader<AcquiredInput> Frames => _frames.Reader;
    private long _sequence;
    internal long LatestSequence => Interlocked.Read(ref _sequence);

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await Task.Delay(polling.ConnectWarmupMs, cancellationToken).ConfigureAwait(false);
            JoystickSnapshot? previous = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Reserve before reading so capture also fences an in-flight pre-request poll.
                var sequence = Interlocked.Increment(ref _sequence);
                if (!source.TryRead(out var snapshot, out var error) || snapshot is null)
                {
                    throw new IOException("DirectInput disconnected: " + (error ?? "controller unavailable"));
                }

                var copy = new JoystickSnapshot(snapshot.Timestamp, [.. snapshot.Buttons],
                    [.. snapshot.PointOfViewControllers], [.. snapshot.Axes]);
                // Warmup establishes held-state baseline and intentionally discards old buffer edges.
                var events = previous is null ? [] : source.LatestBufferedButtonEvents.ToArray();
                // Keep an observation heartbeat whenever dispatch is caught up. During a slow action,
                // identical idle polls need no queue space; changed state and edges are never coalesced.
                if (previous is null || events.Length != 0 || !SameState(previous, copy)
                    || _frames.Reader.Count == 0)
                {
                    if (!_frames.Writer.TryWrite(new AcquiredInput(copy, events, sequence)))
                    {
                        throw new IOException("DirectInput dispatch queue filled; ending this source generation.");
                    }
                }
                previous = copy;
                await Task.Delay(polling.PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _frames.Writer.TryComplete(failure);
        }
    }

    private static bool SameState(JoystickSnapshot first, JoystickSnapshot second) =>
        first.Buttons.AsSpan().SequenceEqual(second.Buttons)
        && first.PointOfViewControllers.AsSpan().SequenceEqual(second.PointOfViewControllers)
        && first.Axes.AsSpan().SequenceEqual(second.Axes);
}

internal sealed record AcquiredInput(JoystickSnapshot Snapshot, IReadOnlyList<JoystickEvent> Events, long Sequence);
