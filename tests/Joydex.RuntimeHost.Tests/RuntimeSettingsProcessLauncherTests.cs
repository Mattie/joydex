using System.Diagnostics;
using Joydex.Contracts;
using Joydex.Ipc;

namespace Joydex.RuntimeHost.Tests;

public sealed class RuntimeSettingsProcessLauncherTests
{
    [Fact]
    public async Task FirstOpenWaitsForAttachAndRepeatedOpenActivatesTheSameChild()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory();
        var tickets = new FakeTicketIssuer();
        await using var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            tickets,
            processes,
            attachTimeout: TimeSpan.FromSeconds(2),
            exitTimeout: TimeSpan.FromSeconds(1));
        var firstRequest = Request();

        var firstOpen = launcher.OpenAsync(firstRequest, CancellationToken.None);
        var child = await processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(firstOpen.IsCompleted);
        launcher.OnSettingsAttached("settings-connection");

        var firstResult = await firstOpen;
        var secondRequest = Request();
        var secondResult = await launcher.OpenAsync(secondRequest, CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Completed, firstResult.Status);
        Assert.Equal(RuntimeCommandStatus.Completed, secondResult.Status);
        Assert.Single(processes.StartInfos);
        Assert.Same(child, Assert.Single(tickets.Processes));
        var messages = await child.ReadMessagesAsync();
        Assert.Collection(
            messages,
            bootstrap =>
            {
                Assert.Equal(RuntimeSettingsChannelMessageKind.Bootstrap, bootstrap.Kind);
                Assert.Equal(selection.Root, bootstrap.DataRoot);
                Assert.Equal(selection.ConfigurationPath, bootstrap.ConfigurationPath);
                Assert.Equal(selection.Endpoint.PipeName, bootstrap.PipeName);
                Assert.Equal("settings-ticket", bootstrap.LaunchTicket);
            },
            activate => Assert.Equal(RuntimeSettingsChannelMessageKind.Activate, activate.Kind));

        var startInfo = Assert.Single(processes.StartInfos);
        Assert.Equal(selection.ApplicationPath, startInfo.FileName);
        Assert.Equal(["--settings"], startInfo.ArgumentList.Cast<string>());
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Normal, startInfo.WindowStyle);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(Path.GetDirectoryName(selection.ApplicationPath), startInfo.WorkingDirectory);
    }

    [Fact]
    public async Task AttachTimeoutReturnsFailureCleansExactChildAndAllowsRetry()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory();
        var tickets = new FakeTicketIssuer();
        await using var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            tickets,
            processes,
            attachTimeout: TimeSpan.FromMilliseconds(50),
            exitTimeout: TimeSpan.FromSeconds(1));
        tickets.AfterIssue = issueNumber =>
        {
            if (issueNumber == 2)
            {
                launcher.OnSettingsAttached("replacement-settings");
            }
        };

        var firstResult = await launcher.OpenAsync(Request(), CancellationToken.None);
        var first = processes.Processes[0];

        Assert.Equal(RuntimeCommandStatus.Failed, firstResult.Status);
        Assert.Equal(1, first.CloseInputCount);
        Assert.Equal(0, first.KillCount);
        Assert.Equal(1, first.DisposeCount);

        var secondResult = await launcher.OpenAsync(Request(), CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Completed, secondResult.Status);
        Assert.Equal(2, processes.Processes.Count);
    }

    [Fact]
    public async Task ChildExitBeforeAttachIsReportedAndReplacementCanStart()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory();
        await using var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            new FakeTicketIssuer(),
            processes,
            attachTimeout: TimeSpan.FromSeconds(2),
            exitTimeout: TimeSpan.FromSeconds(1));

        var open = launcher.OpenAsync(Request(), CancellationToken.None);
        var child = await processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        child.Exit();

        var result = await open;

        Assert.Equal(RuntimeCommandStatus.Failed, result.Status);
        Assert.Contains("exited before it attached", result.Detail, StringComparison.Ordinal);
        Assert.Equal(1, child.DisposeCount);
    }

    [Fact]
    public async Task RuntimeCancellationClosesAndDisposesOnlyTheOwnedChild()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory();
        var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            new FakeTicketIssuer(),
            processes,
            attachTimeout: TimeSpan.FromSeconds(2),
            exitTimeout: TimeSpan.FromSeconds(1));
        using var runtime = new CancellationTokenSource();

        var open = launcher.OpenAsync(Request(), runtime.Token);
        var child = await processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        await EventuallyAsync(() => child.DisposeCount == 1);
        Assert.Equal(1, child.CloseInputCount);
        Assert.Equal(0, child.KillCount);
        await launcher.DisposeAsync();
    }

    [Fact]
    public async Task LostRpcConnectionReattachesTheSameChildBeforeActivation()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory();
        var tickets = new FakeTicketIssuer();
        await using var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            tickets,
            processes,
            attachTimeout: TimeSpan.FromSeconds(2),
            exitTimeout: TimeSpan.FromSeconds(1));

        var firstOpen = launcher.OpenAsync(Request(), CancellationToken.None);
        var child = await processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        launcher.OnSettingsAttached("settings-connection-1");
        await firstOpen;

        launcher.OnSettingsDisconnected("settings-connection-1");
        await EventuallyAsync(() => tickets.Processes.Count == 2);
        var secondOpen = launcher.OpenAsync(Request(), CancellationToken.None);

        Assert.False(secondOpen.IsCompleted);
        launcher.OnSettingsAttached("settings-connection-2");
        var secondResult = await secondOpen;

        Assert.Equal(RuntimeCommandStatus.Completed, secondResult.Status);
        Assert.Single(processes.Processes);
        Assert.Equal([child, child], tickets.Processes);
        var messages = await child.ReadMessagesAsync();
        Assert.Collection(
            messages,
            bootstrap => Assert.Equal(
                RuntimeSettingsChannelMessageKind.Bootstrap,
                bootstrap.Kind),
            reconnect =>
            {
                Assert.Equal(RuntimeSettingsChannelMessageKind.Reconnect, reconnect.Kind);
                Assert.Equal("settings-ticket-2", reconnect.LaunchTicket);
                Assert.Null(reconnect.InstanceKind);
                Assert.Null(reconnect.DataRoot);
                Assert.Null(reconnect.ConfigurationPath);
                Assert.Null(reconnect.PipeName);
            },
            activate => Assert.Equal(
                RuntimeSettingsChannelMessageKind.Activate,
                activate.Kind));
    }

    [Fact]
    public async Task CleanupStillKillsAndDisposesWhenClosingInputFails()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory();
        var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            new FakeTicketIssuer(),
            processes,
            attachTimeout: TimeSpan.FromSeconds(2),
            exitTimeout: TimeSpan.FromMilliseconds(20));

        var open = launcher.OpenAsync(Request(), CancellationToken.None);
        var child = await processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        launcher.OnSettingsAttached("settings-connection");
        await open;
        child.ExitOnClose = false;
        child.CloseInputException = new InvalidOperationException("close failed");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await launcher.DisposeAsync());

        Assert.Equal("close failed", exception.Message);
        Assert.Equal(1, child.CloseInputCount);
        Assert.Equal(1, child.KillCount);
        Assert.Equal(1, child.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForTheSameChildCleanup()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory();
        var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            new FakeTicketIssuer(),
            processes,
            attachTimeout: TimeSpan.FromSeconds(2),
            exitTimeout: TimeSpan.FromSeconds(1));

        var open = launcher.OpenAsync(Request(), CancellationToken.None);
        var child = await processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        launcher.OnSettingsAttached("settings-connection");
        await open;
        child.DisposeBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstDisposal = launcher.DisposeAsync().AsTask();
        await EventuallyAsync(() => child.DisposeCount == 1);
        var secondDisposal = launcher.DisposeAsync().AsTask();

        Assert.False(firstDisposal.IsCompleted);
        Assert.False(secondDisposal.IsCompleted);
        child.DisposeBlocker.SetResult();
        await Task.WhenAll(firstDisposal, secondDisposal);
        Assert.Equal(1, child.CloseInputCount);
        Assert.Equal(1, child.DisposeCount);
    }

    [Fact]
    public async Task FailedReconnectRetainsDirtyChildAndLaterOpenRetriesTheSameProcess()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory();
        var tickets = new FakeTicketIssuer
        {
            BlockOnIssueNumber = 2,
            FailOnIssueNumber = 2,
        };
        var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            tickets,
            processes,
            attachTimeout: TimeSpan.FromSeconds(2),
            exitTimeout: TimeSpan.FromSeconds(1));

        var firstOpen = launcher.OpenAsync(Request(), CancellationToken.None);
        var child = await processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        launcher.OnSettingsAttached("settings-connection-1");
        await firstOpen;

        launcher.OnSettingsDisconnected("settings-connection-1");
        await tickets.BlockedIssueStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var failedOpen = launcher.OpenAsync(Request(), CancellationToken.None);
        tickets.ReleaseBlockedIssue.Set();
        var failedResult = await failedOpen;

        Assert.Equal(RuntimeCommandStatus.Failed, failedResult.Status);
        Assert.Contains("remains open", failedResult.Detail, StringComparison.Ordinal);
        Assert.Single(processes.Processes);
        Assert.Equal(0, child.CloseInputCount);
        Assert.Equal(0, child.KillCount);
        Assert.Equal(0, child.DisposeCount);
        Assert.False(child.HasExited);

        var retryOpen = launcher.OpenAsync(Request(), CancellationToken.None);
        await EventuallyAsync(() => tickets.Processes.Count == 3);
        Assert.False(retryOpen.IsCompleted);
        launcher.OnSettingsAttached("settings-connection-2");
        var retryResult = await retryOpen;

        Assert.Equal(RuntimeCommandStatus.Completed, retryResult.Status);
        Assert.Single(processes.Processes);
        Assert.Equal([child, child, child], tickets.Processes);
        var messages = await child.ReadMessagesAsync();
        Assert.Collection(
            messages,
            bootstrap => Assert.Equal(
                RuntimeSettingsChannelMessageKind.Bootstrap,
                bootstrap.Kind),
            reconnect =>
            {
                Assert.Equal(RuntimeSettingsChannelMessageKind.Reconnect, reconnect.Kind);
                Assert.Equal("settings-ticket-3", reconnect.LaunchTicket);
            },
            activate => Assert.Equal(
                RuntimeSettingsChannelMessageKind.Activate,
                activate.Kind));

        await launcher.DisposeAsync();
        Assert.Equal(1, child.CloseInputCount);
        Assert.Equal(0, child.KillCount);
        Assert.Equal(1, child.DisposeCount);
    }

    [Fact]
    public async Task FailedActivationRetainsTheAttachedChildForALaterRetry()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory();
        var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            new FakeTicketIssuer(),
            processes,
            attachTimeout: TimeSpan.FromSeconds(2),
            exitTimeout: TimeSpan.FromSeconds(1));

        var firstOpen = launcher.OpenAsync(Request(), CancellationToken.None);
        var child = await processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        launcher.OnSettingsAttached("settings-connection");
        await firstOpen;
        child.WriteException = new IOException("control channel failed");

        var failedResult = await launcher.OpenAsync(Request(), CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Failed, failedResult.Status);
        Assert.Contains("remains open", failedResult.Detail, StringComparison.Ordinal);
        Assert.Single(processes.Processes);
        Assert.Equal(0, child.CloseInputCount);
        Assert.Equal(0, child.DisposeCount);
        child.WriteException = null;

        var retryResult = await launcher.OpenAsync(Request(), CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Completed, retryResult.Status);
        Assert.Single(processes.Processes);
        await launcher.DisposeAsync();
    }

    [Fact]
    public async Task ReconnectDeadlineFailureRetriesTheSameChildOnTheNextOpen()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory();
        var tickets = new FakeTicketIssuer();
        var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            tickets,
            processes,
            attachTimeout: TimeSpan.FromMilliseconds(50),
            exitTimeout: TimeSpan.FromSeconds(1));
        // Attach at ticket issuance so successful attempts cannot lose a 50 ms race
        // against the test runner. The second attempt still exercises the deadline.
        tickets.AfterIssue = issueNumber =>
        {
            if (issueNumber is 1 or 3)
            {
                launcher.OnSettingsAttached($"settings-connection-{issueNumber}");
            }
        };

        var firstOpen = launcher.OpenAsync(Request(), CancellationToken.None);
        var child = await processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RuntimeCommandStatus.Completed, (await firstOpen).Status);

        launcher.OnSettingsDisconnected("settings-connection-1");
        var failedResult = await launcher.OpenAsync(Request(), CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Failed, failedResult.Status);
        Assert.Contains("remains open", failedResult.Detail, StringComparison.Ordinal);
        Assert.Single(processes.Processes);
        Assert.Equal(0, child.CloseInputCount);

        var retryResult = await launcher.OpenAsync(Request(), CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Completed, retryResult.Status);
        Assert.Equal(3, tickets.Processes.Count);
        Assert.Single(processes.Processes);
        await launcher.DisposeAsync();
    }

    [Fact]
    public async Task LateAttachOverridesAStaleReconnectDeadlineFailure()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory();
        var tickets = new FakeTicketIssuer();
        var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            tickets,
            processes,
            attachTimeout: TimeSpan.FromMilliseconds(50),
            exitTimeout: TimeSpan.FromSeconds(1));

        var firstOpen = launcher.OpenAsync(Request(), CancellationToken.None);
        var child = await processes.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        launcher.OnSettingsAttached("settings-connection-1");
        await firstOpen;

        launcher.OnSettingsDisconnected("settings-connection-1");
        var failedResult = await launcher.OpenAsync(Request(), CancellationToken.None);
        Assert.Equal(RuntimeCommandStatus.Failed, failedResult.Status);
        launcher.OnSettingsAttached("settings-connection-2");

        var activated = await launcher.OpenAsync(Request(), CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Completed, activated.Status);
        Assert.Equal(2, tickets.Processes.Count);
        Assert.Single(processes.Processes);
        var messages = await child.ReadMessagesAsync();
        Assert.Collection(
            messages,
            bootstrap => Assert.Equal(
                RuntimeSettingsChannelMessageKind.Bootstrap,
                bootstrap.Kind),
            reconnect => Assert.Equal(
                RuntimeSettingsChannelMessageKind.Reconnect,
                reconnect.Kind),
            activate => Assert.Equal(
                RuntimeSettingsChannelMessageKind.Activate,
                activate.Kind));
        await launcher.DisposeAsync();
    }

    [Fact]
    public async Task FailedInitialCleanupBlocksReplacementAndIsReportedByDisposal()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory
        {
            ConfigureProcess = process =>
            {
                process.ExitOnClose = false;
                process.KillException = new InvalidOperationException("kill failed");
            },
        };
        var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            new FakeTicketIssuer(),
            processes,
            attachTimeout: TimeSpan.FromMilliseconds(20),
            exitTimeout: TimeSpan.FromMilliseconds(20));

        var firstResult = await launcher.OpenAsync(Request(), CancellationToken.None);
        var child = Assert.Single(processes.Processes);
        var secondResult = await launcher.OpenAsync(Request(), CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Failed, firstResult.Status);
        Assert.Contains("cleanup could not be confirmed", firstResult.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RuntimeCommandStatus.Failed, secondResult.Status);
        Assert.Contains("cleanup could not be confirmed", secondResult.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Single(processes.Processes);
        Assert.False(child.HasExited);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await launcher.DisposeAsync());
        Assert.Equal("kill failed", exception.Message);
        Assert.Single(processes.Processes);
        Assert.Equal(1, child.DisposeCount);
    }

    [Fact]
    public async Task FailedFirstTicketCleanupRemainsOwnedAndBlocksReplacement()
    {
        var selection = Selection();
        var processes = new FakeProcessFactory
        {
            ConfigureProcess = process =>
            {
                process.ExitOnClose = false;
                process.KillException = new InvalidOperationException("kill failed");
            },
        };
        var launcher = new RuntimeSettingsProcessLauncher(
            selection.Endpoint,
            selection.Root,
            selection.ConfigurationPath,
            selection.ApplicationPath,
            new FakeTicketIssuer { FailOnIssueNumber = 1 },
            processes,
            attachTimeout: TimeSpan.FromSeconds(2),
            exitTimeout: TimeSpan.FromMilliseconds(20));

        var firstResult = await launcher.OpenAsync(Request(), CancellationToken.None);
        var child = Assert.Single(processes.Processes);
        var secondResult = await launcher.OpenAsync(Request(), CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Failed, firstResult.Status);
        Assert.Contains("ticket issue failed", firstResult.Detail, StringComparison.Ordinal);
        Assert.Contains("cleanup could not be confirmed", firstResult.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RuntimeCommandStatus.Failed, secondResult.Status);
        Assert.Contains("cleanup could not be confirmed", secondResult.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Single(processes.Processes);
        Assert.False(child.HasExited);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await launcher.DisposeAsync());
        Assert.Equal("kill failed", exception.Message);
        Assert.Single(processes.Processes);
        Assert.Equal(1, child.DisposeCount);
    }

    private static RuntimeCommandRequest Request() =>
        new(Guid.NewGuid(), RuntimeCommandKind.OpenSettings);

    private static SelectionValues Selection()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "joydex-settings-launcher",
            Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(root, "custom.json");
        var applicationPath = Path.Combine(root, "publish", "Joydex.App.exe");
        var endpoint = RuntimeIpcEndpoint.CreateSynthetic(
            root,
            configurationPath,
            $"joydex-settings-launcher-{Guid.NewGuid():N}");
        return new SelectionValues(root, configurationPath, applicationPath, endpoint);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed record SelectionValues(
        string Root,
        string ConfigurationPath,
        string ApplicationPath,
        RuntimeIpcEndpoint Endpoint);

    private sealed class FakeTicketIssuer : IRuntimeSettingsTicketIssuer
    {
        private readonly object _gate = new();
        private readonly List<IRuntimeSettingsProcess> _processes = [];

        public IReadOnlyList<IRuntimeSettingsProcess> Processes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _processes];
                }
            }
        }
        public int? BlockOnIssueNumber { get; init; }
        public int? FailOnIssueNumber { get; init; }
        public Action<int>? AfterIssue { get; set; }
        public TaskCompletionSource BlockedIssueStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseBlockedIssue { get; } = new(initialState: false);

        public RuntimeIpcLaunchTicket Issue(IRuntimeSettingsProcess process)
        {
            int issueNumber;
            lock (_gate)
            {
                _processes.Add(process);
                issueNumber = _processes.Count;
            }
            if (issueNumber == BlockOnIssueNumber)
            {
                BlockedIssueStarted.TrySetResult();
                // This is a deadlock guard; the test controls release explicitly.
                if (!ReleaseBlockedIssue.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The fake ticket issue was not released.");
                }
            }
            if (issueNumber == FailOnIssueNumber)
            {
                throw new InvalidOperationException("ticket issue failed");
            }
            AfterIssue?.Invoke(issueNumber);
            var suffix = issueNumber == 1 ? string.Empty : $"-{issueNumber}";
            return new RuntimeIpcLaunchTicket(
                "settings-ticket" + suffix,
                DateTimeOffset.UtcNow.AddMinutes(1));
        }
    }

    private sealed class FakeProcessFactory : IRuntimeSettingsProcessFactory
    {
        public List<ProcessStartInfo> StartInfos { get; } = [];
        public List<FakeProcess> Processes { get; } = [];
        public Action<FakeProcess>? ConfigureProcess { get; init; }
        public TaskCompletionSource<FakeProcess> Started { get; private set; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IRuntimeSettingsProcess Start(ProcessStartInfo startInfo)
        {
            StartInfos.Add(startInfo);
            var process = new FakeProcess(Processes.Count + 100);
            ConfigureProcess?.Invoke(process);
            Processes.Add(process);
            Started.TrySetResult(process);
            return process;
        }
    }

    private sealed class FakeProcess(int id) : IRuntimeSettingsProcess
    {
        private readonly FailingMemoryStream _input = new();
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _exited;

        public int Id { get; } = id;
        public bool HasExited => Volatile.Read(ref _exited) != 0;
        public Stream StandardInput => _input;
        public Task Completion => _completion.Task;
        public int CloseInputCount { get; private set; }
        public int KillCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool ExitOnClose { get; set; } = true;
        public Exception? CloseInputException { get; set; }
        public Exception? KillException { get; set; }
        public TaskCompletionSource? DisposeBlocker { get; set; }
        public Exception? WriteException
        {
            get => _input.WriteException;
            set => _input.WriteException = value;
        }

        public void CloseInput()
        {
            CloseInputCount++;
            if (CloseInputException is not null)
            {
                throw CloseInputException;
            }
            if (ExitOnClose)
            {
                Exit();
            }
        }

        public void Kill()
        {
            KillCount++;
            if (KillException is not null)
            {
                throw KillException;
            }
            Exit();
        }

        public void Exit()
        {
            Interlocked.Exchange(ref _exited, 1);
            _completion.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (DisposeBlocker is not null)
            {
                await DisposeBlocker.Task;
            }
        }

        public async Task<RuntimeSettingsChannelMessage[]> ReadMessagesAsync()
        {
            await using var copy = new MemoryStream(_input.ToArray());
            var messages = new List<RuntimeSettingsChannelMessage>();
            while (await RuntimeSettingsChannel.ReadAsync(copy) is { } message)
            {
                messages.Add(message);
            }
            return [.. messages];
        }

        private sealed class FailingMemoryStream : MemoryStream
        {
            public Exception? WriteException { get; set; }

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                return WriteException is null
                    ? base.WriteAsync(buffer, cancellationToken)
                    : ValueTask.FromException(WriteException);
            }
        }
    }
}
