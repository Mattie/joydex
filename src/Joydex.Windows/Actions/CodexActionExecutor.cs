using Joydex.Core.Config;
using Joydex.Core.Mapping;

namespace Joydex.Windows.Actions;

public sealed class CodexActionExecutor : IInjectedKeyStateLifecycle
{
    private static readonly TimeSpan ClipboardTimeout = TimeSpan.FromSeconds(2);
    private readonly SafetyOptions _safety;
    private readonly Action<string> _log;
    private readonly ICodexKeybindingResolver _keybindings;
    private readonly OpenWorkingDirectoryOptions _openWorkingDirectory;
    private readonly IForegroundProcessGuard _foregroundGuard;
    private readonly IInputSender _inputSender;
    private readonly IWorkingDirectoryClipboard _clipboard;
    private readonly WorkingDirectoryLauncherRegistry _launchers;
    private readonly Action<ActionRequest>? _internalAction;
    private readonly ICodexDictationControl _dictationControl;
    private readonly object _heldKeyLock = new();
    private readonly HashSet<(string Bank, int Button)> _heldPushToTalkControls = [];
    private KeyChord? _heldPushToTalkChord;
    private CodexBindingResolution? _heldPushToTalkResolution;
    private readonly object _dictationLock = new();
    private readonly Dictionary<(string DeviceId, string Bank, int Button), IntPtr> _dictationWindows =
        new(DictationOwnerEqualityComparer.Instance);

