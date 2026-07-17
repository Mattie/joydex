using VirpilCodexPad.Core.Config;
using VirpilCodexPad.Core.Mapping;
using VirpilCodexPad.Windows.Actions;

namespace VirpilCodexPad.Core.Tests;

public sealed class ForegroundSafetyTests
{
    [Fact]
    public void SimulatorBlocksDeepLinksAsWellAsShortcuts()
    {
        var safety = Safety(simulators: ["DCS"]);

        var result = ForegroundProcessGuard.Evaluate(
            safety,
            processName: "dcs",
            actionMayBringCodexForward: true);

        Assert.False(result.Allowed);
        Assert.Contains("Simulator", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SimulatorProcessMayBeConfiguredWithExeSuffix()
    {
        var result = ForegroundProcessGuard.Evaluate(
            Safety(simulators: ["DCS.exe"]),
            processName: "DCS",
            actionMayBringCodexForward: true);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void NonCodexForegroundBlocksKeyboardShortcut()
    {
        var result = ForegroundProcessGuard.Evaluate(
            Safety(),
            processName: "explorer",
            actionMayBringCodexForward: false);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void CodexForegroundAllowsKeyboardShortcutCaseInsensitively()
    {
        var result = ForegroundProcessGuard.Evaluate(
            Safety(),
            processName: "chatgpt",
            actionMayBringCodexForward: false);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void ExplicitDeepLinkMayBringCodexForward()
    {
        var result = ForegroundProcessGuard.Evaluate(
            Safety(),
            processName: "explorer",
            actionMayBringCodexForward: true);

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task DryRunReportsActionWithoutDeliveringIt()
    {
        var guard = new RecordingGuard(new ForegroundCheck(true, "ChatGPT", "allowed"));
        var log = new List<string>();
        var executor = CreateExecutor(Safety(dryRun: true), log.Add, guard);

        var result = await executor.ExecuteAsync(Request(CodexAction.NewTask), CancellationToken.None);

        Assert.False(result.Executed);
        Assert.True(result.DryRun);
        Assert.False(guard.LastActionMayBringCodexForward);
        Assert.Contains("DRY RUN new-task", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunReportsWhenLiveModeWouldBlock()
    {
        var guard = new RecordingGuard(new ForegroundCheck(false, "DCS", "blocked"));
        var log = new List<string>();
        var executor = CreateExecutor(Safety(dryRun: true), log.Add, guard);

        var result = await executor.ExecuteAsync(Request(CodexAction.ToggleSidebar), CancellationToken.None);

        Assert.False(result.Executed);
        Assert.True(result.DryRun);
        Assert.False(guard.LastActionMayBringCodexForward);
        var message = Assert.Single(log);
        Assert.Contains("DRY RUN toggle-sidebar", message, StringComparison.Ordinal);
        Assert.Contains("LIVE MODE WOULD BLOCK", message, StringComparison.Ordinal);
        Assert.Contains("button 3", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveModeStillBlocksWhenCodexIsNotForeground()
    {
        var guard = new RecordingGuard(new ForegroundCheck(false, "Explorer", "Codex is not foreground"));
        var log = new List<string>();
        var executor = CreateExecutor(Safety(dryRun: false), log.Add, guard);

        var result = await executor.ExecuteAsync(Request(CodexAction.ToggleSidebar), CancellationToken.None);

        Assert.False(result.Executed);
        Assert.False(result.DryRun);
        Assert.Contains("BLOCKED toggle-sidebar", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ButtonMapRunsInternallyWithoutForegroundOrDryRunBlockingIt()
    {
        var guard = new RecordingGuard(new ForegroundCheck(false, "Explorer", "blocked"));
        var log = new List<string>();
        ActionRequest? delivered = null;
        var executor = new CodexActionExecutor(
            Safety(dryRun: true),
            log.Add,
            new FixedResolver(),
            new OpenWorkingDirectoryOptions(),
            foregroundGuard: guard,
            internalAction: request => delivered = request);

        var result = await executor.ExecuteAsync(Request(CodexAction.ButtonMap), CancellationToken.None);

        Assert.True(result.Executed);
        Assert.False(result.DryRun);
        Assert.NotNull(delivered);
        Assert.Contains("EXECUTED button-map press", Assert.Single(log), StringComparison.Ordinal);
    }

    private static SafetyOptions Safety(bool dryRun = true, string[]? simulators = null) => new()
    {
        DryRun = dryRun,
        SimulatorProcessNames = simulators ?? [],
    };

    private static ActionRequest Request(CodexAction action) => new(
        BindingName: "test-binding",
        Bank: "work",
        Button: 3,
        Trigger: "press",
        Action: action,
        RequestedAt: DateTimeOffset.UtcNow);

    private static CodexActionExecutor CreateExecutor(
        SafetyOptions safety,
        Action<string> log,
        IForegroundProcessGuard guard) => new(
            safety,
            log,
            new FixedResolver(),
            new OpenWorkingDirectoryOptions(),
            foregroundGuard: guard,
            inputSender: new RecordingInputSender());

    private sealed class FixedResolver : ICodexKeybindingResolver
    {
        public Task<CodexBindingResolution> ResolveAsync(
            CodexAction action,
            CancellationToken cancellationToken)
        {
            KeySequenceParser.TryParse("Ctrl+N", false, out var sequence, out _);
            return Task.FromResult(new CodexBindingResolution(
                action,
                action.ToString(),
                sequence,
                CodexBindingSource.User,
                CodexBindingSnapshotState.Current,
                null));
        }
    }

    private sealed class RecordingInputSender : IInputSender
    {
        public Task SendSequenceAsync(KeySequence sequence, CancellationToken cancellationToken) => Task.CompletedTask;

        public void HoldChord(KeyChord chord)
        {
        }

        public void ReleaseChord(KeyChord chord)
        {
        }

        public void SendMouseWheel(int delta)
        {
        }
    }

    private sealed class RecordingGuard(ForegroundCheck result) : IForegroundProcessGuard
    {
        public bool LastActionMayBringCodexForward { get; private set; }

        public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward)
        {
            LastActionMayBringCodexForward = actionMayBringCodexForward;
            return result;
        }
    }
}
