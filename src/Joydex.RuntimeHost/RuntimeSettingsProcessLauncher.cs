using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Text;
using Joydex.Contracts;
using Joydex.Ipc;

namespace Joydex.RuntimeHost;

/// <summary>Starts or activates the one Settings process owned by this runtime.</summary>
internal interface IRuntimeSettingsProcessLauncher
{
    Task<RuntimeCommandResult> OpenAsync(
        RuntimeCommandRequest request,
        CancellationToken runtimeCancellationToken);

    /// <summary>Completes the current child's startup after its Settings RPC attach succeeds.</summary>
    void OnSettingsAttached(string connectionId)
    {
    }

    /// <summary>Begins recovery when the current Settings RPC connection is lost.</summary>
    void OnSettingsDisconnected(string connectionId)
    {
    }
}

internal sealed class UnsupportedRuntimeSettingsProcessLauncher : IRuntimeSettingsProcessLauncher
{
    public static UnsupportedRuntimeSettingsProcessLauncher Instance { get; } = new();

    private UnsupportedRuntimeSettingsProcessLauncher()
    {
    }

    public Task<RuntimeCommandResult> OpenAsync(
        RuntimeCommandRequest request,
        CancellationToken runtimeCancellationToken)
    {
        runtimeCancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RuntimeCommandResult(
            request.OperationId,
            request.Kind,
            RuntimeCommandStatus.Rejected,
            "The Settings process is unavailable in this runtime."));
    }

    public void OnSettingsAttached(string connectionId)
    {
    }

    public void OnSettingsDisconnected(string connectionId)
    {
    }
}

