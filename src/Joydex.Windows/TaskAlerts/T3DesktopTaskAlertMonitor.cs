using Joydex.Core.TaskAlerts;
using Joydex.Core.T3;
using Joydex.Windows.T3;

namespace Joydex.Windows.TaskAlerts;

/// <summary>Follows the desktop catalog while each enabled native environment keeps its own finite read loop.</summary>
public sealed class T3DesktopTaskAlertMonitor : IAsyncDisposable
{
    private readonly TaskAlertCoordinator _coordinator;
    private readonly T3ConnectionOptions _options;
    private readonly Action<string> _log;
    private readonly Func<CancellationToken, Task<T3DesktopDiscoveryResult>> _discover;
    private readonly Func<T3DesktopEnvironment, IReadOnlyCollection<string>, CancellationToken, Task<T3ReadResult>>? _read;
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly SemaphoreSlim _reads = new(4, 4);
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    public Task Completion => _completion.Task;

    public T3DesktopTaskAlertMonitor(TaskAlertCoordinator coordinator, T3ConnectionOptions options, Action<string> log,
        Func<CancellationToken, Task<T3DesktopDiscoveryResult>>? discover = null,
        Func<T3DesktopEnvironment, IReadOnlyCollection<string>, CancellationToken, Task<T3ReadResult>>? read = null)
    {
        _coordinator = coordinator;
        _options = options.Normalize();
        _log = log;
        var reader = new T3Reader();
        _discover = discover ?? reader.DiscoverDesktopAsync;
        _read = read;
    }

    public void Start() => _loop ??= Task.Run(RunAsync);

