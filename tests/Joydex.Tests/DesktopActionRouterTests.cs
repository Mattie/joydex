using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Actions;

namespace Joydex.Tests;

public sealed class DesktopActionRouterTests
{
    private static readonly DesktopActionForeground T3 = new(new(123), 42, @"C:\Apps\T3\T3 Code (Beta).exe", "T3 Code (Beta)", true);
    private static ActionRequest Request(CodexAction action, string trigger = "press") =>
        new("task action", "work", 1, trigger, action, DateTimeOffset.UtcNow);
    private static T3DesktopActionContext Verified => new("environment", "source-keymap-fingerprint", true, true,
        new Dictionary<CodexAction, string>
        {
            [CodexAction.NewTask] = "Ctrl+Alt+N", [CodexAction.PreviousTask] = "Ctrl+Alt+Left",
            [CodexAction.NextTask] = "Ctrl+Alt+Right", [CodexAction.StopTask] = "Ctrl+Alt+S",
        }, TaskAlertState.Running, "thread", "run", WindowHandle: T3.WindowHandle, ProcessId: T3.ProcessId);

    [Theory]
    [InlineData(CodexAction.NewTask, "Ctrl+Alt+N")]
    [InlineData(CodexAction.PreviousTask, "Ctrl+Alt+Left")]
    [InlineData(CodexAction.NextTask, "Ctrl+Alt+Right")]
    [InlineData(CodexAction.StopTask, "Ctrl+Alt+S")]
    public async Task VerifiedCommonActionsUseOnlyTheCurrentSourceBinding(CodexAction action, string binding)
    {
        var input = new RecordingInput();
        var router = Router(input);
        var result = await router.TryExecuteAsync(Request(action), CancellationToken.None);
        Assert.True(result!.Executed);
        Assert.Equal(binding, Assert.Single(input.Sequences).NormalizedText);
        Assert.True(router.GetCapability(action).Available);
    }

    [Theory]
    [InlineData(CodexAction.Approve)]
    [InlineData(CodexAction.Reject)]
    [InlineData(CodexAction.Submit)]
    [InlineData(CodexAction.Dictation)]
    [InlineData(CodexAction.ScrollUp)]
    [InlineData(CodexAction.Home)]
    public async Task CodexSpecificAndRawActionsCannotLeakIntoT3(CodexAction action)
    {
        var input = new RecordingInput();
        var result = await Router(input).TryExecuteAsync(Request(action), CancellationToken.None);
        Assert.False(result!.Executed);
        Assert.Contains("specific to Codex", result.Message, StringComparison.Ordinal);
        Assert.Empty(input.Sequences);
    }

    [Fact]
    public async Task StockDesktopRemainsExplicitlyUnverifiedAfterShortcutSetup()
    {
        var input = new RecordingInput();
        var router = new DesktopActionRouter(new() { DryRun = false }, _ => { },
            foregroundResolver: new Foreground(T3), inputSender: input);
        Assert.False(router.GetCapability(CodexAction.NewTask).Available);
        var result = await router.TryExecuteAsync(Request(CodexAction.NewTask), CancellationToken.None);
        Assert.False(result!.Executed);
        Assert.Contains("Shortcut setup alone cannot enable dispatch", result.Message, StringComparison.Ordinal);
        Assert.Empty(input.Sequences);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task MissingEnvironmentBindingWindowOrProcessProofBlocks(int fault)
    {
        var context = fault switch
        {
            0 => Verified with { EnvironmentVerified = false },
            1 => Verified with { BindingsVerified = false },
            2 => Verified with { BindingFingerprint = null },
            3 => Verified with { EnvironmentId = null },
            4 => Verified with { WindowHandle = new(99) },
            _ => Verified with { ProcessId = 999 },
        };
        var input = new RecordingInput();
        var result = await Router(input, new Context(_ => context)).TryExecuteAsync(Request(CodexAction.NewTask), CancellationToken.None);
        Assert.False(result!.Executed);
        Assert.Empty(input.Sequences);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TaskAlertState.Completed)]
    [InlineData(TaskAlertState.Fault)]
    [InlineData(TaskAlertState.Approval)]
    public async Task StopDoesNotActOnUnknownOrTerminalTask(TaskAlertState? state)
    {
        var input = new RecordingInput();
        var result = await Router(input, new Context(_ => Verified with { ActiveTaskState = state }))
            .TryExecuteAsync(Request(CodexAction.StopTask), CancellationToken.None);
        Assert.False(result!.Executed);
        Assert.Empty(input.Sequences);
    }

