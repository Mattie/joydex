using Joydex.Core.Config;
using Joydex.Core.Input;
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
    private readonly InjectedKeyStateOwner _injectedKeyState;
    private readonly object _heldKeyLock = new();
    private readonly Dictionary<InjectedKeyHoldId, CodexBindingResolution> _heldPushToTalkResolutions = [];
    private readonly ICodexDictationControl _dictationControl;
    private readonly object _dictationLock = new();
    private readonly Dictionary<DictationHoldId, IntPtr> _dictationWindows =
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
        InjectedKeyStateOwner? injectedKeyStateOwner = null,
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
        _injectedKeyState = injectedKeyStateOwner ?? new InjectedKeyStateOwner(_inputSender);
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
            cancellationToken.ThrowIfCancellationRequested();
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
                result = StartDictation(request, foreground.WindowHandle);
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
            var released = _injectedKeyState.ClearStaleChord(resolution.Sequence.Chords[0]);
            if (released)
            {
                _log($"EXECUTED startup push-to-talk cleanup; {DescribeResolution(resolution)}");
            }
        }
        catch (Exception exception)
        {
            _log($"FAILED startup push-to-talk cleanup; {DescribeResolution(resolution)}; error={exception.Message}");
            throw;
        }
    }

    public void ReleaseHeldKeys(InputSourceSession source)
    {
        _injectedKeyState.ReleaseSource(source);
        lock (_heldKeyLock)
        {
            foreach (var owner in _heldPushToTalkResolutions.Keys
                         .Where(owner => SourceMatches(owner.Source, source))
                         .ToArray())
            {
                _heldPushToTalkResolutions.Remove(owner);
            }
        }

        if (!ReleaseDictationOwners(source))
        {
            throw new InvalidOperationException("One or more in-app dictation holds could not be stopped.");
        }
    }

    public void ReleaseAllHeldKeys()
    {
        _ = ReleaseHeldKeys();
    }

    // Retained as a convenience for direct executor owners and existing cleanup callers.
    public bool ReleaseHeldKeys()
    {
        _injectedKeyState.ReleaseAll();
        lock (_heldKeyLock)
        {
            _heldPushToTalkResolutions.Clear();
        }

        return ReleaseDictationOwners();
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

    private ActionExecutionResult StartDictation(ActionRequest request, IntPtr windowHandle)
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

        var started = _dictationControl.Start(windowHandle);
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

    private bool ReleaseDictationOwners(InputSourceSession? source = null)
    {
        KeyValuePair<DictationHoldId, IntPtr>[] pending;
        lock (_dictationLock)
        {
            pending = _dictationWindows
                .Where(entry => source is null || SourceMatches(entry.Key.Source, source.Value))
                .ToArray();
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

        lock (_dictationLock)
        {
            return source is null
                ? _dictationWindows.Count == 0
                : !_dictationWindows.Keys.Any(owner => SourceMatches(owner.Source, source.Value));
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
            var chord = resolution.Sequence.Chords[0];
            var owner = PushToTalkOwner(request);
            var added = _injectedKeyState.Hold(owner, chord);
            _heldPushToTalkResolutions[owner] = resolution;
            if (!added)
            {
                return Success(request, resolution, "hold-already-active");
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
                _heldPushToTalkResolutions.TryGetValue(PushToTalkOwner(request), out resolution);
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
            var owner = PushToTalkOwner(request);
            _heldPushToTalkResolutions.TryGetValue(owner, out var resolution);
            var released = _injectedKeyState.Release(owner);
            if (released.WasHeld)
            {
                _heldPushToTalkResolutions.Remove(owner);
            }
            return new(released.WasHeld, released.KeysReleased, resolution);
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

    private static DictationHoldId DictationOwner(ActionRequest request) => new(
        new InputSourceSession(request.DeviceId, request.SourceGeneration),
        request.Bank,
        request.Button);

    private readonly record struct DictationHoldId(InputSourceSession Source, string Bank, int Button);

    private sealed class DictationOwnerEqualityComparer
        : IEqualityComparer<DictationHoldId>
    {
        public static DictationOwnerEqualityComparer Instance { get; } = new();

        public bool Equals(DictationHoldId x, DictationHoldId y) =>
            x.Button == y.Button
            && x.Source.Generation == y.Source.Generation
            && string.Equals(x.Source.SourceId, y.Source.SourceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Bank, y.Bank, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(DictationHoldId owner) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(owner.Source.SourceId),
                owner.Source.Generation,
                StringComparer.OrdinalIgnoreCase.GetHashCode(owner.Bank),
                owner.Button);
    }
    private readonly record struct PushToTalkRelease(
        bool ControlWasHeld,
        bool KeysReleased,
        CodexBindingResolution? Resolution);

    private static InjectedKeyHoldId PushToTalkOwner(ActionRequest request) => new(
        new InputSourceSession(request.DeviceId, request.SourceGeneration),
        request.Bank,
        request.Button,
        CodexActionCatalog.GetId(CodexAction.PushToTalk));

    private static bool SourceMatches(InputSourceSession left, InputSourceSession right) =>
        left.Generation == right.Generation
        && string.Equals(left.SourceId, right.SourceId, StringComparison.OrdinalIgnoreCase);

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