    public async Task<bool> RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var token = linked.Token;
        await _refresh.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_coordinator.GetSnapshot().Enabled) return true;
            T3DesktopDiscoveryResult catalog;
            try { catalog = await _discover(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { catalog = new(false, false, [], "The T3 desktop catalog could not be read; previous environments are retained."); }
            token.ThrowIfCancellationRequested();
            if (catalog.Environments.Count > 64)
                catalog = new(false, false, [], "The desktop catalog exceeds the supported environment limit; existing sources are retained.");
            if (!catalog.Success)
            {
                _coordinator.SetT3DesktopEnvironments(catalog);
                return false;
            }
            var observed = catalog.Environments.Take(64).Select(WithManualFallback).ToArray();
            var rows = observed.Where(environment => !string.IsNullOrWhiteSpace(environment.EnvironmentId))
                .GroupBy(environment => environment.EnvironmentId, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(environment => environment.Enabled)
                    .ThenBy(environment => environment.UnsupportedReason is null ? 0 : 1)
                    .ThenByDescending(environment => environment.IsLocal)
                    .ThenBy(environment => environment.ConnectionId, StringComparer.Ordinal).First())
                .Concat(observed.Where(environment => string.IsNullOrWhiteSpace(environment.EnvironmentId))).ToArray();
            catalog = catalog with { Environments = rows, IsComplete = catalog.IsComplete && catalog.Environments.Count <= 64 };
            var desired = catalog.IsComplete ? new Dictionary<string, T3DesktopEnvironment>(StringComparer.Ordinal)
                : _workers.ToDictionary(pair => pair.Key, pair => pair.Value.Environment, StringComparer.Ordinal);
            foreach (var environment in rows)
                if (!environment.Enabled) desired.Remove(environment.EnvironmentId);
                else if (environment.UnsupportedReason is null && !string.IsNullOrWhiteSpace(environment.EnvironmentId))
                    desired[environment.EnvironmentId] = environment;
            if (desired.Count > 64)
            {
                _coordinator.SetT3DesktopEnvironments(new(false, false, [],
                    "The partial desktop catalog exceeds the supported environment limit; existing sources are retained."));
                return false;
            }
            foreach (var pair in _workers.ToArray())
                if (!desired.TryGetValue(pair.Key, out var environment) || pair.Value.Environment != environment
                    || pair.Value.Monitor.Completion.IsCompleted)
                {
                    await pair.Value.Monitor.DisposeAsync().ConfigureAwait(false);
                    await pair.Value.HealthObserver.ConfigureAwait(false);
                    _coordinator.SetSourceUnavailable(new(TaskAlertBackend.RemoteT3, pair.Key), new()
                        { Reason = "The desktop environment route changed or is no longer enabled.", ErrorClass = "RouteChanged" });
                    _workers.Remove(pair.Key);
                }
            _coordinator.SetT3DesktopEnvironments(catalog);
            foreach (var environment in desired.Values)
                if (!_workers.ContainsKey(environment.EnvironmentId))
                {
                    var reader = new T3Reader();
                    async Task<T3ReadResult> Read(IReadOnlyCollection<string> known, CancellationToken readToken)
                    {
                        await _reads.WaitAsync(readToken).ConfigureAwait(false);
                        try
                        {
                            var result = _read is null ? await reader.ReadDesktopEnvironmentAsync(environment, known, readToken).ConfigureAwait(false)
                                : await _read(environment, known, readToken).ConfigureAwait(false);
                            return result with { TransportKind = environment.TransportKind, DisplayName = environment.DisplayName, Enabled = true };
                        }
                        finally { _reads.Release(); }
                    }
                    var monitor = new T3TaskAlertMonitor(_coordinator, new() { EnvironmentId = environment.EnvironmentId,
                        DisplayName = environment.DisplayName, SshHost = environment.SshHost ?? "" }, _log, readEnvironment: Read);
                    _workers.Add(environment.EnvironmentId, new(environment, monitor, ObserveHealthAsync(environment, monitor)));
                    monitor.Start();
                }
            return catalog.IsComplete;
        }
        finally { _refresh.Release(); }
    }

    private T3DesktopEnvironment WithManualFallback(T3DesktopEnvironment environment) =>
        environment.Enabled && environment.UnsupportedReason is not null && environment.EnvironmentId == _options.EnvironmentId
            && T3SshTransport.IsValidHost(_options.SshHost)
            ? environment with { IsLocal = false, SshHost = _options.SshHost, UnsupportedReason = null, TransportKind = "ssh" }
            : environment;

    private async Task ObserveHealthAsync(T3DesktopEnvironment environment, T3TaskAlertMonitor monitor)
    {
        try { await monitor.Completion.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (_stopping.IsCancellationRequested) return;
            _coordinator.SetSourceUnavailable(new(TaskAlertBackend.RemoteT3, environment.EnvironmentId),
                new() { ErrorClass = error.GetType().Name, Reason = "The environment reader stopped unexpectedly; recovery is pending." });
            try { _log("T3 environment " + environment.EnvironmentId + " reader stopped (" + error.GetType().Name + "); recovery is pending."); }
            catch { /* A diagnostic sink cannot prevent source cleanup or recovery. */ }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                await RefreshOnceAsync(_stopping.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(10), _stopping.Token).ConfigureAwait(false);
            }
            _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { _completion.TrySetResult(); }
        catch (Exception error) { _completion.TrySetException(error); }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        await _refresh.WaitAsync().ConfigureAwait(false);
        try
        {
            // Cancel every source before awaiting cleanup. One grant's bounded
            // revocation must not leave other sources polling during shutdown.
            var cleanup = _workers.Values.Select(worker => worker.Monitor.DisposeAsync().AsTask()).ToArray();
            await Task.WhenAll(cleanup).ConfigureAwait(false);
            await Task.WhenAll(_workers.Values.Select(worker => worker.HealthObserver)).ConfigureAwait(false);
            _workers.Clear();
        }
        finally { _refresh.Release(); }
        _completion.TrySetResult();
        _stopping.Dispose();
        _reads.Dispose();
        _refresh.Dispose();
    }

    private sealed record Worker(T3DesktopEnvironment Environment, T3TaskAlertMonitor Monitor, Task HealthObserver);
}
