using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Joydex.Windows.Actions;

/// <summary>Sends complete sequences or held chords through the Windows input queue.</summary>
public interface IInputSender
{
    Task SendSequenceAsync(KeySequence sequence, CancellationToken cancellationToken);

    Task SendTextAsync(string text, CancellationToken cancellationToken);

    void HoldChord(KeyChord chord);

    void ReleaseChord(KeyChord chord);

    void SendMouseWheel(int delta);
}

internal enum WindowsInputEventKind
{
    KeyDown,
    KeyUp,
    MouseWheel,
    UnicodeKeyDown,
    UnicodeKeyUp,
}

internal sealed record WindowsInputEvent(
    WindowsInputEventKind Kind,
    ushort VirtualKey = 0,
    ushort ScanCode = 0,
    int WheelDelta = 0,
    bool ExtendedKey = false);

internal interface IWindowsInputSink
{
    void Send(IReadOnlyList<WindowsInputEvent> events);
}

internal interface IWindowsInputDelay
{
    Task DelayAsync(int milliseconds, CancellationToken cancellationToken);
}

public sealed class WindowsInputSender : IInputSender
{
    private const int SequenceStepDelayMs = 50;
    private readonly IWindowsInputSink _sink;
    private readonly IWindowsInputDelay _delay;

    public WindowsInputSender()
        : this(new NativeWindowsInputSink(), new SystemWindowsInputDelay())
    {
    }

    internal WindowsInputSender(IWindowsInputSink sink, IWindowsInputDelay delay)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public async Task SendSequenceAsync(KeySequence sequence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        for (var index = 0; index < sequence.Chords.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendChord(sequence.Chords[index]);
            if (index < sequence.Chords.Count - 1)
            {
                await _delay.DelayAsync(SequenceStepDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var segments = normalized.Split('\n');
        for (var index = 0; index < segments.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendUnicode(segments[index], cancellationToken);
            if (index < segments.Length - 1)
            {
                SendChord(new KeyChord([VirtualKey.Shift, VirtualKey.Enter], "Shift+Enter"));
            }
        }

        return Task.CompletedTask;
    }

    public void HoldChord(KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        _sink.Send(chord.VirtualKeys.Select(key => KeyEvent(key, WindowsInputEventKind.KeyDown)).ToArray());
    }

    public void ReleaseChord(KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        _sink.Send(chord.VirtualKeys.Reverse().Select(key => KeyEvent(key, WindowsInputEventKind.KeyUp)).ToArray());
    }

    public void SendMouseWheel(int delta) =>
        _sink.Send([new WindowsInputEvent(WindowsInputEventKind.MouseWheel, WheelDelta: delta)]);

    private void SendChord(KeyChord chord)
    {
        var events = chord.VirtualKeys
            .Select(key => KeyEvent(key, WindowsInputEventKind.KeyDown))
            .Concat(chord.VirtualKeys.Reverse().Select(key => KeyEvent(key, WindowsInputEventKind.KeyUp)))
            .ToArray();
        _sink.Send(events);
    }

    private void SendUnicode(string text, CancellationToken cancellationToken)
    {
        const int chunkSize = 256;
        for (var offset = 0; offset < text.Length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(chunkSize, text.Length - offset);
            var events = new List<WindowsInputEvent>(length * 2);
            for (var index = offset; index < offset + length; index++)
            {
                var scanCode = text[index];
                events.Add(new WindowsInputEvent(WindowsInputEventKind.UnicodeKeyDown, ScanCode: scanCode));
                events.Add(new WindowsInputEvent(WindowsInputEventKind.UnicodeKeyUp, ScanCode: scanCode));
            }

            if (events.Count > 0)
            {
                _sink.Send(events);
            }
        }
    }

    private static WindowsInputEvent KeyEvent(ushort key, WindowsInputEventKind kind) =>
        new(kind, key, ExtendedKey: IsExtendedKey(key));

    private static bool IsExtendedKey(ushort key) => key is
        VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.End or VirtualKey.Home
        or VirtualKey.Left or VirtualKey.Up or VirtualKey.Right or VirtualKey.Down
        or VirtualKey.Insert or VirtualKey.Delete or VirtualKey.Divide or VirtualKey.LeftWindows;
}

internal sealed class SystemWindowsInputDelay : IWindowsInputDelay
{
    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        Task.Delay(milliseconds, cancellationToken);
}

internal sealed partial class NativeWindowsInputSink : IWindowsInputSink
{
    public void Send(IReadOnlyList<WindowsInputEvent> events)
    {
        var inputs = events.Select(ToNativeInput).ToArray();
        var inputSize = Marshal.SizeOf<Input>();
        var sent = SendInput((uint)inputs.Length, inputs, inputSize);
        if (sent != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                $"Windows accepted {sent} of {inputs.Length} input events (INPUT size {inputSize}, Win32 error {error}).");
        }
    }

    private static Input ToNativeInput(WindowsInputEvent inputEvent) => inputEvent.Kind switch
    {
        WindowsInputEventKind.KeyDown => Input.Key(inputEvent.VirtualKey, keyUp: false, inputEvent.ExtendedKey),
        WindowsInputEventKind.KeyUp => Input.Key(inputEvent.VirtualKey, keyUp: true, inputEvent.ExtendedKey),
        WindowsInputEventKind.MouseWheel => Input.MouseWheel(inputEvent.WheelDelta),
        WindowsInputEventKind.UnicodeKeyDown => Input.Unicode(inputEvent.ScanCode, keyUp: false),
        WindowsInputEventKind.UnicodeKeyUp => Input.Unicode(inputEvent.ScanCode, keyUp: true),
        _ => throw new InvalidOperationException($"Unknown Windows input event kind '{inputEvent.Kind}'."),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;

        public static Input Key(ushort virtualKey, bool keyUp, bool extendedKey) => new()
        {
            Type = 1,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = (keyUp ? 0x0002u : 0u) | (extendedKey ? 0x0001u : 0u),
                },
            },
        };

        public static Input MouseWheel(int delta) => new()
        {
            Type = 0,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    MouseData = unchecked((uint)delta),
                    Flags = 0x0800,
                },
            },
        };

        public static Input Unicode(ushort scanCode, bool keyUp) => new()
        {
            Type = 1,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    ScanCode = scanCode,
                    Flags = 0x0004u | (keyUp ? 0x0002u : 0u),
                },
            },
        };
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);
}