/// <summary>
/// Owns the sole Settings child, its inherited control channel, and its attach-readiness boundary.
/// </summary>
internal sealed class RuntimeSettingsProcessLauncher :
    IRuntimeSettingsProcessLauncher,
    IAsyncDisposable
{
    private static readonly TimeSpan DefaultAttachTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultExitTimeout = TimeSpan.FromSeconds(5);

    private readonly object _stateGate = new();
    private readonly object _disposeGate = new();
    private readonly RuntimeIpcEndpoint _endpoint;
    private readonly string _dataRoot;
    private readonly string _configurationPath;
    private readonly string _applicationPath;
    private readonly IRuntimeSettingsProcessFactory _processFactory;
    private readonly IRuntimeSettingsTicketIssuer _ticketIssuer;
    private readonly TimeSpan _attachTimeout;
    private readonly TimeSpan _exitTimeout;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly SemaphoreSlim _channelWriteGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private ChildState? _child;
    private CancellationTokenRegistration _runtimeCancellationRegistration;
    private bool _runtimeCancellationRegistered;
    private Task? _disposalTask;
    private int _disposed;

    internal RuntimeSettingsProcessLauncher(
        RuntimeIpcEndpoint endpoint,
        string dataRoot,
        string configurationPath,
        string applicationPath,
        IRuntimeSettingsTicketIssuer ticketIssuer,
        IRuntimeSettingsProcessFactory? processFactory = null,
        TimeSpan? attachTimeout = null,
        TimeSpan? exitTimeout = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        _ticketIssuer = ticketIssuer ?? throw new ArgumentNullException(nameof(ticketIssuer));
        _dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot.Trim()));
        _configurationPath = Path.GetFullPath(configurationPath.Trim());
        _applicationPath = Path.GetFullPath(applicationPath.Trim());
        _processFactory = processFactory ?? WindowsRuntimeSettingsProcessFactory.Instance;
        _attachTimeout = attachTimeout ?? DefaultAttachTimeout;
        _exitTimeout = exitTimeout ?? DefaultExitTimeout;
        if (_attachTimeout <= TimeSpan.Zero || _attachTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(attachTimeout));
        }
        if (_exitTimeout <= TimeSpan.Zero || _exitTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(exitTimeout));
        }

        var configurationRoot = Path.GetDirectoryName(_configurationPath);
        if (!string.Equals(_dataRoot, configurationRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The Settings configuration must be inside the selected data root.",
                nameof(configurationPath));
        }
    }

    public async Task<RuntimeCommandResult> OpenAsync(
        RuntimeCommandRequest request,
        CancellationToken runtimeCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        runtimeCancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        RegisterRuntimeCancellation(runtimeCancellationToken);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            runtimeCancellationToken,
            _disposeCancellation.Token);
        await _openGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            while (CurrentChild() is { } existingChild)
            {
                if (existingChild.CleanupTask is { IsFaulted: true } failedCleanup)
                {
                    return Failed(
                        request,
                        "The previous Settings process cleanup could not be confirmed: "
                        + CleanupDetail(failedCleanup.Exception));
                }
                if (existingChild.Process.HasExited)
                {
                    try
                    {
                        await StopExactChildAsync(existingChild).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        return Failed(
                            request,
                            "The exited Settings process cleanup could not be confirmed: "
                            + exception.Message);
                    }
                    continue;
                }

                var reconnect = existingChild.ReconnectTask;
                if (reconnect is not null)
                {
                    if (reconnect.IsCompleted
                        && !reconnect.IsCompletedSuccessfully)
                    {
                        _ = reconnect.Exception;
                        if (existingChild.Ready.Task.IsCompletedSuccessfully)
                        {
                            reconnect = Task.CompletedTask;
                        }
                        else
                        {
                            reconnect = RetryReconnect(existingChild, reconnect);
                        }
                    }
                    try
                    {
                        await reconnect.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        if (existingChild.Process.HasExited)
                        {
                            try
                            {
                                await StopExactChildAsync(existingChild).ConfigureAwait(false);
                            }
                            catch (Exception cleanupFailure)
                            {
                                return Failed(
                                    request,
                                    "The exited Settings process cleanup could not be confirmed: "
                                    + cleanupFailure.Message);
                            }
                            continue;
                        }
                        return Failed(
                            request,
                            "The existing Settings process remains open but could not reconnect: "
                            + exception.Message);
                    }
                    if (!ReferenceEquals(CurrentChild(), existingChild))
                    {
                        continue;
                    }
                }

                var ready = existingChild.Ready;
                if (!ready.Task.IsCompletedSuccessfully)
                {
                    return await WaitForReadyAsync(
                            request,
                            existingChild,
                            ready,
                            linkedCancellation.Token)
                        .ConfigureAwait(false);
                }

                try
                {
                    await WriteMessageAsync(
                            existingChild,
                            RuntimeSettingsChannel.CreateActivate(),
                            linkedCancellation.Token)
                        .ConfigureAwait(false);
                    if (!ReferenceEquals(existingChild.Ready, ready)
                        || !ready.Task.IsCompletedSuccessfully)
                    {
                        continue;
                    }
                    return Completed(request, "The existing Settings process was activated.");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (existingChild.Process.HasExited)
                    {
                        try
                        {
                            await StopExactChildAsync(existingChild).ConfigureAwait(false);
                        }
                        catch (Exception cleanupFailure)
                        {
                            return Failed(
                                request,
                                "The exited Settings process cleanup could not be confirmed: "
                                + cleanupFailure.Message);
                        }
                        continue;
                    }
                    return Failed(
                        request,
                        "The existing Settings process remains open but could not be activated: "
                        + exception.Message);
                }
            }

            ChildState? child = null;
            try
            {
                child = StartChild();
                SetCurrentChild(child);
                _ = ObserveExitAsync(child);
                var ticket = _ticketIssuer.Issue(child.Process);
                var bootstrap = RuntimeSettingsChannel.CreateBootstrap(
                    _endpoint,
                    _dataRoot,
                    _configurationPath,
                    ticket.Value);
                await WriteMessageAsync(
                        child,
                        bootstrap,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (child is not null)
                {
                    return await FailAndStopAsync(
                            request,
                            child,
                            "The Settings process could not start: " + exception.Message)
                        .ConfigureAwait(false);
                }
                return Failed(request, "The Settings process could not start: " + exception.Message);
            }

            return await WaitForReadyAsync(
                    request,
                    child,
                    child.Ready,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _openGate.Release();
        }
    }

    public void OnSettingsAttached(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        lock (_stateGate)
        {
            if (_disposed != 0 || _child is null || _child.Process.HasExited)
            {
                return;
            }
            _child.ConnectionId ??= connectionId;
            if (string.Equals(_child.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                _child.EverAttached = true;
                _child.Ready.TrySetResult();
            }
        }
    }

    public void OnSettingsDisconnected(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        lock (_stateGate)
        {
            if (_disposed != 0
                || _child is null
                || _child.Process.HasExited
                || !string.Equals(_child.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                return;
            }

            _child.ConnectionId = null;
            _child.Ready = NewReadySource();
            _child.ReconnectTask = ReconnectAsync(_child, _child.Ready);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposalTask is null)
            {
                Interlocked.Exchange(ref _disposed, 1);
                _disposalTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? failures = null;
        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        await _openGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ChildState? child;
            lock (_stateGate)
            {
                child = _child;
                _child = null;
            }
            Task? reconnect = null;
            if (child is not null)
            {
                reconnect = child.ReconnectTask;
                try
                {
                    await CleanupChildAsync(child, requestStop: true).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }
            if (reconnect is not null)
            {
                try
                {
                    await reconnect.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Reconnect failures are operational; exact-child cleanup is tracked separately.
                }
            }
        }
        finally
        {
            _openGate.Release();
            _runtimeCancellationRegistration.Dispose();
            _disposeCancellation.Dispose();
            _openGate.Dispose();
            _channelWriteGate.Dispose();
        }

        ThrowCleanupFailures(failures);
    }

    internal static ProcessStartInfo CreateStartInfo(string applicationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        var fullPath = Path.GetFullPath(applicationPath.Trim());
        var startInfo = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // STARTUPINFO controls the first WinForms Show call. Settings must start visible.
            WindowStyle = ProcessWindowStyle.Normal,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
            WorkingDirectory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException(
                    "The Settings executable has no deployment directory."),
        };
        startInfo.ArgumentList.Add("--settings");
        return startInfo;
    }

    private ChildState? CurrentChild()
    {
        lock (_stateGate)
        {
            return _child;
        }
    }

    private ChildState StartChild()
    {
        var process = _processFactory.Start(CreateStartInfo(_applicationPath));
        return new ChildState(process);
    }

    private void SetCurrentChild(ChildState child)
    {
        lock (_stateGate)
        {
            if (_child is not null)
            {
                throw new InvalidOperationException("A Settings process is already owned.");
            }
            _child = child;
        }
    }

    private async Task<RuntimeCommandResult> WaitForReadyAsync(
        RuntimeCommandRequest request,
        ChildState child,
        TaskCompletionSource ready,
        CancellationToken cancellationToken)
    {
        var timeout = Task.Delay(_attachTimeout, cancellationToken);
        var completed = await Task.WhenAny(
                ready.Task,
                child.Process.Completion,
                timeout)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (ReferenceEquals(completed, ready.Task)
            && !child.Process.HasExited)
        {
            await ready.Task.ConfigureAwait(false);
            return Completed(request, "The Settings process attached and opened.");
        }

        var detail = ReferenceEquals(completed, child.Process.Completion)
            ? "The Settings process exited before it attached."
            : "The Settings process did not attach before its startup deadline.";
        if (child.EverAttached && !child.Process.HasExited)
        {
            return Failed(request, detail);
        }
        return await FailAndStopAsync(request, child, detail).ConfigureAwait(false);
    }

    private async Task ReconnectAsync(ChildState child, TaskCompletionSource ready)
    {
        await Task.Yield();
        if (!ReferenceEquals(CurrentChild(), child) || child.Process.HasExited)
        {
            return;
        }

        var ticket = _ticketIssuer.Issue(child.Process);
        await WriteMessageAsync(
                child,
                RuntimeSettingsChannel.CreateReconnect(ticket.Value),
                _disposeCancellation.Token)
            .ConfigureAwait(false);

        var timeout = Task.Delay(_attachTimeout, _disposeCancellation.Token);
        var completed = await Task.WhenAny(
                ready.Task,
                child.Process.Completion,
                timeout)
            .ConfigureAwait(false);
        _disposeCancellation.Token.ThrowIfCancellationRequested();
        if (ReferenceEquals(completed, ready.Task)
            && !child.Process.HasExited)
        {
            await ready.Task.ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException(
            ReferenceEquals(completed, child.Process.Completion)
                ? "The Settings process exited before reconnecting."
                : "The Settings process did not reconnect before its deadline.");
    }

    private Task RetryReconnect(ChildState child, Task completedAttempt)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_child, child)
                && child.Ready.Task.IsCompletedSuccessfully)
            {
                return Task.CompletedTask;
            }
            if (!ReferenceEquals(_child, child)
                || !ReferenceEquals(child.ReconnectTask, completedAttempt)
                || child.Process.HasExited)
            {
                return child.ReconnectTask ?? completedAttempt;
            }

            child.ReconnectTask = ReconnectAsync(child, child.Ready);
            return child.ReconnectTask;
        }
    }

    private async Task WriteMessageAsync(
        ChildState child,
        RuntimeSettingsChannelMessage message,
        CancellationToken cancellationToken)
    {
        await _channelWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (child.Process.HasExited)
            {
                throw new InvalidOperationException("The Settings process has exited.");
            }
            await RuntimeSettingsChannel.WriteAsync(
                    child.Process.StandardInput,
                    message,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _channelWriteGate.Release();
        }
    }

    private async Task ObserveExitAsync(ChildState child)
    {
        try
        {
            await child.Process.Completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Process exit remains authoritative even if the platform wait reports a failure.
        }

        var ownsChild = false;
        lock (_stateGate)
        {
            if (ReferenceEquals(_child, child))
            {
                ownsChild = true;
            }
        }
        if (ownsChild)
        {
            var cleanupSucceeded = false;
            try
            {
                await CleanupChildAsync(
                        child,
                        requestStop: !child.Process.HasExited)
                    .ConfigureAwait(false);
                cleanupSucceeded = true;
            }
            catch (Exception)
            {
                // Retaining this child lets Open and Dispose report the shared cleanup failure.
            }
            finally
            {
                lock (_stateGate)
                {
                    if (cleanupSucceeded && ReferenceEquals(_child, child))
                    {
                        _child = null;
                    }
                }
            }
        }
    }

    private async Task StopExactChildAsync(ChildState child)
    {
        await CleanupChildAsync(child, requestStop: true).ConfigureAwait(false);
        lock (_stateGate)
        {
            if (ReferenceEquals(_child, child))
            {
                _child = null;
            }
        }
    }

    private async Task<RuntimeCommandResult> FailAndStopAsync(
        RuntimeCommandRequest request,
        ChildState child,
        string detail)
    {
        try
        {
            await StopExactChildAsync(child).ConfigureAwait(false);
            return Failed(request, detail);
        }
        catch (Exception cleanupFailure)
        {
            return Failed(
                request,
                detail + " Cleanup could not be confirmed: " + cleanupFailure.Message);
        }
    }

    private Task CleanupChildAsync(ChildState child, bool requestStop)
    {
        lock (child)
        {
            child.CleanupTask ??= requestStop
                ? StopProcessAsync(child.Process)
                : child.Process.DisposeAsync().AsTask();
            return child.CleanupTask;
        }
    }

    private async Task StopProcessAsync(IRuntimeSettingsProcess process)
    {
        List<Exception>? failures = null;
        try
        {
            process.CloseInput();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        var completionObserved = false;
        try
        {
            await process.Completion.WaitAsync(_exitTimeout).ConfigureAwait(false);
            completionObserved = true;
        }
        catch (TimeoutException)
        {
            // A graceful-exit deadline is expected to fall back to a process-tree kill.
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (!completionObserved)
        {
            var killed = false;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    killed = true;
                }
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                if (killed)
                {
                    await process.Completion.WaitAsync(_exitTimeout).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        try
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        ThrowCleanupFailures(failures);
    }

    private void RegisterRuntimeCancellation(CancellationToken runtimeCancellationToken)
    {
        lock (_stateGate)
        {
            if (_runtimeCancellationRegistered)
            {
                return;
            }
            _runtimeCancellationRegistered = true;
            _runtimeCancellationRegistration = runtimeCancellationToken.UnsafeRegister(
                static state =>
                {
                    var launcher = (RuntimeSettingsProcessLauncher)state!;
                    var disposal = launcher.DisposeAsync().AsTask();
                    _ = disposal.ContinueWith(
                        static completed => _ = completed.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted
                        | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                },
                this);
        }
    }

    private static RuntimeCommandResult Completed(RuntimeCommandRequest request, string detail) =>
        new(
            request.OperationId,
            request.Kind,
            RuntimeCommandStatus.Completed,
            detail);

    private static RuntimeCommandResult Failed(RuntimeCommandRequest request, string detail) =>
        new(
            request.OperationId,
            request.Kind,
            RuntimeCommandStatus.Failed,
            detail);

    private static void ThrowCleanupFailures(List<Exception>? failures)
    {
        if (failures is { Count: 1 })
        {
            throw failures[0];
        }
        if (failures is { Count: > 1 })
        {
            throw new AggregateException(
                "The Settings process could not be stopped cleanly.",
                failures);
        }
    }

    private static string CleanupDetail(AggregateException? exception) =>
        exception?.GetBaseException().Message ?? "unknown cleanup failure";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private sealed class ChildState(IRuntimeSettingsProcess process)
    {
        public IRuntimeSettingsProcess Process { get; } = process;

        public TaskCompletionSource Ready { get; set; } = NewReadySource();

        public string? ConnectionId { get; set; }

        public volatile bool EverAttached;

        public Task? CleanupTask { get; set; }

        public Task? ReconnectTask { get; set; }
    }

    private static TaskCompletionSource NewReadySource() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);
}

internal interface IRuntimeSettingsProcessFactory
{
    IRuntimeSettingsProcess Start(ProcessStartInfo startInfo);
}

internal interface IRuntimeSettingsTicketIssuer
{
    RuntimeIpcLaunchTicket Issue(IRuntimeSettingsProcess process);
}

internal interface IRuntimeSettingsProcess : IAsyncDisposable
{
    int Id { get; }

    bool HasExited { get; }

    Stream StandardInput { get; }

    Task Completion { get; }

    void CloseInput();

    void Kill();
}

internal interface IRuntimeWorkerProcess
{
    SafeProcessHandle ProcessHandle { get; }

    long ProcessStartTimeUtcTicks { get; }

    int SessionId { get; }
}

internal sealed class RuntimeIpcSettingsTicketIssuer(RuntimeIpcServer server) :
    IRuntimeSettingsTicketIssuer
{
    private readonly RuntimeIpcServer _server =
        server ?? throw new ArgumentNullException(nameof(server));

    public RuntimeIpcLaunchTicket Issue(IRuntimeSettingsProcess process)
    {
        if (process is not WindowsRuntimeSettingsProcess windowsProcess)
        {
            throw new InvalidOperationException(
                "The production ticket issuer requires a native Settings process.");
        }
        return _server.IssueLaunchTicket(
            RuntimeClientKind.Settings,
            windowsProcess.Process);
    }
}

internal sealed class WindowsRuntimeSettingsProcessFactory : IRuntimeSettingsProcessFactory
{
    public static WindowsRuntimeSettingsProcessFactory Instance { get; } = new();

    private WindowsRuntimeSettingsProcessFactory()
    {
    }

    public IRuntimeSettingsProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Settings process did not start.");
        try
        {
            return new WindowsRuntimeSettingsProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

internal sealed class WindowsRuntimeSettingsProcess : IRuntimeSettingsProcess, IRuntimeWorkerProcess
{
    private readonly Task _completion;
    private readonly Task _standardOutput;
    private readonly Task _standardError;
    private int _disposed;

    public WindowsRuntimeSettingsProcess(Process process)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        _completion = process.WaitForExitAsync();
        _standardOutput = process.StandardOutput.ReadToEndAsync();
        _standardError = process.StandardError.ReadToEndAsync();
    }

    internal Process Process { get; }

    public int Id => Process.Id;

    public bool HasExited => Process.HasExited;

    public Stream StandardInput => Process.StandardInput.BaseStream;

    public Task Completion => _completion;

    SafeProcessHandle IRuntimeWorkerProcess.ProcessHandle => Process.SafeHandle;

    long IRuntimeWorkerProcess.ProcessStartTimeUtcTicks =>
        Process.StartTime.ToUniversalTime().Ticks;

    int IRuntimeWorkerProcess.SessionId => Process.SessionId;

    public void CloseInput()
    {
        try
        {
            Process.StandardInput.Close();
        }
        catch (Exception) when (Process.HasExited)
        {
        }
    }

    public void Kill()
    {
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_completion.IsCompleted)
        {
            try
            {
                await Task.WhenAll(_completion, _standardOutput, _standardError)
                    .ConfigureAwait(false);
            }
            finally
            {
                Process.Dispose();
            }
            return;
        }

        Process.Dispose();
        ObserveFailure(_completion);
        ObserveFailure(_standardOutput);
        ObserveFailure(_standardError);
    }

    private static void ObserveFailure(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
            | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
