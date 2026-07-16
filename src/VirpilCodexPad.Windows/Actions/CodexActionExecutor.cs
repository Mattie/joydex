using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VirpilCodexPad.Core.Config;
using VirpilCodexPad.Core.Mapping;

namespace VirpilCodexPad.Windows.Actions;

public sealed partial class CodexActionExecutor(
    SafetyOptions safety,
    Action<string> log,
    IForegroundProcessGuard? foregroundGuard = null,
    Action<ActionRequest>? internalAction = null)
{
    private readonly IForegroundProcessGuard _foregroundGuard = foregroundGuard ?? new ForegroundProcessGuard();
    private readonly object _heldKeyLock = new();
    private readonly HashSet<(string Bank, int Button)> _heldPushToTalkControls = [];

    public Task<ActionExecutionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Action == CodexAction.ButtonMap)
        {
            if (internalAction is null)
            {
                throw new InvalidOperationException("The button-map action needs an application callback.");
            }

            internalAction(request);
            var internalMessage = $"EXECUTED button-map {request.Trigger} from {request.Bank}/button {request.Button}.";
            log(internalMessage);
            return Task.FromResult(ActionExecutionResult.Success(internalMessage));
        }

        if (request.Action == CodexAction.PushToTalk
            && string.Equals(request.Trigger, "release", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ReleasePushToTalk(request));
        }

        var deepLink = GetDeepLink(request.Action);
        var wheelDelta = GetMouseWheelDelta(request.Action, request.WheelNotches);
        var foreground = _foregroundGuard.Check(safety, deepLink is not null);
        if (safety.DryRun)
        {
            var safetyResult = foreground.Allowed
                ? foreground.Reason
                : $"LIVE MODE WOULD BLOCK: {foreground.Reason}";
            var simulated = $"DRY RUN {CodexActionCatalog.GetId(request.Action)} {request.Trigger} from {request.Bank}/button {request.Button}; {safetyResult}";
            log(simulated);
            return Task.FromResult(ActionExecutionResult.Simulated(simulated));
        }

        if (!foreground.Allowed)
        {
            var blocked = $"BLOCKED {CodexActionCatalog.GetId(request.Action)} {request.Trigger} from {request.Bank}/button {request.Button}: {foreground.Reason}";
            log(blocked);
            return Task.FromResult(ActionExecutionResult.Blocked(blocked));
        }

        if (request.Action == CodexAction.PushToTalk)
        {
            HoldPushToTalk(request);
        }
        else if (wheelDelta is not null)
        {
            SendMouseWheel(wheelDelta.Value);
        }
        else if (deepLink is not null)
        {
            Process.Start(new ProcessStartInfo(deepLink) { UseShellExecute = true });
        }
        else
        {
            SendChord(GetShortcut(request.Action));
        }

        var message = $"EXECUTED {CodexActionCatalog.GetId(request.Action)} {request.Trigger} from {request.Bank}/button {request.Button}.";
        log(message);
        return Task.FromResult(ActionExecutionResult.Success(message));
    }

    public void ClearInjectedKeyState() => ReleasePushToTalkKeys(force: true);

    public void ReleaseHeldKeys() => ReleasePushToTalkKeys(force: false);

    private void ReleasePushToTalkKeys(bool force)
    {
        lock (_heldKeyLock)
        {
            if (!force && _heldPushToTalkControls.Count == 0)
            {
                return;
            }

            SendInputs(
                Input.Key(VirtualKey.CapsLock, keyUp: true),
                Input.Key(VirtualKey.Control, keyUp: true));
            _heldPushToTalkControls.Clear();
        }
    }

    private static string? GetDeepLink(CodexAction action) => action switch
    {
        CodexAction.NewTask => "codex://threads/new",
        CodexAction.OpenSkills => "codex://skills",
        _ => null,
    };

    private static int? GetMouseWheelDelta(CodexAction action, int wheelNotches) => action switch
    {
        CodexAction.ScrollUp => checked(120 * wheelNotches),
        CodexAction.ScrollDown => checked(-120 * wheelNotches),
        _ => null,
    };

    private static ushort[] GetShortcut(CodexAction action) => action switch
    {
        CodexAction.Agent1 => ModifiedFunctionKey(VirtualKey.F1),
        CodexAction.Agent2 => ModifiedFunctionKey(VirtualKey.F2),
        CodexAction.Agent3 => ModifiedFunctionKey(VirtualKey.F3),
        CodexAction.Agent4 => ModifiedFunctionKey(VirtualKey.F4),
        CodexAction.Agent5 => ModifiedFunctionKey(VirtualKey.F5),
        CodexAction.Agent6 => ModifiedFunctionKey(VirtualKey.F6),
        CodexAction.ToggleFastMode => ModifiedFunctionKey(VirtualKey.F7),
        CodexAction.Approve => ModifiedFunctionKey(VirtualKey.F8),
        CodexAction.Reject => ModifiedFunctionKey(VirtualKey.F9),
        CodexAction.ForkTask => ModifiedFunctionKey(VirtualKey.F10),
        CodexAction.Submit => ModifiedFunctionKey(VirtualKey.F11),
        CodexAction.TogglePlanMode => ModifiedFunctionKey(VirtualKey.F12),
        CodexAction.IncreaseReasoning => [VirtualKey.Control, VirtualKey.Alt, VirtualKey.PageUp],
        CodexAction.DecreaseReasoning => [VirtualKey.Control, VirtualKey.Alt, VirtualKey.PageDown],
        CodexAction.Home => [VirtualKey.Home],
        CodexAction.End => [VirtualKey.End],
        CodexAction.PreviousTask => [VirtualKey.Control, VirtualKey.Shift, VirtualKey.LeftBracket],
        CodexAction.NextTask => [VirtualKey.Control, VirtualKey.Shift, VirtualKey.RightBracket],
        CodexAction.NavigateBack => [VirtualKey.Control, VirtualKey.LeftBracket],
        CodexAction.NavigateForward => [VirtualKey.Control, VirtualKey.RightBracket],
        CodexAction.ToggleSidebar => [VirtualKey.Control, VirtualKey.B],
        CodexAction.Dictation => [VirtualKey.Control, VirtualKey.Shift, VirtualKey.D],
        _ => throw new InvalidOperationException($"Action '{action}' has no keyboard shortcut."),
    };

    private static ushort[] ModifiedFunctionKey(ushort functionKey) =>
        [VirtualKey.Control, VirtualKey.Alt, VirtualKey.Shift, functionKey];

    private void HoldPushToTalk(ActionRequest request)
    {
        lock (_heldKeyLock)
        {
            var control = (request.Bank, request.Button);
            if (!_heldPushToTalkControls.Add(control)
                || _heldPushToTalkControls.Count > 1)
            {
                return;
            }

            try
            {
                SendInputs(
                    Input.Key(VirtualKey.Control, keyUp: false),
                    Input.Key(VirtualKey.CapsLock, keyUp: false));
            }
            catch
            {
                try
                {
                    ReleaseHeldKeys();
                }
                catch (Exception releaseException)
                {
                    log($"Could not clean up a partial push-to-talk chord: {releaseException.Message}");
                }

                throw;
            }
        }
    }

    private ActionExecutionResult ReleasePushToTalk(ActionRequest request)
    {
        if (safety.DryRun)
        {
            var simulated = $"DRY RUN push-to-talk release from {request.Bank}/button {request.Button}.";
            log(simulated);
            return ActionExecutionResult.Simulated(simulated);
        }

        ReleasePushToTalkControl(request);
        var message = $"EXECUTED push-to-talk release from {request.Bank}/button {request.Button}.";
        log(message);
        return ActionExecutionResult.Success(message);
    }

    private void ReleasePushToTalkControl(ActionRequest request)
    {
        lock (_heldKeyLock)
        {
            var control = (request.Bank, request.Button);
            if (!_heldPushToTalkControls.Contains(control))
            {
                return;
            }

            if (_heldPushToTalkControls.Count > 1)
            {
                _heldPushToTalkControls.Remove(control);
                return;
            }

            SendInputs(
                Input.Key(VirtualKey.CapsLock, keyUp: true),
                Input.Key(VirtualKey.Control, keyUp: true));
            _heldPushToTalkControls.Remove(control);
        }
    }

    private static void SendChord(IReadOnlyList<ushort> keys)
    {
        var inputs = new Input[keys.Count * 2];
        for (var index = 0; index < keys.Count; index++)
        {
            inputs[index] = Input.Key(keys[index], keyUp: false);
            inputs[inputs.Length - index - 1] = Input.Key(keys[index], keyUp: true);
        }

        SendInputs(inputs);
    }

    private static void SendMouseWheel(int delta) => SendInputs(Input.MouseWheel(delta));

    private static void SendInputs(params Input[] inputs)
    {
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

    private static class VirtualKey
    {
        public const ushort Shift = 0x10;
        public const ushort Control = 0x11;
        public const ushort Alt = 0x12;
        public const ushort CapsLock = 0x14;
        public const ushort PageUp = 0x21;
        public const ushort PageDown = 0x22;
        public const ushort End = 0x23;
        public const ushort Home = 0x24;
        public const ushort B = 0x42;
        public const ushort D = 0x44;
        public const ushort F1 = 0x70;
        public const ushort F2 = 0x71;
        public const ushort F3 = 0x72;
        public const ushort F4 = 0x73;
        public const ushort F5 = 0x74;
        public const ushort F6 = 0x75;
        public const ushort F7 = 0x76;
        public const ushort F8 = 0x77;
        public const ushort F9 = 0x78;
        public const ushort F10 = 0x79;
        public const ushort F11 = 0x7A;
        public const ushort F12 = 0x7B;
        public const ushort LeftBracket = 0xDB;
        public const ushort RightBracket = 0xDD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;

        public static Input Key(ushort virtualKey, bool keyUp) => new()
        {
            Type = 1,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? 0x0002u : 0u,
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
