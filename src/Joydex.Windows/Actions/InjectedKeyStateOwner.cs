using Joydex.Core.Input;

namespace Joydex.Windows.Actions;

/// <summary>
/// Coordinates injected held chords across every action executor in one Joydex runtime.
/// </summary>
public sealed class InjectedKeyStateOwner
{
    private readonly object _gate = new();
    private readonly IInputSender _inputSender;
    private readonly Dictionary<HoldIdentity, HeldKeyState> _holds = [];
    private readonly Dictionary<string, ChordState> _chords = new(StringComparer.Ordinal);
    private readonly Dictionary<CleanupDebtIdentity, KeyChord> _cleanupDebts = [];
    private readonly HashSet<string> _clearedStaleChords = new(StringComparer.Ordinal);

    public InjectedKeyStateOwner(IInputSender? inputSender = null)
    {
        _inputSender = inputSender ?? new WindowsInputSender();
    }

    internal bool Hold(InjectedKeyHoldId owner, KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        var identity = HoldIdentity.From(owner);
        var chordKey = CanonicalChordKey(chord);
        lock (_gate)
        {
            if (_holds.ContainsKey(identity))
            {
                return false;
            }

            ResolveCleanupDebtForChordLocked(chordKey);
            if (_chords.TryGetValue(chordKey, out var existing))
            {
                existing.OwnerCount++;
                _holds.Add(identity, new HeldKeyState(chordKey, chord));
                return true;
            }

            try
            {
                _inputSender.HoldChord(chord);
            }
            catch
            {
                var debt = new CleanupDebtIdentity(identity.Source, chordKey);
                _cleanupDebts[debt] = chord;
                try
                {
                    _inputSender.ReleaseChord(chord);
                    _cleanupDebts.Remove(debt);
                }
                catch
                {
                    // The source and all-source cleanup paths retain and retry this debt.
                }
                throw;
            }

            _chords.Add(chordKey, new ChordState(chord));
            _holds.Add(identity, new HeldKeyState(chordKey, chord));
            return true;
        }
    }

    internal InjectedKeyRelease Release(InjectedKeyHoldId owner)
    {
        var identity = HoldIdentity.From(owner);
        lock (_gate)
        {
            if (!_holds.TryGetValue(identity, out var held))
            {
                return new InjectedKeyRelease(false, false);
            }

            var chord = _chords[held.ChordKey];
            if (chord.OwnerCount > 1)
            {
                chord.OwnerCount--;
                _holds.Remove(identity);
                return new InjectedKeyRelease(true, false);
            }

            _inputSender.ReleaseChord(chord.Chord);
            _holds.Remove(identity);
            _chords.Remove(held.ChordKey);
            ClearCleanupDebtForChordLocked(held.ChordKey);
            return new InjectedKeyRelease(true, true);
        }
    }

    internal bool ClearStaleChord(KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        var chordKey = CanonicalChordKey(chord);
        lock (_gate)
        {
            if (!_clearedStaleChords.Add(chordKey))
            {
                return false;
            }

            if (_chords.ContainsKey(chordKey))
            {
                return false;
            }

            try
            {
                _inputSender.ReleaseChord(chord);
                return true;
            }
            catch
            {
                _clearedStaleChords.Remove(chordKey);
                throw;
            }
        }
    }

