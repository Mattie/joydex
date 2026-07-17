using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Runtime;
using Joydex.Windows.Actions;
using Joydex.Windows.Input;

namespace Joydex.Windows.Runtime;

public sealed class CompanionWorker(
    CompanionConfig config,
    IJoystickSource source,
    CodexActionExecutor executor,
    Action<string> log,
    IInjectedKeyStateLifecycle? keyStateLifecycle = null) : IAsyncDisposable
{
    private readonly CompanionEngine _engine = new(config);
    private readonly IInjectedKeyStateLifecycle _keyStateLifecycle = keyStateLifecycle ?? executor;
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;

    public event EventHandler<string>? StatusChanged;

    public void Start()
    {
        if (_runTask is not null)
        {
            return;
        }

        try
        {
            _keyStateLifecycle.ClearInjectedKeyState();
        }
        catch (Exception exception)
        {
            log($"Could not clear stale push-to-talk keys during startup: {exception.Message}");
        }

        _cancellation = new CancellationTokenSource();
        _runTask = RunGuardedAsync(_cancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_runTask is null || _cancellation is null)
        {
            return;
        }

        _cancellation.Cancel();
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _runTask = null;
        _cancellation.Dispose();
        _cancellation = null;
        source.Disconnect();
        _engine.Reset();
        SetStatus("Stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        source.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            try
            {
                log($"Companion stopped unexpectedly: {exception}");
            }
            catch
            {
                // The worker must remain observable through its tray status even if the log is unavailable.
            }

            SetStatus("Companion stopped; see log");
        }
        finally
        {
            try
            {
                _keyStateLifecycle.ReleaseHeldKeys();
            }
            catch (Exception exception)
            {
                try
                {
                    log($"Could not release a held push-to-talk key: {exception.Message}");
                }
                catch
                {
                    // Cleanup must not fault the worker if the log has also become unavailable.
                }
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (source.ConnectedDevice is null)
            {
                if (source.TryConnect(config.Device, out var connectionMessage))
                {
                    _engine.Reset();
                    log(connectionMessage);
                    SetStatus(config.Safety.DryRun
                        ? $"Connected (dry run): {source.ConnectedDevice?.ProductName}"
                        : $"Connected: {source.ConnectedDevice?.ProductName}");
                    await Task.Delay(config.Polling.ConnectWarmupMs, cancellationToken).ConfigureAwait(false);

                    if (source.TryRead(out var baseline, out _) && baseline is not null)
                    {
                        _engine.Process(baseline, []);
                    }

                    continue;
                }
                else
                {
                    SetStatus("Waiting for throttle");
                    await Task.Delay(config.Polling.ReconnectIntervalMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            if (!source.TryRead(out var snapshot, out var readError) || snapshot is null)
            {
                if (!string.IsNullOrWhiteSpace(readError))
                {
                    log($"DirectInput disconnected: {readError}");
                }

                _engine.Reset();
                SetStatus("Throttle disconnected");
                await Task.Delay(config.Polling.ReconnectIntervalMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var result = _engine.Process(snapshot, source.LatestBufferedButtonEvents);
            if (config.Safety.DryRun)
            {
                foreach (var inputEvent in result.InputEvents)
                {
                    var trigger = inputEvent.Kind switch
                    {
                        JoystickEventKind.ButtonPressed => "press",
                        JoystickEventKind.ButtonReleased => "release",
                        _ => null,
                    };
                    if (trigger is not null)
                    {
                        log($"INPUT {trigger} from throttle/button {inputEvent.DisplayIndex}");
                    }
                }
            }

            foreach (var request in result.ActionRequests)
            {
                try
                {
                    await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    log($"Action {request.BindingName} failed: {exception.Message}");
                    SetStatus("Action failed; see log");
                }
            }

            await Task.Delay(config.Polling.PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetStatus(string status) => StatusChanged?.Invoke(this, status);
}
