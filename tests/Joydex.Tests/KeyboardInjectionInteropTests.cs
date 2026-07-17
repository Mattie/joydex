using System.Reflection;
using System.Runtime.InteropServices;
using Joydex.Windows.Actions;

namespace Joydex.Tests;

public sealed class KeyboardInjectionInteropTests
{
    [Fact]
    public void InputStructureMatchesTheWindowsAbi()
    {
        var inputType = typeof(NativeWindowsInputSink).GetNestedType("Input", BindingFlags.NonPublic);
        var unionType = typeof(NativeWindowsInputSink).GetNestedType("InputUnion", BindingFlags.NonPublic);

        Assert.NotNull(inputType);
        Assert.NotNull(unionType);
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf(inputType));
        Assert.Equal(IntPtr.Size == 8 ? 32 : 24, Marshal.SizeOf(unionType));
    }

    [Fact]
    public void ParserSupportsOrdinaryAndMultiStepSequences()
    {
        var parsed = KeySequenceParser.TryParse(
            "Ctrl+K Ctrl+Shift+F24",
            allowBareModifiers: false,
            out var sequence,
            out var error);

        Assert.True(parsed, error);
        Assert.Equal("Ctrl+K Ctrl+Shift+F24", sequence!.NormalizedText);
        Assert.Equal(2, sequence.Chords.Count);
    }

    [Fact]
    public void ParserAllowsBareModifiersOnlyWhenRequested()
    {
        Assert.False(KeySequenceParser.TryParse("Ctrl", false, out _, out _));

        Assert.True(KeySequenceParser.TryParse("Ctrl", true, out var sequence, out var error), error);
        Assert.Equal("Ctrl", sequence!.NormalizedText);
    }

    [Fact]
    public void ParserRejectsNonKeyboardBindings()
    {
        Assert.False(KeySequenceParser.TryParse("MouseBack", false, out _, out var error));
        Assert.Contains("cannot be sent", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("+", "Shift+Plus")]
    [InlineData("Ctrl++", "Ctrl+Shift+Plus")]
    [InlineData("Ctrl+?", "Ctrl+Shift+Slash")]
    [InlineData("Ctrl+!", "Ctrl+Shift+1")]
    public void ParserSupportsThePlusPunctuationKey(string binding, string normalized)
    {
        Assert.True(KeySequenceParser.TryParse(binding, false, out var sequence, out var error), error);
        Assert.Equal(normalized, sequence!.NormalizedText);
    }

    [Fact]
    public async Task MultiStepSenderReleasesEachChordBeforeWaitingForTheNext()
    {
        Assert.True(KeySequenceParser.TryParse(
            "Ctrl+K Alt+Enter",
            false,
            out var sequence,
            out var error), error);
        var sink = new RecordingSink();
        var delay = new RecordingDelay();
        var sender = new WindowsInputSender(sink, delay);

        await sender.SendSequenceAsync(sequence!, CancellationToken.None);

        Assert.Equal(2, sink.Batches.Count);
        Assert.Collection(
            sink.Batches[0],
            input => AssertEvent(input, WindowsInputEventKind.KeyDown, 0x11),
            input => AssertEvent(input, WindowsInputEventKind.KeyDown, 0x4B),
            input => AssertEvent(input, WindowsInputEventKind.KeyUp, 0x4B),
            input => AssertEvent(input, WindowsInputEventKind.KeyUp, 0x11));
        Assert.Collection(
            sink.Batches[1],
            input => AssertEvent(input, WindowsInputEventKind.KeyDown, 0x12),
            input => AssertEvent(input, WindowsInputEventKind.KeyDown, 0x0D),
            input => AssertEvent(input, WindowsInputEventKind.KeyUp, 0x0D),
            input => AssertEvent(input, WindowsInputEventKind.KeyUp, 0x12));
        Assert.Equal([50], delay.Delays);
    }

    [Fact]
    public async Task NavigationKeysAreMarkedAsExtendedWindowsKeys()
    {
        Assert.True(KeySequenceParser.TryParse("Home", false, out var sequence, out var error), error);
        var sink = new RecordingSink();
        var sender = new WindowsInputSender(sink, new RecordingDelay());

        await sender.SendSequenceAsync(sequence!, CancellationToken.None);

        Assert.All(Assert.Single(sink.Batches), input => Assert.True(input.ExtendedKey));
    }

    [Fact]
    public async Task WindowsModifierIsMarkedExtendedWhileItsPrimaryKeyIsNot()
    {
        Assert.True(KeySequenceParser.TryParse("Win+K", false, out var sequence, out var error), error);
        var sink = new RecordingSink();
        var sender = new WindowsInputSender(sink, new RecordingDelay());

        await sender.SendSequenceAsync(sequence!, CancellationToken.None);

        var events = Assert.Single(sink.Batches);
        Assert.True(events[0].ExtendedKey);
        Assert.False(events[1].ExtendedKey);
        Assert.False(events[2].ExtendedKey);
        Assert.True(events[3].ExtendedKey);
    }

    [Fact]
    public void MouseWheelInputUsesTheWindowsWheelFlagAndSignedDelta()
    {
        var inputType = typeof(NativeWindowsInputSink).GetNestedType("Input", BindingFlags.NonPublic);
        var unionType = typeof(NativeWindowsInputSink).GetNestedType("InputUnion", BindingFlags.NonPublic);
        var mouseType = typeof(NativeWindowsInputSink).GetNestedType("MouseInput", BindingFlags.NonPublic);

        Assert.NotNull(inputType);
        Assert.NotNull(unionType);
        Assert.NotNull(mouseType);

        var factory = inputType.GetMethod("MouseWheel", BindingFlags.Public | BindingFlags.Static);
        var input = factory?.Invoke(null, [-120]);
        var data = inputType.GetField("Data")?.GetValue(input);
        var mouse = unionType.GetField("Mouse")?.GetValue(data);

        Assert.NotNull(input);
        Assert.NotNull(data);
        Assert.NotNull(mouse);
        Assert.Equal(0u, Assert.IsType<uint>(inputType.GetField("Type")?.GetValue(input)));
        Assert.Equal(unchecked((uint)-120), Assert.IsType<uint>(mouseType.GetField("MouseData")?.GetValue(mouse)));
        Assert.Equal(0x0800u, Assert.IsType<uint>(mouseType.GetField("Flags")?.GetValue(mouse)));
    }

    private static void AssertEvent(WindowsInputEvent input, WindowsInputEventKind kind, ushort virtualKey)
    {
        Assert.Equal(kind, input.Kind);
        Assert.Equal(virtualKey, input.VirtualKey);
    }

    private sealed class RecordingSink : IWindowsInputSink
    {
        public List<WindowsInputEvent[]> Batches { get; } = [];

        public void Send(IReadOnlyList<WindowsInputEvent> events) => Batches.Add(events.ToArray());
    }

    private sealed class RecordingDelay : IWindowsInputDelay
    {
        public List<int> Delays { get; } = [];

        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            Delays.Add(milliseconds);
            return Task.CompletedTask;
        }
    }
}
