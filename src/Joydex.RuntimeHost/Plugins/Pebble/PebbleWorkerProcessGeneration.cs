using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Joydex.Contracts;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.RuntimeHost.Plugins;
using Joydex.RuntimeHost.Production;
using StreamJsonRpc;

namespace Joydex.RuntimeHost.Plugins.Pebble;

internal sealed record PebbleWorkerGenerationConfiguration(
    long Generation,
    PebbleIndexPreferences Preferences,
    string Secret,
    string Inbox,
    string DesktopBridgePipeName);

internal interface IPebbleWorkerHostCallbacks
{
    void PublishStatus(PebbleWorkerStatus status);

    void WriteLog(long generation, string message);
}

internal interface IPebbleWorkerGeneration : IAsyncDisposable
{
    long Generation { get; }

    PebbleWorkerStatus Status { get; }

    Task Completion { get; }

    PebbleWorkerStatus ActivateCallbacks();

    void DeactivateCallbacks();
}

internal interface IPebbleWorkerGenerationFactory
{
    Task<IPebbleWorkerGeneration> StartAsync(
        PebbleWorkerGenerationConfiguration configuration,
        IPebbleWorkerHostCallbacks callbacks,
        CancellationToken cancellationToken);
}

internal sealed class PebbleWorkerProcessGenerationFactory(
    string executablePath,
    IRuntimeSettingsProcessFactory processFactory,
    Func<IWorkerProcessJob>? jobFactory = null) : IPebbleWorkerGenerationFactory
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private readonly string _executablePath = Path.GetFullPath(
        executablePath ?? throw new ArgumentNullException(nameof(executablePath)));
    private readonly IRuntimeSettingsProcessFactory _processFactory =
        processFactory ?? throw new ArgumentNullException(nameof(processFactory));
    private readonly Func<IWorkerProcessJob> _jobFactory = jobFactory ?? WorkerProcessJob.Create;

    public async Task<IPebbleWorkerGeneration> StartAsync(
        PebbleWorkerGenerationConfiguration configuration,
        IPebbleWorkerHostCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(callbacks);
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.Generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }

        var pipeName = "Joydex.PebbleWorker." + Guid.NewGuid().ToString("N");
        var capability = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        NamedPipeServerStream? pipe = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        IWorkerProcessJob? job = null;
        IRuntimeSettingsProcess? process = null;
        JsonRpc? rpc = null;
        BoundedMessageStream? bounded = null;
        using var startupCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCancellation.CancelAfter(StartTimeout);
        try
        {
            job = _jobFactory();
            process = _processFactory.Start(CreateStartInfo());
            if (process is not IRuntimeWorkerProcess nativeProcess)
            {
                throw new InvalidOperationException(
                    "The production Pebble worker requires a native process owner.");
            }
            job.Assign(nativeProcess);
            var processStartTicks = nativeProcess.ProcessStartTimeUtcTicks;
            using var hostProcess = Process.GetCurrentProcess();
            var ticket = new PebbleWorkerLaunchTicket(
                pipeName,
                capability,
                configuration.Generation,
                PebbleWorkerProtocol.MajorVersion,
                PebbleWorkerProtocol.MinorVersion,
                hostProcess.Id,
                hostProcess.StartTime.ToUniversalTime().Ticks,
                hostProcess.SessionId);
            await JsonSerializer.SerializeAsync(
                    process.StandardInput,
                    ticket,
                    cancellationToken: startupCancellation.Token)
                .ConfigureAwait(false);
            await process.StandardInput.WriteAsync("\n"u8.ToArray(), startupCancellation.Token)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(startupCancellation.Token).ConfigureAwait(false);
            process.CloseInput();

            await pipe.WaitForConnectionAsync(startupCancellation.Token).ConfigureAwait(false);
            _ = WindowsPipePeerVerifier.VerifyClient(
                pipe,
                hostProcess.SessionId,
                process.Id,
                processStartTicks);

            (rpc, bounded) = RuntimeJsonRpc.Create(pipe);
            var callbackTarget = new PebbleWorkerHostRpcTarget(
                configuration.Generation,
                callbacks);
            rpc.AddLocalRpcTarget(callbackTarget);
            rpc.StartListening();
            var response = await rpc.InvokeWithCancellationAsync<PebbleWorkerStartResponse>(
                    PebbleWorkerProtocol.Start,
                    [CreateStartRequest(configuration, capability)],
                    startupCancellation.Token)
                .ConfigureAwait(false);
            ValidateStartResponse(configuration.Generation, response);
            if (response.Failure is { } failure)
            {
                throw new PebbleWorkerStartupException(failure.Kind, failure.Detail);
            }
            callbackTarget.SetStartStatus(response.Status!);
            var generation = new PebbleWorkerProcessGeneration(
                configuration.Generation,
                process,
                job,
                pipe,
                rpc,
                bounded,
                callbackTarget,
                StopTimeout);
            process = null;
            job = null;
            rpc = null;
            bounded = null;
            pipe = null;
            return generation;
        }
        catch (Exception startupFailure)
        {
            var cleanupFailure = await CleanupFailedStartAsync(
                    process,
                    job,
                    rpc,
                    bounded,
                    pipe)
                .ConfigureAwait(false);
            if (cleanupFailure is not null)
            {
                throw new ProductionOwnershipCleanupException(
                    "Pebble worker startup failed and generation cleanup was not confirmed.",
                    [startupFailure, cleanupFailure]);
            }
            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo() => new()
    {
        FileName = _executablePath,
        Arguments = "--pebble-worker",
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    private static PebbleWorkerStartRequest CreateStartRequest(
        PebbleWorkerGenerationConfiguration configuration,
        string capability)
    {
        var value = configuration.Preferences.Normalize();
        return new PebbleWorkerStartRequest(
            capability,
            configuration.Generation,
            PebbleWorkerProtocol.MajorVersion,
            PebbleWorkerProtocol.MinorVersion,
            new PebbleWorkerPreferences(
                value.SchemaVersion,
                value.Enabled,
                value.Port,
                value.TargetTaskId,
                value.TargetHostId,
                value.TargetTaskLabel),
            new PebbleWorkerPaths(
                configuration.Secret,
                configuration.Inbox,
                configuration.DesktopBridgePipeName));
    }

    internal static void ValidateStartResponse(
        long generation,
        PebbleWorkerStartResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.ProtocolMajor != PebbleWorkerProtocol.MajorVersion
            || response.ProtocolMinor is < 0 or > PebbleWorkerProtocol.MinorVersion
            || (response.Status is null) == (response.Failure is null)
            || response.Failure is { } failure
                && (!Enum.IsDefined(failure.Kind)
                    || string.IsNullOrWhiteSpace(failure.Detail)
                    || failure.Detail.Length > RuntimeUiLimits.MaximumStatusCharacters))
        {
            throw new InvalidDataException("The Pebble worker returned an invalid Start response.");
        }
        if (response.Status is not null)
        {
            PebbleWorkerStatusValidator.Validate(response.Status, generation, requireRunning: true);
        }
    }

    private static async Task<Exception?> CleanupFailedStartAsync(
        IRuntimeSettingsProcess? process,
        IWorkerProcessJob? job,
        JsonRpc? rpc,
        BoundedMessageStream? bounded,
        NamedPipeServerStream? pipe)
    {
        List<Exception>? confirmationFailures = null;
        try { rpc?.Dispose(); } catch { }
        try { bounded?.Dispose(); } catch { }
        try { pipe?.Dispose(); } catch { }
        if (job is not null)
        {
            try { job.Terminate(); } catch { }
        }
        if (process is not null)
        {
            try { process.Kill(); } catch { }
            try
            {
                using var timeout = new CancellationTokenSource(StopTimeout);
                await process.Completion.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) { (confirmationFailures ??= []).Add(exception); }
        }
        if (job is not null)
        {
            try
            {
                await WorkerProcessJob.WaitForEmptyAsync(job, StopTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) { (confirmationFailures ??= []).Add(exception); }
            job.Dispose();
        }
        if (process is not null)
        {
            try { await process.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        return confirmationFailures is null ? null : new AggregateException(confirmationFailures);
    }
}

internal sealed class PebbleWorkerProcessGeneration : IPebbleWorkerGeneration
{
    private readonly IRuntimeSettingsProcess _process;
    private readonly IWorkerProcessJob _job;
    private readonly NamedPipeServerStream _pipe;
    private readonly JsonRpc _rpc;
    private readonly BoundedMessageStream _bounded;
    private readonly PebbleWorkerHostRpcTarget _callbacks;
    private readonly TimeSpan _stopTimeout;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public PebbleWorkerProcessGeneration(
        long generation,
        IRuntimeSettingsProcess process,
        IWorkerProcessJob job,
        NamedPipeServerStream pipe,
        JsonRpc rpc,
        BoundedMessageStream bounded,
        PebbleWorkerHostRpcTarget callbacks,
        TimeSpan stopTimeout)
    {
        Generation = generation;
        _process = process;
        _job = job;
        _pipe = pipe;
        _rpc = rpc;
        _bounded = bounded;
        _callbacks = callbacks;
        _stopTimeout = stopTimeout;
        Completion = Task.WhenAny(process.Completion, rpc.Completion).Unwrap();
    }

    public long Generation { get; }

    public PebbleWorkerStatus Status => _callbacks.CurrentStatus;

    public Task Completion { get; }

    public PebbleWorkerStatus ActivateCallbacks() => _callbacks.EnableAndGetStatus();

    public void DeactivateCallbacks() => _callbacks.Disable();

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _callbacks.Disable();
        try
        {
            using var graceful = new CancellationTokenSource(_stopTimeout);
            await _rpc.InvokeWithCancellationAsync(
                    PebbleWorkerProtocol.Stop,
                    [new PebbleWorkerGenerationMessage(Generation)],
                    graceful.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // Forced generation cleanup below remains authoritative.
        }

        List<Exception>? confirmationFailures = null;
        try { _rpc.Dispose(); } catch { }
        try { _bounded.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }
        try
        {
            if (_job.ActiveProcessCount != 0)
            {
                _job.Terminate();
            }
        }
        catch { }
        try
        {
            using var cleanup = new CancellationTokenSource(_stopTimeout);
            await Task.WhenAll(
                    _process.Completion.WaitAsync(cleanup.Token),
                    WorkerProcessJob.WaitForEmptyAsync(_job, _stopTimeout, cleanup.Token))
                .ConfigureAwait(false);
        }
        catch (Exception exception) { (confirmationFailures ??= []).Add(exception); }
        _job.Dispose();
        try { await _process.DisposeAsync().ConfigureAwait(false); } catch { }
        if (confirmationFailures is not null)
        {
            throw new ProductionOwnershipCleanupException(
                "Pebble worker generation cleanup was not confirmed.",
                confirmationFailures);
        }
    }
}

internal sealed class PebbleWorkerStartupException(
    PebbleWorkerStartFailureKind kind,
    string message) : Exception(message)
{
    public PebbleWorkerStartFailureKind Kind { get; } = kind;
}

internal sealed class PebbleWorkerHostRpcTarget(
    long generation,
    IPebbleWorkerHostCallbacks callbacks)
{
    private readonly object _gate = new();
    private PebbleWorkerStatus? _current;
    private bool _enabled;
    private bool _disabled;

    public PebbleWorkerStatus CurrentStatus
    {
        get
        {
            lock (_gate)
            {
                return _current
                    ?? throw new InvalidOperationException("The Pebble worker has no initial status.");
            }
        }
    }

    public void SetStartStatus(PebbleWorkerStatus status)
    {
        PebbleWorkerStatusValidator.Validate(status, generation, requireRunning: true);
        lock (_gate)
        {
            if (!_disabled && (_current is null || status.Sequence > _current.Sequence))
            {
                _current = status;
            }
        }
    }

    public PebbleWorkerStatus EnableAndGetStatus()
    {
        lock (_gate)
        {
            if (_disabled)
            {
                throw new ObjectDisposedException(nameof(PebbleWorkerHostRpcTarget));
            }
            _enabled = true;
            return _current
                ?? throw new InvalidOperationException("The Pebble worker has no initial status.");
        }
    }

    public void Disable()
    {
        lock (_gate)
        {
            _disabled = true;
            _enabled = false;
        }
    }

    [JsonRpcMethod(PebbleWorkerProtocol.PublishStatus)]
    public Task PublishStatusAsync(PebbleWorkerStatus status)
    {
        if (status.Generation != generation)
        {
            return Task.CompletedTask;
        }
        PebbleWorkerStatusValidator.Validate(status, generation, requireRunning: false);
        var publish = false;
        lock (_gate)
        {
            if (!_disabled && (_current is null || status.Sequence > _current.Sequence))
            {
                _current = status;
                publish = _enabled;
            }
        }
        if (publish)
        {
            callbacks.PublishStatus(status);
        }
        return Task.CompletedTask;
    }

    [JsonRpcMethod(PebbleWorkerProtocol.Log)]
    public Task LogAsync(PebbleWorkerLogMessage message)
    {
        if (IsEnabled(message.Generation)
            && message.Message is not null
            && message.Message.Length <= RuntimeUiLimits.MaximumStatusCharacters)
        {
            callbacks.WriteLog(generation, message.Message);
        }
        return Task.CompletedTask;
    }

    private bool IsEnabled(long messageGeneration)
    {
        lock (_gate)
        {
            return !_disabled && _enabled && messageGeneration == generation;
        }
    }
}

internal static class PebbleWorkerStatusValidator
{
    private const int MaximumOutstandingCount = 100_000;

    public static void Validate(
        PebbleWorkerStatus status,
        long expectedGeneration,
        bool requireRunning)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.Generation != expectedGeneration
            || status.Sequence <= 0
            || requireRunning && !status.Running
            || string.IsNullOrWhiteSpace(status.Message)
            || status.Message.Length > RuntimeUiLimits.MaximumStatusCharacters
            || status.OutstandingCount is < 0 or > MaximumOutstandingCount
            || status.Latest is { } latest && InvalidDelivery(latest))
        {
            throw new InvalidDataException("The Pebble worker returned an invalid bounded status.");
        }
    }

    private static bool InvalidDelivery(PebbleWorkerDeliveryStatus value) =>
        !IsCanonicalStoredId(value.HashedId)
        || !Enum.IsDefined(value.State)
        || string.IsNullOrWhiteSpace(value.Detail)
        || value.Detail.Length > RuntimeUiLimits.MaximumStatusCharacters;

    private static bool IsCanonicalStoredId(string? id)
    {
        if (id is null)
        {
            return false;
        }
        var hash = id.StartsWith("provided-", StringComparison.Ordinal)
            ? id.AsSpan("provided-".Length)
            : id.AsSpan();
        if (hash.Length != 64)
        {
            return false;
        }
        foreach (var character in hash)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }
}