    public void ReleaseSource(InputSourceSession source)
    {
        var failures = new List<Exception>();
        lock (_gate)
        {
            var sourceHolds = _holds
                .Where(pair => pair.Key.Source.Equals(SourceIdentity.From(source)))
                .GroupBy(pair => pair.Value.ChordKey, StringComparer.Ordinal)
                .ToArray();
            foreach (var group in sourceHolds)
            {
                var chord = _chords[group.Key];
                var sourceOwnerCount = group.Count();
                if (chord.OwnerCount > sourceOwnerCount)
                {
                    chord.OwnerCount -= sourceOwnerCount;
                    foreach (var hold in group)
                    {
                        _holds.Remove(hold.Key);
                    }
                    continue;
                }

                try
                {
                    _inputSender.ReleaseChord(chord.Chord);
                    foreach (var hold in group)
                    {
                        _holds.Remove(hold.Key);
                    }
                    _chords.Remove(group.Key);
                    ClearCleanupDebtForChordLocked(group.Key);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            foreach (var debt in _cleanupDebts
                         .Where(pair => pair.Key.Source.Equals(SourceIdentity.From(source)))
                         .ToArray())
            {
                if (_chords.ContainsKey(debt.Key.ChordKey))
                {
                    continue;
                }
                try
                {
                    _inputSender.ReleaseChord(debt.Value);
                    _cleanupDebts.Remove(debt.Key);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }

        ThrowCleanupFailures(failures, $"Could not release every injected chord for source '{source.SourceId}'.");
    }

    public void ReleaseAll()
    {
        var failures = new List<Exception>();
        lock (_gate)
        {
            foreach (var chord in _chords.ToArray())
            {
                try
                {
                    _inputSender.ReleaseChord(chord.Value.Chord);
                    _chords.Remove(chord.Key);
                    foreach (var owner in _holds
                                 .Where(pair => string.Equals(pair.Value.ChordKey, chord.Key, StringComparison.Ordinal))
                                 .Select(pair => pair.Key)
                                 .ToArray())
                    {
                        _holds.Remove(owner);
                    }
                    ClearCleanupDebtForChordLocked(chord.Key);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            foreach (var debt in _cleanupDebts.ToArray())
            {
                try
                {
                    _inputSender.ReleaseChord(debt.Value);
                    _cleanupDebts.Remove(debt.Key);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }

        ThrowCleanupFailures(failures, "Could not release every injected chord.");
    }

    private static string CanonicalChordKey(KeyChord chord) =>
        string.Join(',', chord.VirtualKeys.Select(key => key.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));

    private void ResolveCleanupDebtForChordLocked(string chordKey)
    {
        foreach (var debt in _cleanupDebts
                     .Where(pair => string.Equals(pair.Key.ChordKey, chordKey, StringComparison.Ordinal))
                     .ToArray())
        {
            try
            {
                _inputSender.ReleaseChord(debt.Value);
                _cleanupDebts.Remove(debt.Key);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "A previous partial hold for this chord still needs cleanup.",
                    exception);
            }
        }
    }

    private void ClearCleanupDebtForChordLocked(string chordKey)
    {
        foreach (var debt in _cleanupDebts.Keys
                     .Where(debt => string.Equals(debt.ChordKey, chordKey, StringComparison.Ordinal))
                     .ToArray())
        {
            _cleanupDebts.Remove(debt);
        }
    }

    private static void ThrowCleanupFailures(List<Exception> failures, string message)
    {
        if (failures.Count == 1)
        {
            throw failures[0];
        }
        if (failures.Count > 1)
        {
            throw new AggregateException(message, failures);
        }
    }

    private readonly record struct SourceIdentity(string SourceId, long Generation)
    {
        public static SourceIdentity From(InputSourceSession source) => new(
            source.SourceId.ToUpperInvariant(),
            source.Generation);
    }

    private readonly record struct HoldIdentity(
        SourceIdentity Source,
        string Bank,
        int Button,
        string ActionId)
    {
        public static HoldIdentity From(InjectedKeyHoldId owner) => new(
            SourceIdentity.From(owner.Source),
            owner.Bank.ToUpperInvariant(),
            owner.Button,
            owner.ActionId.ToUpperInvariant());
    }

    private readonly record struct CleanupDebtIdentity(SourceIdentity Source, string ChordKey);

    private sealed record HeldKeyState(string ChordKey, KeyChord Chord);

    private sealed class ChordState(KeyChord chord)
    {
        public KeyChord Chord { get; } = chord;
        public int OwnerCount { get; set; } = 1;
    }
}

internal readonly record struct InjectedKeyHoldId(
    InputSourceSession Source,
    string Bank,
    int Button,
    string ActionId);

internal readonly record struct InjectedKeyRelease(bool WasHeld, bool KeysReleased);