    [Fact]
    public async Task StopNeverInventsAnEscapeBinding()
    {
        var input = new RecordingInput();
        var result = await Router(input, new Context(_ => Verified with
            { Bindings = new Dictionary<CodexAction, string> { [CodexAction.NewTask] = "Ctrl+N" } }))
            .TryExecuteAsync(Request(CodexAction.StopTask), CancellationToken.None);
        Assert.False(result!.Executed);
        Assert.Contains("No verified effective T3 shortcut", result.Message, StringComparison.Ordinal);
        Assert.Empty(input.Sequences);
    }

    [Fact]
    public async Task ForegroundChangeBeforeFirstChordBlocksAllInput()
    {
        var input = new RecordingInput();
        var foreground = new Foreground(T3) { Current = false };
        var result = await Router(input, foreground: foreground).TryExecuteAsync(Request(CodexAction.NewTask), CancellationToken.None);
        Assert.False(result!.Executed);
        Assert.Empty(input.Sequences);
    }

    [Fact]
    public async Task MultiChordSequenceRechecksFocusBeforeEachChord()
    {
        var input = new RecordingInput();
        var foreground = new Foreground(T3);
        input.AfterSend = () => foreground.Current = false;
        var context = new Context(_ => Verified with { Bindings = new Dictionary<CodexAction, string>
            { [CodexAction.NewTask] = "Ctrl+K Ctrl+N" } });
        var result = await Router(input, context, foreground).TryExecuteAsync(Request(CodexAction.NewTask), CancellationToken.None);
        Assert.False(result!.Executed);
        Assert.Equal("Ctrl+K", Assert.Single(input.Sequences).NormalizedText);
    }

