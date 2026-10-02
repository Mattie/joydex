using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Windows.Actions;

namespace Joydex.Tests;

public sealed class CodexActionExecutorTests
{
    public static TheoryData<CodexAction, string> CommandMappings => new()
    {
        { CodexAction.Agent1, "thread1" },
        { CodexAction.Agent2, "thread2" },
        { CodexAction.Agent3, "thread3" },
        { CodexAction.Agent4, "thread4" },
        { CodexAction.Agent5, "thread5" },
        { CodexAction.Agent6, "thread6" },
        { CodexAction.ToggleFastMode, "composer.toggleFastMode" },
        { CodexAction.Approve, "approval.approve" },
        { CodexAction.Reject, "approval.decline" },
        { CodexAction.ForkTask, "forkThread" },
        { CodexAction.PushToTalk, "globalDictationHold" },
        { CodexAction.Submit, "composer.submit" },
        { CodexAction.TogglePlanMode, "composer.togglePlanMode" },
        { CodexAction.IncreaseReasoning, "composer.increaseReasoningEffort" },
        { CodexAction.DecreaseReasoning, "composer.decreaseReasoningEffort" },
        { CodexAction.ArchiveChat, "archiveThread" },
        { CodexAction.NewTask, "newTask" },
        { CodexAction.SideConversation, "openSideChat" },
        { CodexAction.PreviousTask, "previousThread" },
        { CodexAction.NextTask, "nextThread" },
        { CodexAction.NavigateBack, "navigateBack" },
        { CodexAction.NavigateForward, "navigateForward" },
        { CodexAction.ToggleSidebar, "toggleSidebar" },
        { CodexAction.ToggleReviewPanel, "toggleSidePanel" },
        { CodexAction.OpenSkills, "openSkills" },
        { CodexAction.StartVoiceChat, "composer.startVoiceMode" },
        { CodexAction.EndVoiceChat, "realtimeVoice.endCall" },
        { CodexAction.ToggleVoiceChatMicrophone, "realtimeVoice.toggleMicrophoneMute" },
        { CodexAction.OpenWorkingDirectory, "copyWorkingDirectory" },
    };

    public static TheoryData<CodexAction> OrdinaryCommandActions => new()
    {
        CodexAction.Agent1,
        CodexAction.Agent2,
        CodexAction.Agent3,
        CodexAction.Agent4,
        CodexAction.Agent5,
        CodexAction.Agent6,
        CodexAction.ToggleFastMode,
        CodexAction.Approve,
        CodexAction.Reject,
        CodexAction.ForkTask,
        CodexAction.Submit,
        CodexAction.TogglePlanMode,
        CodexAction.IncreaseReasoning,
        CodexAction.DecreaseReasoning,
        CodexAction.ArchiveChat,
        CodexAction.NewTask,
        CodexAction.SideConversation,
        CodexAction.PreviousTask,
        CodexAction.NextTask,
        CodexAction.NavigateBack,
        CodexAction.NavigateForward,
        CodexAction.ToggleSidebar,
        CodexAction.ToggleReviewPanel,
        CodexAction.OpenSkills,
        CodexAction.StartVoiceChat,
        CodexAction.EndVoiceChat,
        CodexAction.ToggleVoiceChatMicrophone,
    };

    [Theory]
    [MemberData(nameof(CommandMappings))]
    public void EveryCommandBackedActionHasOneCentralCommandId(CodexAction action, string commandId)
    {
        Assert.True(CodexCommandCatalog.TryGet(action, out var descriptor));
        Assert.Equal(commandId, descriptor.CommandId);
    }

