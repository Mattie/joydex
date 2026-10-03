using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Mapping;
using Joydex.Core.Runtime;
using Joydex.Windows.Actions;

namespace Joydex.Tests;

public sealed class InjectedKeyStateOwnerTests
{
    [Fact]
    public async Task TwoExecutorsShareOnePhysicalChordUntilBothSourcesRelease()
    {
        var input = new RecordingInputSender();
        var keys = new InjectedKeyStateOwner(input);
        var first = CreateExecutor(input, keys);
        var second = CreateExecutor(input, keys);
        var sourceA = new InputSourceSession("stick-a", 1);
        var sourceB = new InputSourceSession("stick-b", 2);

        await first.ExecuteAsync(Request(sourceA, "work", 3, "press"), CancellationToken.None);
        await second.ExecuteAsync(Request(sourceB, "work", 3, "press"), CancellationToken.None);
        first.ReleaseHeldKeys(sourceA);

        Assert.Single(input.HeldChords);
        Assert.Empty(input.ReleasedChords);

        await second.ExecuteAsync(Request(sourceB, "work", 3, "release"), CancellationToken.None);

        Assert.Single(input.HeldChords);
        Assert.Single(input.ReleasedChords);
        Assert.Equal("Ctrl+CapsLock", input.ReleasedChords[0].NormalizedText);
    }

    [Fact]
    public async Task DelayedOldGenerationReleaseCannotClearNewGenerationHold()
    {
        var input = new RecordingInputSender();
        var keys = new InjectedKeyStateOwner(input);
        var executor = CreateExecutor(input, keys);
        var oldSource = new InputSourceSession("stick-a", 1);
        var newSource = new InputSourceSession("stick-a", 2);
        await executor.ExecuteAsync(Request(oldSource, "work", 3, "press"), CancellationToken.None);
        executor.ReleaseHeldKeys(oldSource);
        await executor.ExecuteAsync(Request(newSource, "work", 3, "press"), CancellationToken.None);

        var staleRelease = await executor.ExecuteAsync(
            Request(oldSource, "work", 3, "release"),
            CancellationToken.None);

        Assert.False(staleRelease.Executed);
        Assert.Equal(2, input.HeldChords.Count);
        Assert.Single(input.ReleasedChords);

        await executor.ExecuteAsync(Request(newSource, "work", 3, "release"), CancellationToken.None);
        Assert.Equal(2, input.ReleasedChords.Count);
    }

    [Fact]
    public async Task CaptureCleanupOnOneSourcePreservesTheOtherSourcesSharedChord()
    {
        var input = new RecordingInputSender();
        var keys = new InjectedKeyStateOwner(input);
        var first = CreateExecutor(input, keys);
        var second = CreateExecutor(input, keys);
        using var host = new RuntimeInputHost();
        InputSourceSession sourceA = default;
        sourceA = host.ConnectSource(
            new InputSourceDescriptor("stick-a", "Stick A"),
            () => first.ReleaseHeldKeys(sourceA));
        var sourceB = host.ConnectSource(
            new InputSourceDescriptor("stick-b", "Stick B"),
            () => { });
        await Route(host, sourceA);
        await Route(host, sourceB);
        await first.ExecuteAsync(Request(sourceA, "work", 3, "press"), CancellationToken.None);
        await second.ExecuteAsync(Request(sourceB, "work", 3, "press"), CancellationToken.None);

        host.BeginCapture(new InputCaptureRequest("ui-1", "stick-a", "binding"));
        await Route(host, sourceA);

        Assert.Single(input.HeldChords);
        Assert.Empty(input.ReleasedChords);
        await second.ExecuteAsync(Request(sourceB, "work", 3, "release"), CancellationToken.None);
        Assert.Single(input.ReleasedChords);
    }

    [Fact]
    public void SourceCleanupRetainsFailedDebtAndContinuesOtherChords()
    {
        var input = new RecordingInputSender();
        var keys = new InjectedKeyStateOwner(input);
        var source = new InputSourceSession("stick-a", 1);
        var first = ParseChord("Ctrl+CapsLock");
        var second = ParseChord("Alt+Space");
        keys.Hold(new InjectedKeyHoldId(source, "work", 1, "push-to-talk"), first);
        keys.Hold(new InjectedKeyHoldId(source, "work", 2, "push-to-talk"), second);
        input.FailReleaseFor = "Ctrl+CapsLock";

        Assert.Throws<InvalidOperationException>(() => keys.ReleaseSource(source));

        Assert.Contains(input.ReleasedChords, chord => chord.NormalizedText == "Alt+Space");
        input.FailReleaseFor = null;
        keys.ReleaseSource(source);
        Assert.Equal(2, input.ReleasedChords.Count);
        Assert.Contains(input.ReleasedChords, chord => chord.NormalizedText == "Ctrl+CapsLock");
    }