    public CodexActionExecutor(
        SafetyOptions safety,
        Action<string> log,
        ICodexKeybindingResolver keybindings,
        OpenWorkingDirectoryOptions openWorkingDirectory,
        IForegroundProcessGuard? foregroundGuard = null,
        IInputSender? inputSender = null,
        IWorkingDirectoryClipboard? clipboard = null,
        WorkingDirectoryLauncherRegistry? launchers = null,
        Action<ActionRequest>? internalAction = null,
        ICodexDictationControl? dictationControl = null)
    {
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _keybindings = keybindings ?? throw new ArgumentNullException(nameof(keybindings));
        _openWorkingDirectory = openWorkingDirectory ?? throw new ArgumentNullException(nameof(openWorkingDirectory));
        _foregroundGuard = foregroundGuard ?? new ForegroundProcessGuard();
        _inputSender = inputSender ?? new WindowsInputSender();
        _clipboard = clipboard ?? new WindowsWorkingDirectoryClipboard();
        _launchers = launchers ?? new WorkingDirectoryLauncherRegistry();
        _internalAction = internalAction;
        _dictationControl = dictationControl ?? new WindowsCodexDictationControl();
    }

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Action == CodexAction.ButtonMap)
        {
            return ExecuteInternalAction(request);
        }

        if (request.Action == CodexAction.InAppPushToTalk
            && string.Equals(request.Trigger, "release", StringComparison.OrdinalIgnoreCase))
        {
            return ReleaseInAppPushToTalk(request);
        }

        if (request.Action == CodexAction.PushToTalk
            && string.Equals(request.Trigger, "release", StringComparison.OrdinalIgnoreCase))
        {
            return ReleasePushToTalk(request);
        }

        CodexBindingResolution? resolution = null;
        try
        {
            var commandBacked = CodexCommandCatalog.TryGet(request.Action, out var descriptor);
            if (commandBacked)
            {
                resolution = new CodexBindingResolution(
                    request.Action,
                    descriptor.CommandId,
                    null,
                    CodexBindingSource.None,
                    CodexBindingSnapshotState.Unavailable,
                    "Binding resolution did not complete.");
                resolution = await _keybindings.ResolveAsync(request.Action, cancellationToken).ConfigureAwait(false);
            }
            var foreground = _foregroundGuard.Check(_safety, actionMayBringCodexForward: false);
            if (_safety.DryRun)
            {
                var blockers = new List<string>();
                if (!foreground.Allowed)
                {
                    blockers.Add(foreground.Reason);
                }

                if (resolution is { Resolved: false })
                {
                    blockers.Add(resolution.Error ?? "The Codex binding is unresolved.");
                }

                var safetyResult = blockers.Count == 0
                    ? foreground.Reason
                    : "LIVE MODE WOULD BLOCK: " + string.Join("; ", blockers);
                var simulated =
                    $"DRY RUN {DescribeRequest(request)}; {DescribeResolution(resolution)}; {safetyResult}";
                _log(simulated);
                return ActionExecutionResult.Simulated(simulated);
            }

            if (!foreground.Allowed)
            {
                return LogBlocked(request, resolution, foreground.Reason);
            }

            if (resolution is { Resolved: false })
            {
                return LogBlocked(request, resolution, resolution.Error ?? "The Codex binding is unresolved.");
            }

            ActionExecutionResult result;
            if (request.Action is CodexAction.InAppPushToTalk or CodexAction.Dictation)
            {
                result = StartDictation(request);
            }
            else if (request.Action == CodexAction.PushToTalk)
            {
                result = HoldPushToTalk(request, resolution!);
            }
            else if (request.Action == CodexAction.OpenWorkingDirectory)
            {
                result = await OpenWorkingDirectoryAsync(request, resolution!, cancellationToken).ConfigureAwait(false);
            }
            else if (resolution is not null)
            {
                await _inputSender.SendSequenceAsync(resolution.Sequence!, cancellationToken).ConfigureAwait(false);
                result = Success(request, resolution);
            }
            else if (RawInputCatalog.GetMouseWheelDelta(request.Action, request.WheelNotches) is { } wheelDelta)
            {
                _inputSender.SendMouseWheel(wheelDelta);
                result = Success(request, null);
            }
            else if (RawInputCatalog.TryGetKeySequence(request.Action, out var rawSequence))
            {
                await _inputSender.SendSequenceAsync(rawSequence, cancellationToken).ConfigureAwait(false);
                result = Success(request, null);
            }
            else
            {
                throw new InvalidOperationException($"Action '{request.Action}' has no execution behavior.");
            }

            if (result.Executed)
            {
                _log(result.Message);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log($"FAILED {DescribeRequest(request)}; {DescribeResolution(resolution)}; error={exception.Message}");
            throw;
        }
    }

    public void ClearInjectedKeyState()
    {
        var resolution = _keybindings
            .ResolveAsync(CodexAction.PushToTalk, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (!resolution.Resolved || resolution.Sequence!.Chords.Count != 1)
        {
            _log(
                $"BLOCKED startup push-to-talk cleanup; {DescribeResolution(resolution)}; "
                + "error=The current globalDictationHold binding is not one releasable chord.");
            return;
        }

        try
        {
            _inputSender.ReleaseChord(resolution.Sequence.Chords[0]);
            _log($"EXECUTED startup push-to-talk cleanup; {DescribeResolution(resolution)}");
        }
        catch (Exception exception)
        {
            _log($"FAILED startup push-to-talk cleanup; {DescribeResolution(resolution)}; error={exception.Message}");
            throw;
        }
    }

    public void ReleaseHeldKeys()
    {
        ReleasePushToTalkKeys(force: false);
        ReleaseAllDictationOwners();
    }

    private ActionExecutionResult ExecuteInternalAction(ActionRequest request)
    {
        if (_internalAction is null)
        {
            throw new InvalidOperationException("The button-map action needs an application callback.");
        }

        _internalAction(request);
        var message = $"EXECUTED {DescribeRequest(request)}; internal-action";
        _log(message);
        return ActionExecutionResult.Success(message);
    }

    private ActionExecutionResult StartDictation(ActionRequest request)
    {
        var owner = DictationOwner(request);
        if (request.Action == CodexAction.InAppPushToTalk)
        {
            lock (_dictationLock)
            {
                if (_dictationWindows.ContainsKey(owner))
                {
                    return Success(request, null, "accessibility-button=Dictate; hold-already-active");
                }
            }
        }

        var started = _dictationControl.Start();
        if (!started.Success)
        {
            return LogBlocked(request, null, started.Detail);
        }

        if (request.Action == CodexAction.InAppPushToTalk)
        {
            lock (_dictationLock)
            {
                _dictationWindows[owner] = started.WindowHandle;
            }
        }

        return Success(
            request,
            null,
            request.Action == CodexAction.InAppPushToTalk
                ? $"{started.Detail}; hold-started"
                : started.Detail);
    }

    private ActionExecutionResult ReleaseInAppPushToTalk(ActionRequest request)
    {
        if (_safety.DryRun)
        {
            var simulated = $"DRY RUN {DescribeRequest(request)}; accessibility-button=Stop dictation; release-only";
            _log(simulated);
            return ActionExecutionResult.Simulated(simulated);
        }

        var owner = DictationOwner(request);
        IntPtr windowHandle;
        lock (_dictationLock)
        {
            if (!_dictationWindows.TryGetValue(owner, out windowHandle))
            {
                return LogBlocked(request, null, "No in-app dictation hold was active for this control.");
            }
        }

        var stopped = _dictationControl.Stop(windowHandle);
        if (!stopped.Success)
        {
            return LogBlocked(request, null, stopped.Detail);
        }

        lock (_dictationLock)
        {
            if (_dictationWindows.TryGetValue(owner, out var current)
                && current == windowHandle)
            {
                _dictationWindows.Remove(owner);
            }
        }

        var result = Success(request, null, $"{stopped.Detail}; release");
        _log(result.Message);
        return result;
    }

    private void ReleaseAllDictationOwners()
    {
        KeyValuePair<(string DeviceId, string Bank, int Button), IntPtr>[] pending;
        lock (_dictationLock)
        {
            pending = _dictationWindows.ToArray();
        }

        foreach (var (owner, windowHandle) in pending)
        {
            var stopped = _dictationControl.Stop(windowHandle);
            if (!stopped.Success)
            {
                _log($"BLOCKED in-app dictation cleanup; error={stopped.Detail}");
                continue;
            }

            lock (_dictationLock)
            {
                if (_dictationWindows.TryGetValue(owner, out var current)
                    && current == windowHandle)
                {
                    _dictationWindows.Remove(owner);
                }
            }
            _log($"EXECUTED in-app dictation cleanup; {stopped.Detail}");
        }
    }

    private async Task<ActionExecutionResult> OpenWorkingDirectoryAsync(
        ActionRequest request,
        CodexBindingResolution resolution,
        CancellationToken cancellationToken)
    {
        var previousSequenceNumber = _clipboard.GetSequenceNumber();
        await _inputSender.SendSequenceAsync(resolution.Sequence!, cancellationToken).ConfigureAwait(false);
        var clipboardResult = await _clipboard
            .WaitForNewDirectoryAsync(previousSequenceNumber, ClipboardTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!clipboardResult.Success)
        {
            return LogBlocked(request, resolution, clipboardResult.Error ?? "Codex did not copy a working directory.");
        }

        var launchResult = _launchers.Launch(
            _openWorkingDirectory.Target,
            clipboardResult.DirectoryPath!);
        if (!launchResult.Success)
        {
            return LogBlocked(request, resolution, launchResult.Error ?? "The configured target could not be launched.");
        }

        return Success(request, resolution, $"target={_openWorkingDirectory.Target}");
    }

    private ActionExecutionResult HoldPushToTalk(
        ActionRequest request,
        CodexBindingResolution resolution)
    {
        if (resolution.Sequence!.Chords.Count != 1)
        {
            return LogBlocked(
                request,
                resolution,
                "Push-to-talk requires a single chord. Assign globalDictationHold a single chord in Settings > Keyboard Shortcuts.");
        }

        lock (_heldKeyLock)
        {
            var control = (request.Bank, request.Button);
            if (!_heldPushToTalkControls.Add(control) || _heldPushToTalkControls.Count > 1)
            {
                return Success(request, resolution, "hold-already-active");
            }

            var chord = resolution.Sequence.Chords[0];
            _heldPushToTalkChord = chord;
            _heldPushToTalkResolution = resolution;
            try
            {
                _inputSender.HoldChord(chord);
            }
            catch
            {
                try
                {
                    _inputSender.ReleaseChord(chord);
                    _heldPushToTalkControls.Clear();
                    _heldPushToTalkChord = null;
                    _heldPushToTalkResolution = null;
                }
                catch (Exception releaseException)
                {
                    _log($"Could not clean up a partial push-to-talk chord: {releaseException.Message}");
                }
                throw;
            }
        }

        return Success(request, resolution, "hold-started");
    }

    private ActionExecutionResult ReleasePushToTalk(ActionRequest request)
    {
        CodexBindingResolution? resolution = null;
        if (_safety.DryRun)
        {
            resolution = ResolvePushToTalkForDiagnostics();
            var simulated = $"DRY RUN {DescribeRequest(request)}; {DescribeResolution(resolution)}; release-only";
            _log(simulated);
            return ActionExecutionResult.Simulated(simulated);
        }

        try
        {
            lock (_heldKeyLock)
            {
                resolution = _heldPushToTalkResolution;
            }

            var released = ReleasePushToTalkControl(request);
            resolution = released.Resolution ?? ResolvePushToTalkForDiagnostics();
            if (!released.ControlWasHeld)
            {
                return LogBlocked(request, resolution, "No push-to-talk chord was held for this control.");
            }

            var result = Success(
                request,
                resolution,
                released.KeysReleased ? "release" : "hold-remains-active");
            _log(result.Message);
            return result;
        }
        catch (Exception exception)
        {
            _log($"FAILED {DescribeRequest(request)}; {DescribeResolution(resolution)}; error={exception.Message}");
            throw;
        }
    }

    private PushToTalkRelease ReleasePushToTalkControl(ActionRequest request)
    {
        lock (_heldKeyLock)
        {
            var control = (request.Bank, request.Button);
            if (!_heldPushToTalkControls.Contains(control))
            {
                return new(false, false, null);
            }

            var resolution = _heldPushToTalkResolution;
            if (_heldPushToTalkControls.Count > 1)
            {
                _heldPushToTalkControls.Remove(control);
                return new(true, false, resolution);
            }

            if (_heldPushToTalkChord is not null)
            {
                _inputSender.ReleaseChord(_heldPushToTalkChord);
            }

            _heldPushToTalkChord = null;
            _heldPushToTalkResolution = null;
            _heldPushToTalkControls.Remove(control);
            return new(true, true, resolution);
        }
    }

    private CodexBindingResolution ResolvePushToTalkForDiagnostics()
    {
        try
        {
            return _keybindings
                .ResolveAsync(CodexAction.PushToTalk, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            CodexCommandCatalog.TryGet(CodexAction.PushToTalk, out var descriptor);
            return new CodexBindingResolution(
                CodexAction.PushToTalk,
                descriptor?.CommandId ?? "globalDictationHold",
                null,
                CodexBindingSource.None,
                CodexBindingSnapshotState.Unavailable,
                exception.Message);
        }
    }

    private void ReleasePushToTalkKeys(bool force)
    {
        lock (_heldKeyLock)
        {
            if (!force && _heldPushToTalkControls.Count == 0 && _heldPushToTalkChord is null)
            {
                return;
            }

            if (_heldPushToTalkChord is not null)
            {
                _inputSender.ReleaseChord(_heldPushToTalkChord);
            }

            _heldPushToTalkChord = null;
            _heldPushToTalkResolution = null;
            _heldPushToTalkControls.Clear();
        }
    }

    private static (string DeviceId, string Bank, int Button) DictationOwner(ActionRequest request) =>
        (request.DeviceId, request.Bank, request.Button);

    private sealed class DictationOwnerEqualityComparer
        : IEqualityComparer<(string DeviceId, string Bank, int Button)>
    {
        public static DictationOwnerEqualityComparer Instance { get; } = new();

        public bool Equals(
            (string DeviceId, string Bank, int Button) x,
            (string DeviceId, string Bank, int Button) y) =>
            x.Button == y.Button
            && string.Equals(x.DeviceId, y.DeviceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Bank, y.Bank, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string DeviceId, string Bank, int Button) owner) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(owner.DeviceId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(owner.Bank),
                owner.Button);
    }

    private readonly record struct PushToTalkRelease(
        bool ControlWasHeld,
        bool KeysReleased,
        CodexBindingResolution? Resolution);

    private ActionExecutionResult LogBlocked(
        ActionRequest request,
        CodexBindingResolution? resolution,
        string reason)
    {
        var message = $"BLOCKED {DescribeRequest(request)}; {DescribeResolution(resolution)}; error={reason}";
        _log(message);
        return ActionExecutionResult.Blocked(message);
    }

    private static ActionExecutionResult Success(
        ActionRequest request,
        CodexBindingResolution? resolution,
        string? detail = null)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"; {detail}";
        var message = $"EXECUTED {DescribeRequest(request)}; {DescribeResolution(resolution)}{suffix}";
        return ActionExecutionResult.Success(message);
    }

    private static string DescribeRequest(ActionRequest request) =>
        $"{CodexActionCatalog.GetId(request.Action)} {request.Trigger} from {request.Bank}/button {request.Button}";

    private static string DescribeResolution(CodexBindingResolution? resolution)
    {
        if (resolution is null)
        {
            return "raw-input";
        }

        var binding = resolution.Sequence?.NormalizedText ?? "<unresolved>";
        var source = resolution.Source.ToString().ToLowerInvariant();
        var snapshot = resolution.SnapshotState switch
        {
            CodexBindingSnapshotState.Current => "current",
            CodexBindingSnapshotState.LastKnownGood => "last-known-good",
            _ => "unavailable",
        };
        return $"command={resolution.CommandId}; binding={binding}; source={source}; snapshot={snapshot}";
    }
}