    [Fact]
    public void CentralCatalogCoversEveryNonRawAction()
    {
        var rawActions = new HashSet<CodexAction>
        {
            CodexAction.ScrollUp,
            CodexAction.ScrollDown,
            CodexAction.Home,
            CodexAction.End,
            CodexAction.ButtonMap,
            CodexAction.InAppPushToTalk,
            CodexAction.Dictation,
        };
        var expected = Enum.GetValues<CodexAction>().Where(action => !rawActions.Contains(action)).Order().ToArray();
        var actual = CodexCommandCatalog.All.Select(descriptor => descriptor.Action).Order().ToArray();

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(OrdinaryCommandActions))]
    public async Task CommandBackedActionsAlwaysUseTheResolver(CodexAction action)
    {
        var resolver = new RecordingResolver("Ctrl+Alt+Q");
        var input = new RecordingInputSender();
        var log = new List<string>();
        var executor = CreateExecutor(resolver, input, log);

        var result = await executor.ExecuteAsync(Request(action), CancellationToken.None);

        Assert.True(result.Executed);
        Assert.Equal([action], resolver.Actions);
        Assert.Equal("Ctrl+Alt+Q", Assert.Single(input.SentSequences).NormalizedText);
        var diagnostic = Assert.Single(log);
        Assert.Contains("command=test.command", diagnostic, StringComparison.Ordinal);
        Assert.Contains("binding=Ctrl+Alt+Q", diagnostic, StringComparison.Ordinal);
        Assert.Contains("source=user", diagnostic, StringComparison.Ordinal);
        Assert.Contains("snapshot=current", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnresolvedCommandLogsCompleteActionableDiagnosticsWithoutSendingInput()
    {
        var resolution = new CodexBindingResolution(
            CodexAction.Approve,
            "approval.approve",
            null,
            CodexBindingSource.None,
            CodexBindingSnapshotState.LastKnownGood,
            "Assign 'approval.approve' in Settings > Keyboard Shortcuts.");
        var input = new RecordingInputSender();
        var log = new List<string>();
        var executor = CreateExecutor(new FixedResolutionResolver(resolution), input, log);

        var result = await executor.ExecuteAsync(Request(CodexAction.Approve), CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Empty(input.SentSequences);
        var diagnostic = Assert.Single(log);
        Assert.Contains("BLOCKED approve", diagnostic, StringComparison.Ordinal);
        Assert.Contains("command=approval.approve", diagnostic, StringComparison.Ordinal);
        Assert.Contains("binding=<unresolved>", diagnostic, StringComparison.Ordinal);
        Assert.Contains("source=none", diagnostic, StringComparison.Ordinal);
        Assert.Contains("snapshot=last-known-good", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Settings > Keyboard Shortcuts", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LastKnownGoodSuccessIsVisibleInDispatchDiagnostics()
    {
        var resolver = new RecordingResolver(
            "Ctrl+Enter",
            commandId: "approval.approve",
            snapshotState: CodexBindingSnapshotState.LastKnownGood);
        var log = new List<string>();
        var executor = CreateExecutor(resolver, new RecordingInputSender(), log);

        var result = await executor.ExecuteAsync(Request(CodexAction.Approve), CancellationToken.None);

        Assert.True(result.Executed);
        Assert.Contains("snapshot=last-known-good", Assert.Single(log), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CodexAction.Home, "Home")]
    [InlineData(CodexAction.End, "End")]
    public async Task RawNavigationGesturesDoNotUseTheCodexResolver(CodexAction action, string expected)
    {
        var resolver = new RecordingResolver("Ctrl+Q");
        var input = new RecordingInputSender();
        var executor = CreateExecutor(resolver, input);

        await executor.ExecuteAsync(Request(action), CancellationToken.None);

        Assert.Empty(resolver.Actions);
        Assert.Equal(expected, Assert.Single(input.SentSequences).NormalizedText);
    }

    [Theory]
    [InlineData(CodexAction.ScrollUp, 5, 600)]
    [InlineData(CodexAction.ScrollDown, 5, -600)]
    public async Task ScrollRemainsDirectRawInput(CodexAction action, int notches, int expectedDelta)
    {
        var resolver = new RecordingResolver("Ctrl+Q");
        var input = new RecordingInputSender();
        var executor = CreateExecutor(resolver, input);

        await executor.ExecuteAsync(Request(action, wheelNotches: notches), CancellationToken.None);

        Assert.Empty(resolver.Actions);
        Assert.Equal(expectedDelta, Assert.Single(input.WheelDeltas));
    }

    [Fact]
    public async Task PushToTalkHoldsAndReleasesTheCurrentlyResolvedChord()
    {
        var resolver = new RecordingResolver("Alt+Space", commandId: "globalDictationHold");
        var input = new RecordingInputSender();
        var log = new List<string>();
        var executor = CreateExecutor(resolver, input, log);

        var pressed = await executor.ExecuteAsync(Request(CodexAction.PushToTalk), CancellationToken.None);
        var released = await executor.ExecuteAsync(
            Request(CodexAction.PushToTalk, trigger: "release"),
            CancellationToken.None);

        Assert.True(pressed.Executed);
        Assert.True(released.Executed);
        Assert.Equal("Alt+Space", Assert.Single(input.HeldChords).NormalizedText);
        Assert.Equal("Alt+Space", Assert.Single(input.ReleasedChords).NormalizedText);
        Assert.Contains("command=globalDictationHold", log[1], StringComparison.Ordinal);
        Assert.Contains("binding=Alt+Space", log[1], StringComparison.Ordinal);
        Assert.Contains("source=user", log[1], StringComparison.Ordinal);
        Assert.Contains("snapshot=current", log[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InAppPushToTalkInvokesAndStopsDictationInTheRecordedWindow()
    {
        var resolver = new RecordingResolver("Ctrl+Shift+D");
        var input = new RecordingInputSender();
        var dictation = new RecordingDictationControl();
        var log = new List<string>();
        var executor = CreateExecutor(resolver, input, log, dictationControl: dictation);

        var pressed = await executor.ExecuteAsync(Request(CodexAction.InAppPushToTalk), CancellationToken.None);
        var released = await executor.ExecuteAsync(
            Request(CodexAction.InAppPushToTalk, trigger: "release"),
            CancellationToken.None);

        Assert.True(pressed.Executed);
        Assert.True(released.Executed);
        Assert.Empty(resolver.Actions);
        Assert.Empty(input.HeldChords);
        Assert.Empty(input.ReleasedChords);
        Assert.Equal(1, dictation.StartCalls);
        Assert.Equal([dictation.WindowHandle], dictation.StopWindows);
        Assert.Contains("accessibility-button=Dictate", log[0], StringComparison.Ordinal);
        Assert.Contains("accessibility-button=Stop dictation", log[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InAppPushToTalkReleaseMatchesDeviceAndBankCaseInsensitively()
    {
        var dictation = new RecordingDictationControl();
        var executor = CreateExecutor(
            new RecordingResolver("Ctrl+Shift+D"),
            new RecordingInputSender(),
            dictationControl: dictation);

        await executor.ExecuteAsync(
            Request(CodexAction.InAppPushToTalk, deviceId: "T4") with { Bank = "Always" },
            CancellationToken.None);
        var released = await executor.ExecuteAsync(
            Request(CodexAction.InAppPushToTalk, trigger: "release", deviceId: "t4") with { Bank = "ALWAYS" },
            CancellationToken.None);

        Assert.True(released.Executed);
        Assert.Equal([dictation.WindowHandle], dictation.StopWindows);
    }

    [Fact]
    public async Task DictationInvokesTheAccessibleComposerButtonWithoutResolvingACommand()
    {
        var resolver = new RecordingResolver("Ctrl+Shift+D");
        var dictation = new RecordingDictationControl();
        var executor = CreateExecutor(
            resolver,
            new RecordingInputSender(),
            dictationControl: dictation);

        var result = await executor.ExecuteAsync(Request(CodexAction.Dictation), CancellationToken.None);

        Assert.True(result.Executed);
        Assert.Empty(resolver.Actions);
        Assert.Equal(1, dictation.StartCalls);
        Assert.Empty(dictation.StopWindows);
    }

    [Fact]
    public async Task HeldKeyCleanupStopsInAppDictationInTheRecordedWindow()
    {
        var dictation = new RecordingDictationControl();
        var executor = CreateExecutor(
            new RecordingResolver("Ctrl+Shift+D"),
            new RecordingInputSender(),
            dictationControl: dictation);
        await executor.ExecuteAsync(
            Request(CodexAction.InAppPushToTalk, deviceId: "t4"),
            CancellationToken.None);

        var completed = executor.ReleaseHeldKeys();

        Assert.True(completed);
        Assert.Equal([dictation.WindowHandle], dictation.StopWindows);
    }

    [Fact]
    public async Task FailedDictationCleanupReportsIncompleteAndRetries()
    {
        var dictation = new RecordingDictationControl();
        dictation.StopResults.Enqueue(
            CodexDictationControlResult.Failed(dictation.WindowHandle, "Dictation startup is still settling."));
        var executor = CreateExecutor(
            new RecordingResolver("Ctrl+Shift+D"),
            new RecordingInputSender(),
            dictationControl: dictation);
        await executor.ExecuteAsync(Request(CodexAction.InAppPushToTalk), CancellationToken.None);

        var firstCleanup = executor.ReleaseHeldKeys();
        var secondCleanup = executor.ReleaseHeldKeys();

        Assert.False(firstCleanup);
        Assert.True(secondCleanup);
        Assert.Equal([dictation.WindowHandle, dictation.WindowHandle], dictation.StopWindows);
    }

    [Fact]
    public async Task HeldActionReleaseBypassesTheForegroundGuard()
    {
        var resolver = new RecordingResolver("Ctrl+Shift+D");
        var input = new RecordingInputSender();
        var dictation = new RecordingDictationControl();
        var foreground = new MutableForegroundGuard();
        var executor = CreateExecutor(
            resolver,
            input,
            foregroundGuard: foreground,
            dictationControl: dictation);

        await executor.ExecuteAsync(Request(CodexAction.InAppPushToTalk), CancellationToken.None);
        foreground.Allowed = false;
        var released = await executor.ExecuteAsync(
            Request(CodexAction.InAppPushToTalk, trigger: "release"),
            CancellationToken.None);

        Assert.True(released.Executed);
        Assert.Equal([dictation.WindowHandle], dictation.StopWindows);
    }

    [Fact]
    public async Task FailedInAppStopKeepsTheRecordedOwnerForRetry()
    {
        var dictation = new RecordingDictationControl();
        dictation.StopResults.Enqueue(
            CodexDictationControlResult.Failed(dictation.WindowHandle, "Dictation startup is still settling."));
        var executor = CreateExecutor(
            new RecordingResolver("Ctrl+Shift+D"),
            new RecordingInputSender(),
            dictationControl: dictation);

        await executor.ExecuteAsync(Request(CodexAction.InAppPushToTalk), CancellationToken.None);
        var firstRelease = await executor.ExecuteAsync(
            Request(CodexAction.InAppPushToTalk, trigger: "release"),
            CancellationToken.None);
        var secondRelease = await executor.ExecuteAsync(
            Request(CodexAction.InAppPushToTalk, trigger: "release"),
            CancellationToken.None);

        Assert.False(firstRelease.Executed);
        Assert.True(secondRelease.Executed);
        Assert.Equal([dictation.WindowHandle, dictation.WindowHandle], dictation.StopWindows);
    }

    [Fact]
    public void StartupCleanupReleasesTheCurrentlyResolvedPushToTalkChord()
    {
        var resolver = new RecordingResolver("Alt+Space", commandId: "globalDictationHold");
        var input = new RecordingInputSender();
        var log = new List<string>();
        var executor = CreateExecutor(resolver, input, log);

        executor.ClearInjectedKeyState();

        Assert.Equal([CodexAction.PushToTalk], resolver.Actions);
        Assert.Equal("Alt+Space", Assert.Single(input.ReleasedChords).NormalizedText);
        Assert.Contains("startup push-to-talk cleanup", Assert.Single(log), StringComparison.Ordinal);
        Assert.Contains("binding=Alt+Space", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiStepPushToTalkFailsWithoutInjectingKeys()
    {
        var resolver = new RecordingResolver("Ctrl+K Ctrl+D", commandId: "globalDictationHold");
        var input = new RecordingInputSender();
        var log = new List<string>();
        var executor = CreateExecutor(resolver, input, log);

        var result = await executor.ExecuteAsync(Request(CodexAction.PushToTalk), CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Empty(input.HeldChords);
        Assert.Contains("single chord", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedPushToTalkReleaseIsRetriedDuringCleanup()
    {
        var resolver = new RecordingResolver("Ctrl+CapsLock", commandId: "globalDictationHold");
        var input = new RecordingInputSender { ReleaseFailuresRemaining = 1 };
        var executor = CreateExecutor(resolver, input);
        await executor.ExecuteAsync(Request(CodexAction.PushToTalk), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            Request(CodexAction.PushToTalk, trigger: "release"),
            CancellationToken.None));
        executor.ReleaseHeldKeys();

        Assert.Equal(2, input.ReleaseAttempts);
        Assert.Single(input.ReleasedChords);
    }

    [Fact]
    public async Task FailedPartialHoldCleanupRetainsTheChordForAnotherReleaseAttempt()
    {
        var resolver = new RecordingResolver("Ctrl+CapsLock", commandId: "globalDictationHold");
        var input = new RecordingInputSender
        {
            HoldFailuresRemaining = 1,
            ReleaseFailuresRemaining = 1,
        };
        var executor = CreateExecutor(resolver, input);

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            Request(CodexAction.PushToTalk),
            CancellationToken.None));
        executor.ReleaseHeldKeys();

        Assert.Equal(2, input.ReleaseAttempts);
        Assert.Equal("Ctrl+CapsLock", Assert.Single(input.ReleasedChords).NormalizedText);
    }

    [Fact]
    public async Task InputFailureLogsTheCompleteCommandResolutionBeforeEscaping()
    {
        var resolver = new RecordingResolver(
            "Ctrl+Enter",
            commandId: "approval.approve",
            snapshotState: CodexBindingSnapshotState.LastKnownGood);
        var input = new RecordingInputSender { SendFailure = new InvalidOperationException("simulated input failure") };
        var log = new List<string>();
        var executor = CreateExecutor(resolver, input, log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            Request(CodexAction.Approve),
            CancellationToken.None));

        var diagnostic = Assert.Single(log);
        Assert.Contains("FAILED approve", diagnostic, StringComparison.Ordinal);
        Assert.Contains("command=approval.approve", diagnostic, StringComparison.Ordinal);
        Assert.Contains("binding=Ctrl+Enter", diagnostic, StringComparison.Ordinal);
        Assert.Contains("source=user", diagnostic, StringComparison.Ordinal);
        Assert.Contains("snapshot=last-known-good", diagnostic, StringComparison.Ordinal);
        Assert.Contains("simulated input failure", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolverFailureRetainsTheCommandIdentityInDiagnostics()
    {
        var log = new List<string>();
        var executor = CreateExecutor(new ThrowingResolver(), new RecordingInputSender(), log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            Request(CodexAction.Approve),
            CancellationToken.None));

        var diagnostic = Assert.Single(log);
        Assert.Contains("FAILED approve", diagnostic, StringComparison.Ordinal);
        Assert.Contains("command=approval.approve", diagnostic, StringComparison.Ordinal);
        Assert.Contains("binding=<unresolved>", diagnostic, StringComparison.Ordinal);
        Assert.Contains("source=none", diagnostic, StringComparison.Ordinal);
        Assert.Contains("snapshot=unavailable", diagnostic, StringComparison.Ordinal);
        Assert.Contains("simulated resolver failure", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRequiresFreshClipboardAndUsesTheConfiguredFixedLauncher()
    {
        const string directory = @"C:\project with & metacharacters";
        var resolver = new RecordingResolver("Ctrl+Shift+C", commandId: "copyWorkingDirectory");
        var input = new RecordingInputSender();
        var clipboard = new RecordingClipboard(42, ClipboardDirectoryResult.Found(directory));
        var launcher = new RecordingLauncher(OpenWorkingDirectoryOptions.FileExplorerTarget);
        var log = new List<string>();
        var executor = CreateExecutor(
            resolver,
            input,
            log,
            clipboard,
            new WorkingDirectoryLauncherRegistry([launcher]),
            OpenWorkingDirectoryOptions.FileExplorerTarget);

        var result = await executor.ExecuteAsync(Request(CodexAction.OpenWorkingDirectory), CancellationToken.None);

        Assert.True(result.Executed);
        Assert.Equal((uint)42, clipboard.PreviousSequenceNumber);
        Assert.Equal("Ctrl+Shift+C", Assert.Single(input.SentSequences).NormalizedText);
        Assert.Equal(directory, Assert.Single(launcher.Directories));
        Assert.DoesNotContain(directory, Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenBlocksOnAStaleClipboardWithoutLaunching()
    {
        var resolver = new RecordingResolver("Ctrl+Shift+C", commandId: "copyWorkingDirectory");
        var clipboard = new RecordingClipboard(
            7,
            ClipboardDirectoryResult.Failure("Codex did not place a fresh working directory on the clipboard."));
        var launcher = new RecordingLauncher(OpenWorkingDirectoryOptions.VisualStudioCodeTarget);
        var log = new List<string>();
        var executor = CreateExecutor(
            resolver,
            new RecordingInputSender(),
            log,
            clipboard,
            new WorkingDirectoryLauncherRegistry([launcher]));

        var result = await executor.ExecuteAsync(Request(CodexAction.OpenWorkingDirectory), CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Empty(launcher.Directories);
        Assert.Contains("fresh working directory", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenBlocksWhenTheConfiguredLauncherIsMissing()
    {
        var resolver = new RecordingResolver("Ctrl+Shift+C", commandId: "copyWorkingDirectory");
        var executor = CreateExecutor(
            resolver,
            new RecordingInputSender(),
            clipboard: new RecordingClipboard(1, ClipboardDirectoryResult.Found(@"C:\project")),
            launchers: new WorkingDirectoryLauncherRegistry([]));

        var result = await executor.ExecuteAsync(Request(CodexAction.OpenWorkingDirectory), CancellationToken.None);

        Assert.False(result.Executed);
        Assert.Contains("Unknown working-directory target", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipboardDirectoryValidationAcceptsOnlyExistingAbsoluteDirectories()
    {
        var directory = Path.Combine(Path.GetTempPath(), "JoydexTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.True(WindowsWorkingDirectoryClipboard.ValidateDirectory(directory).Success);
            Assert.False(WindowsWorkingDirectoryClipboard.ValidateDirectory("relative").Success);
            Assert.False(WindowsWorkingDirectoryClipboard.ValidateDirectory(Path.Combine(directory, "missing")).Success);
            Assert.False(WindowsWorkingDirectoryClipboard.ValidateDirectory(string.Empty).Success);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClipboardWaitRejectsAnUnchangedSequenceAtTheRealTimeoutBoundary()
    {
        var native = new ScriptedClipboardNative { SequenceNumber = 10 };
        var timing = new ManualClipboardTiming();
        var clipboard = new WindowsWorkingDirectoryClipboard(native, timing);

        var result = await clipboard.WaitForNewDirectoryAsync(
            10,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("fresh working directory", result.Error, StringComparison.Ordinal);
        Assert.Equal([25, 25, 25, 25], timing.Delays);
        Assert.Equal(TimeSpan.FromMilliseconds(100), timing.Elapsed);
        Assert.Equal(0, native.ReadAttempts);
    }

    [Fact]
    public async Task ClipboardWaitRetriesALockedChangedClipboardUntilItCanRead()
    {
        var directory = Path.Combine(Path.GetTempPath(), "JoydexTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var native = new ScriptedClipboardNative { SequenceNumber = 11 };
            native.ReadResults.Enqueue((false, null));
            native.ReadResults.Enqueue((true, directory));
            var timing = new ManualClipboardTiming();
            var clipboard = new WindowsWorkingDirectoryClipboard(native, timing);

            var result = await clipboard.WaitForNewDirectoryAsync(
                10,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

            Assert.True(result.Success, result.Error);
            Assert.Equal(directory, result.DirectoryPath);
            Assert.Equal(2, native.ReadAttempts);
            Assert.Equal([25], timing.Delays);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClipboardWaitReportsAChangedClipboardThatStaysLocked()
    {
        var native = new ScriptedClipboardNative { SequenceNumber = 11 };
        var timing = new ManualClipboardTiming();
        var clipboard = new WindowsWorkingDirectoryClipboard(native, timing);

        var result = await clipboard.WaitForNewDirectoryAsync(
            10,
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("could not be read", result.Error, StringComparison.Ordinal);
        Assert.Equal(2, native.ReadAttempts);
    }

    [Fact]
    public void LauncherArgumentsArePassedAsOneLiteralArgument()
    {
        const string directory = @"C:\project & calc.exe";

        var startInfo = WorkingDirectoryProcessStartInfo.Create(@"C:\Program Files\Code.exe", directory);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Equal(directory, Assert.Single(startInfo.ArgumentList));
    }

    [Fact]
    public void VisualStudioCodeLauncherUsesOneFixedWindowSwitchAndOneLiteralDirectory()
    {
        const string directory = @"C:\project & calc.exe";

        var startInfo = WorkingDirectoryProcessStartInfo.CreateVisualStudioCode(
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            directory);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Equal(["--new-window", directory], startInfo.ArgumentList);
    }

    private static CodexActionExecutor CreateExecutor(
        ICodexKeybindingResolver resolver,
        RecordingInputSender input,
        List<string>? log = null,
        IWorkingDirectoryClipboard? clipboard = null,
        WorkingDirectoryLauncherRegistry? launchers = null,
        string openTarget = OpenWorkingDirectoryOptions.VisualStudioCodeTarget,
        IForegroundProcessGuard? foregroundGuard = null,
        ICodexDictationControl? dictationControl = null) => new(
            new SafetyOptions { DryRun = false },
            (log ?? []).Add,
            resolver,
            new OpenWorkingDirectoryOptions { Target = openTarget },
            foregroundGuard: foregroundGuard ?? new AllowedForegroundGuard(),
            inputSender: input,
            clipboard: clipboard ?? new RecordingClipboard(0, ClipboardDirectoryResult.Failure("not used")),
            launchers: launchers ?? new WorkingDirectoryLauncherRegistry([]),
            dictationControl: dictationControl);

    private static ActionRequest Request(
        CodexAction action,
        string trigger = "press",
        int wheelNotches = 1,
        string deviceId = "test-device") => new(
            BindingName: "test",
            Bank: "work",
            Button: 3,
            Trigger: trigger,
            Action: action,
            RequestedAt: DateTimeOffset.UtcNow,
            WheelNotches: wheelNotches,
            DeviceId: deviceId);

    private sealed class AllowedForegroundGuard : IForegroundProcessGuard
    {
        public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward) =>
            new(true, "Codex", "allowed");
    }

    private sealed class MutableForegroundGuard : IForegroundProcessGuard
    {
        public bool Allowed { get; set; } = true;

        public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward) =>
            new(Allowed, Allowed ? "Codex" : "Other", Allowed ? "allowed" : "blocked");
    }

    private sealed class RecordingDictationControl : ICodexDictationControl
    {
        public IntPtr WindowHandle { get; } = new(1234);

        public int StartCalls { get; private set; }

        public List<IntPtr> StopWindows { get; } = [];

        public Queue<CodexDictationControlResult> StopResults { get; } = [];

        public CodexDictationControlResult Start()
        {
            StartCalls++;
            return CodexDictationControlResult.Completed(WindowHandle, "accessibility-button=Dictate");
        }

        public CodexDictationControlResult Stop(IntPtr windowHandle)
        {
            StopWindows.Add(windowHandle);
            return StopResults.TryDequeue(out var result)
                ? result
                : CodexDictationControlResult.Completed(windowHandle, "accessibility-button=Stop dictation");
        }
    }

    private sealed class RecordingResolver : ICodexKeybindingResolver
    {
        private readonly string _binding;
        private readonly string _commandId;
        private readonly CodexBindingSnapshotState _snapshotState;

        public RecordingResolver(
            string binding,
            string commandId = "test.command",
            CodexBindingSnapshotState snapshotState = CodexBindingSnapshotState.Current)
        {
            _binding = binding;
            _commandId = commandId;
            _snapshotState = snapshotState;
        }

        public List<CodexAction> Actions { get; } = [];

        public Task<CodexBindingResolution> ResolveAsync(CodexAction action, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            Assert.True(KeySequenceParser.TryParse(_binding, true, out var sequence, out var error), error);
            return Task.FromResult(new CodexBindingResolution(
                action,
                _commandId,
                sequence,
                CodexBindingSource.User,
                _snapshotState,
                null));
        }
    }

    private sealed class FixedResolutionResolver(CodexBindingResolution resolution) : ICodexKeybindingResolver
    {
        public Task<CodexBindingResolution> ResolveAsync(
            CodexAction action,
            CancellationToken cancellationToken) => Task.FromResult(resolution);
    }

    private sealed class ThrowingResolver : ICodexKeybindingResolver
    {
        public Task<CodexBindingResolution> ResolveAsync(
            CodexAction action,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated resolver failure");
    }

    private sealed class RecordingInputSender : IInputSender
    {
        public int HoldFailuresRemaining { get; init; }

        public int ReleaseFailuresRemaining { get; init; }

        public Exception? SendFailure { get; init; }

        public int HoldAttempts { get; private set; }

        public int ReleaseAttempts { get; private set; }

        public List<KeySequence> SentSequences { get; } = [];

        public List<KeyChord> HeldChords { get; } = [];

        public List<KeyChord> ReleasedChords { get; } = [];

        public List<int> WheelDeltas { get; } = [];

        public Task SendSequenceAsync(KeySequence sequence, CancellationToken cancellationToken)
        {
            if (SendFailure is not null)
            {
                throw SendFailure;
            }

            SentSequences.Add(sequence);
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;

        public void HoldChord(KeyChord chord)
        {
            HoldAttempts++;
            if (HoldFailuresRemaining >= HoldAttempts)
            {
                throw new InvalidOperationException("simulated hold failure");
            }

            HeldChords.Add(chord);
        }

        public void ReleaseChord(KeyChord chord)
        {
            ReleaseAttempts++;
            if (ReleaseFailuresRemaining >= ReleaseAttempts)
            {
                throw new InvalidOperationException("simulated release failure");
            }

            ReleasedChords.Add(chord);
        }

        public void SendMouseWheel(int delta) => WheelDeltas.Add(delta);
    }

    private sealed class RecordingClipboard(uint sequenceNumber, ClipboardDirectoryResult result)
        : IWorkingDirectoryClipboard
    {
        public uint? PreviousSequenceNumber { get; private set; }

        public uint GetSequenceNumber() => sequenceNumber;

        public Task<ClipboardDirectoryResult> WaitForNewDirectoryAsync(
            uint previousSequenceNumber,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            PreviousSequenceNumber = previousSequenceNumber;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingLauncher(string targetId) : IWorkingDirectoryLauncher
    {
        public string TargetId { get; } = targetId;

        public List<string> Directories { get; } = [];

        public WorkingDirectoryLaunchResult Launch(string directoryPath)
        {
            Directories.Add(directoryPath);
            return WorkingDirectoryLaunchResult.Launched();
        }
    }

    private sealed class ScriptedClipboardNative : IWindowsClipboardNative
    {
        public uint SequenceNumber { get; init; }

        public Queue<(bool Success, string? Text)> ReadResults { get; } = [];

        public int ReadAttempts { get; private set; }

        public uint GetSequenceNumber() => SequenceNumber;

        public bool TryReadUnicodeText(out string? text)
        {
            ReadAttempts++;
            if (ReadResults.TryDequeue(out var result))
            {
                text = result.Text;
                return result.Success;
            }

            text = null;
            return false;
        }
    }

    private sealed class ManualClipboardTiming : IWindowsClipboardTiming
    {
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UnixEpoch;

        public List<int> Delays { get; } = [];

        public TimeSpan Elapsed { get; private set; }

        public DateTimeOffset UtcNow => _startedAt + Elapsed;

        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(milliseconds);
            Elapsed += TimeSpan.FromMilliseconds(milliseconds);
            return Task.CompletedTask;
        }
    }
}