    [Fact]
    public void PartialHoldDebtMustClearBeforeAnotherSourceCanOwnTheChord()
    {
        var input = new RecordingInputSender
        {
            FailHoldFor = "Ctrl+CapsLock",
            FailReleaseFor = "Ctrl+CapsLock",
        };
        var keys = new InjectedKeyStateOwner(input);
        var chord = ParseChord("Ctrl+CapsLock");
        var sourceA = new InputSourceSession("stick-a", 1);
        var sourceB = new InputSourceSession("stick-b", 1);
        var ownerA = new InjectedKeyHoldId(sourceA, "work", 1, "push-to-talk");
        var ownerB = new InjectedKeyHoldId(sourceB, "work", 1, "push-to-talk");

        Assert.Throws<InvalidOperationException>(() => keys.Hold(ownerA, chord));
        input.FailHoldFor = null;
        Assert.Throws<InvalidOperationException>(() => keys.Hold(ownerB, chord));
        Assert.Empty(input.HeldChords);

        input.FailReleaseFor = null;
        Assert.True(keys.Hold(ownerB, chord));
        var releaseCountAfterDebtResolved = input.ReleasedChords.Count;
        keys.ReleaseSource(sourceA);

        Assert.Equal(releaseCountAfterDebtResolved, input.ReleasedChords.Count);
        Assert.True(keys.Release(ownerB).KeysReleased);
        Assert.Equal(releaseCountAfterDebtResolved + 1, input.ReleasedChords.Count);
    }

    private static CodexActionExecutor CreateExecutor(
        RecordingInputSender input,
        InjectedKeyStateOwner keys) => new(
        new SafetyOptions { DryRun = false },
        _ => { },
        new FreshChordResolver(),
        new OpenWorkingDirectoryOptions(),
        foregroundGuard: new AllowedForegroundGuard(),
        inputSender: input,
        injectedKeyStateOwner: keys);

    private static ActionRequest Request(
        InputSourceSession source,
        string bank,
        int button,
        string trigger) => new(
        "mutable binding label",
        bank,
        button,
        trigger,
        CodexAction.PushToTalk,
        DateTimeOffset.UtcNow,
        DeviceId: source.SourceId,
        SourceGeneration: source.Generation);

    private static KeyChord ParseChord(string text)
    {
        Assert.True(KeySequenceParser.TryParse(text, true, out var sequence, out var error), error);
        return sequence!.Chords[0];
    }

    private static Task<bool> Route(RuntimeInputHost host, InputSourceSession source) =>
        host.RouteAsync(
            source,
            new JoystickSnapshot(DateTimeOffset.UtcNow, new bool[4], [-1], [0]),
            [],
            _ => Task.FromResult(true));

    private sealed class FreshChordResolver : ICodexKeybindingResolver
    {
        public Task<CodexBindingResolution> ResolveAsync(
            CodexAction action,
            CancellationToken cancellationToken)
        {
            Assert.True(
                KeySequenceParser.TryParse("Ctrl+CapsLock", true, out var sequence, out var error),
                error);
            return Task.FromResult(new CodexBindingResolution(
                action,
                "globalDictationHold",
                sequence,
                CodexBindingSource.User,
                CodexBindingSnapshotState.Current,
                null));
        }
    }

    private sealed class AllowedForegroundGuard : IForegroundProcessGuard
    {
        public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward) =>
            new(true, "Codex", "allowed");
    }

    private sealed class RecordingInputSender : IInputSender
    {
        public string? FailReleaseFor { get; set; }

        public string? FailHoldFor { get; set; }

        public List<KeyChord> HeldChords { get; } = [];

        public List<KeyChord> ReleasedChords { get; } = [];

        public Task SendSequenceAsync(KeySequence sequence, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendTextAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;

        public void HoldChord(KeyChord chord)
        {
            if (string.Equals(FailHoldFor, chord.NormalizedText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("simulated hold failure");
            }
            HeldChords.Add(chord);
        }

        public void ReleaseChord(KeyChord chord)
        {
            if (string.Equals(FailReleaseFor, chord.NormalizedText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("simulated release failure");
            }
            ReleasedChords.Add(chord);
        }

        public void SendMouseWheel(int delta)
        {
        }
    }
}