    [Fact]
    public async Task EnvironmentOrBindingChangesDuringDispatchInvalidateTheProof()
    {
        var calls = 0;
        var context = new Context(_ => ++calls == 1 ? Verified : Verified with { BindingFingerprint = "changed" });
        var input = new RecordingInput();
        var result = await Router(input, context).TryExecuteAsync(Request(CodexAction.NextTask), CancellationToken.None);
        Assert.False(result!.Executed);
        Assert.Empty(input.Sequences);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DryRunAndConfiguredSimulatorNeverSendInput(bool dryRun, bool simulator)
    {
        var input = new RecordingInput();
        var router = new DesktopActionRouter(new() { DryRun = dryRun,
            SimulatorProcessNames = simulator ? [T3.ProcessName] : [] }, _ => { },
            new Context(_ => Verified), new Foreground(T3), input);
        var result = await router.TryExecuteAsync(Request(CodexAction.NewTask), CancellationToken.None);
        Assert.False(result!.Executed);
        Assert.Equal(dryRun, result.DryRun);
        Assert.Empty(input.Sequences);
    }

    [Fact]
    public async Task NonT3ForegroundDelegatesToTheExistingCodexPath()
    {
        var input = new RecordingInput();
        var foreground = new Foreground(T3 with { IsT3Desktop = false, ProcessName = "Codex" });
        var context = new Context(_ => throw new InvalidOperationException("T3 context must not be read for Codex."));
        var result = await Router(input, context, foreground).TryExecuteAsync(Request(CodexAction.NewTask), CancellationToken.None);
        Assert.Null(result);
        Assert.Empty(input.Sequences);
    }

    [Fact]
    public async Task InternalButtonMapAndHeldKeyReleaseRunBeforeRouting()
    {
        var input = new RecordingInput();
        var resolver = new Resolver();
        var routeCalls = 0;
        var mapCalls = 0;
        var executor = Executor(input, resolver, (_, _) =>
        {
            routeCalls++;
            return Task.FromResult<ActionExecutionResult?>(null);
        }, _ => mapCalls++);
        Assert.True((await executor.ExecuteAsync(Request(CodexAction.ButtonMap), CancellationToken.None)).Executed);
        Assert.Equal(1, mapCalls);
        Assert.Equal(0, routeCalls);
        Assert.True((await executor.ExecuteAsync(Request(CodexAction.PushToTalk), CancellationToken.None)).Executed);
        var beforeRelease = routeCalls;
        Assert.True((await executor.ExecuteAsync(Request(CodexAction.PushToTalk, "release"), CancellationToken.None)).Executed);
        Assert.Equal(beforeRelease, routeCalls);
        Assert.Equal(1, input.Holds);
        Assert.Equal(1, input.Releases);
    }

    [Fact]
    public async Task ExecutorDoesNotResolveCodexKeysWhenT3RouterBlocks()
    {
        var input = new RecordingInput();
        var resolver = new Resolver();
        var router = Router(input);
        var executor = Executor(input, resolver, router.TryExecuteAsync);
        var result = await executor.ExecuteAsync(Request(CodexAction.Approve), CancellationToken.None);
        Assert.False(result.Executed);
        Assert.Equal(0, resolver.Calls);
        Assert.Empty(input.Sequences);
    }

    [Fact]
    public async Task FocusChangeWhileCodexBindingIsReadCannotSendCodexKeysToT3()
    {
        var input = new RecordingInput();
        var foreground = new Foreground(T3 with { IsT3Desktop = false });
        var resolver = new Resolver { OnResolve = () => foreground.Value = T3 };
        var router = Router(input, foreground: foreground);
        var executor = Executor(input, resolver, router.TryExecuteAsync);
        var result = await executor.ExecuteAsync(Request(CodexAction.Approve), CancellationToken.None);
        Assert.False(result.Executed);
        Assert.Empty(input.Sequences);
    }

    [Fact]
    public async Task CodexStopIsExplicitlyUnavailableWithoutChangingCommandCatalog()
    {
        Assert.True(CodexActionCatalog.TryParse("stop-task", out var action));
        Assert.Equal(CodexAction.StopTask, action);
        Assert.False(CodexCommandCatalog.TryGet(action, out _));
        Assert.Equal((int)CodexAction.OpenWorkingDirectory + 1, (int)action);
        var input = new RecordingInput();
        var resolver = new Resolver();
        var result = await Executor(input, resolver).ExecuteAsync(Request(action), CancellationToken.None);
        Assert.False(result.Executed);
        Assert.Contains("no verified Codex Desktop stop action", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, resolver.Calls);
    }

    [Theory]
    [InlineData("\"C:\\Apps\\T3 Code (Beta).exe\" \"%1\"", "C:\\Apps\\T3 Code (Beta).exe")]
    [InlineData("C:\\Apps\\T3.exe \"%1\"", null)]
    [InlineData("\"C:\\Apps\\T3.exe\" --extra \"%1\"", null)]
    [InlineData("\"C:\\Apps\\T3.exe\" %1", null)]
    [InlineData("\"T3.exe\" \"%1\"", null)]
    public void RegistryCommandParserAcceptsOnlyTheVerifiedQuotedUriHandlerShape(string command, string? expected) =>
        Assert.Equal(expected, InstalledT3ForegroundResolver.ParseRegisteredExecutable(command));

    private static DesktopActionRouter Router(RecordingInput input, Context? context = null, Foreground? foreground = null) =>
        new(new() { DryRun = false }, _ => { }, context ?? new(_ => Verified), foreground ?? new(T3), input);
    private static CodexActionExecutor Executor(RecordingInput input, Resolver resolver,
        Func<ActionRequest, CancellationToken, Task<ActionExecutionResult?>>? router = null,
        Action<ActionRequest>? internalAction = null) =>
        new(new() { DryRun = false }, _ => { }, resolver, new(), foregroundGuard: new AllowedGuard(),
            inputSender: input, internalAction: internalAction, desktopActionRouter: router);

    private sealed class Foreground(DesktopActionForeground value) : IDesktopActionForegroundResolver
    {
        public DesktopActionForeground Value { get; set; } = value;
        public bool Current { get; set; } = true;
        public DesktopActionForeground Capture() => Value;
        public bool IsCurrent(DesktopActionForeground foreground) => Current && Value == foreground;
    }
    private sealed class Context(Func<DesktopActionForeground, T3DesktopActionContext> get) : IT3DesktopActionContextProvider
    {
        public T3DesktopActionContext GetContext(DesktopActionForeground foreground) => get(foreground);
    }
    private sealed class RecordingInput : IInputSender
    {
        public List<KeySequence> Sequences { get; } = [];
        public Action? AfterSend { get; set; }
        public int Holds { get; private set; }
        public int Releases { get; private set; }
        public Task SendSequenceAsync(KeySequence sequence, CancellationToken cancellationToken)
        { Sequences.Add(sequence); AfterSend?.Invoke(); return Task.CompletedTask; }
        public Task SendTextAsync(string text, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public void HoldChord(KeyChord chord) => Holds++;
        public void ReleaseChord(KeyChord chord) => Releases++;
        public void SendMouseWheel(int delta) => throw new InvalidOperationException();
    }
    private sealed class Resolver : ICodexKeybindingResolver
    {
        public int Calls { get; private set; }
        public Action? OnResolve { get; init; }
        public Task<CodexBindingResolution> ResolveAsync(CodexAction action, CancellationToken cancellationToken)
        {
            Calls++; OnResolve?.Invoke();
            Assert.True(KeySequenceParser.TryParse("Ctrl+N", false, out var sequence, out _));
            return Task.FromResult(new CodexBindingResolution(action, "existing.command", sequence,
                CodexBindingSource.User, CodexBindingSnapshotState.Current, null));
        }
    }
    private sealed class AllowedGuard : IForegroundProcessGuard
    {
        public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward) => new(true, "Codex", "allowed", new(11));
    }
}
