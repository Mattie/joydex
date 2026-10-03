using Joydex.App;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class VoiceMediaStaHostTests
{
    [Fact]
    public void StartupFailureClassificationDistinguishesIncompleteOwnershipCleanup()
    {
        var startupFailure = new InvalidOperationException("startup failed");
        Assert.Same(
            startupFailure,
            VoiceRuntimeStartupRollback.ClassifyStartupFailure(startupFailure, []));

        var cleanupFailure = new IOException("cleanup failed");
        var terminal = Assert.IsType<VoiceOwnershipCleanupException>(
            VoiceRuntimeStartupRollback.ClassifyStartupFailure(
                startupFailure,
                [cleanupFailure]));
        Assert.Equal([startupFailure, cleanupFailure], terminal.InnerExceptions);
    }

    [Fact]
    public async Task StartsAnIndependentStaWithARunningMessagePump()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        await using var host = await VoiceMediaStaHost.StartAsync();

        var firstThread = 0;
        await host.InvokeAsync(() =>
        {
            firstThread = Environment.CurrentManagedThreadId;
            Assert.NotEqual(callerThread, firstThread);
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Assert.True(Application.MessageLoop);
            Assert.True(host.CheckAccess());
            Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);
            return Task.CompletedTask;
        });

        await host.InvokeAsync(() =>
        {
            Assert.Equal(firstThread, Environment.CurrentManagedThreadId);
            return Task.CompletedTask;
        });

        Assert.False(host.Completion.IsCompleted);
    }

    [Fact]
    public async Task CallerCancellationDoesNotStopTheMediaSta()
    {
        await using var host = await VoiceMediaStaHost.StartAsync(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blockingOperation = host.InvokeAsync(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(3)));
            return Task.CompletedTask;
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        using var cancellation = new CancellationTokenSource();
        var canceledOperation = host.InvokeAsync(() => Task.CompletedTask, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledOperation);

        release.Set();
        await blockingOperation;
        var laterOperationRan = false;
        await host.InvokeAsync(() =>
        {
            laterOperationRan = true;
            return Task.CompletedTask;
        });

        Assert.True(laterOperationRan);
        Assert.False(host.Completion.IsCompleted);
    }

    [Fact]
    public async Task CancellationDoesNotAbandonAnOperationAfterItStartsOnTheSta()
    {
        await using var host = await VoiceMediaStaHost.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var operation = host.InvokeAsync(
            async () =>
            {
                started.TrySetResult();
                await release.Task;
            },
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            cancellation.Cancel();
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await operation;
        Assert.False(host.Completion.IsCompleted);
    }

    [Fact]
    public async Task ShutdownFailsWhileTheOwningThreadCannotBeJoined()
    {
        var host = await VoiceMediaStaHost.StartAsync(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(200));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blockingOperation = host.InvokeAsync(() =>
        {
            entered.Set();
            release.Wait();
            return Task.CompletedTask;
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            var failure = await Assert.ThrowsAsync<TimeoutException>(
                () => host.DisposeAsync().AsTask());
            Assert.Contains("must not be replaced", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(host.IsThreadAlive);
        }
        finally
        {
            release.Set();
            await host.ThreadExited.WaitAsync(TimeSpan.FromSeconds(3));
        }

        Assert.True(SpinWait.SpinUntil(
            () => !host.IsThreadAlive,
            TimeSpan.FromSeconds(3)));
        await Assert.ThrowsAnyAsync<Exception>(() => blockingOperation);
        await Assert.ThrowsAnyAsync<Exception>(() => host.Completion);
    }

    [Fact]
    public async Task RuntimeGateCancelsAndDrainsOldGenerationPublications()
    {
        var gate = new VoiceRuntimeAsyncGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(gate.TryRun(async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                stopped.TrySetResult();
            }
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await gate.DisposeAsync();

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(gate.TryRun(_ => Task.CompletedTask));
        Assert.False(gate.TryPublish(CancellationToken.None, () => Assert.Fail("Stale publication ran.")));
    }

    [Fact]
    public async Task StartupRollbackDisposesEveryPartialOwnerInRuntimeOrder()
    {
        var order = new List<string>();
        var expectedFailure = new InvalidOperationException("adapter cleanup failed");

        var failures = await VoiceRuntimeStartupRollback.DisposeAsync(
            new RecordingDisposable("publications", order),
            new RecordingDisposable("adapter", order, expectedFailure),
            new RecordingDisposable("coordinator", order),
            new RecordingDisposable("owner", order),
            new RecordingDisposable("media-sta", order));

        Assert.Equal(
            ["publications", "adapter", "coordinator", "owner", "media-sta"],
            order);
        Assert.Equal(expectedFailure, Assert.Single(failures));
    }

    [Fact]
    public void RuntimeLoggingCannotInterruptVoiceOwnershipCleanup()
    {
        var calls = 0;
        var log = VoicePeBridgeRuntime.CreateBestEffortLog(_ =>
        {
            calls++;
            throw new InvalidOperationException("diagnostic sink failed");
        });

        var exception = Record.Exception(() => log("cleanup continues"));

        Assert.Null(exception);
        Assert.Equal(1, calls);
    }

    private sealed class RecordingDisposable(
        string name,
        List<string> order,
        Exception? failure = null) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            order.Add(name);
            return failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(failure);
        }
    }
}
