using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Voice;

namespace Joydex.RuntimeHost.Production;

internal sealed class PebbleIndexProductionOwner : IProductionRuntimeOwner
{
    private readonly PebbleIndexReceiverRuntime _runtime;
    private readonly ProductionDesktopBrokerLease _desktopBrokerLease;
    private readonly Task _completion;
    private int _disposed;

    private PebbleIndexProductionOwner(
        PebbleIndexReceiverRuntime runtime,
        ProductionDesktopBrokerLease desktopBrokerLease)
    {
        _runtime = runtime;
        _desktopBrokerLease = desktopBrokerLease;
        _completion = Task.WhenAny(runtime.Completion, desktopBrokerLease.Completion).Unwrap();
    }

    public SettingsAggregateId Aggregate => SettingsAggregateId.PebbleIndex;

    public Task Completion => _completion;

    public static async Task<PebbleIndexProductionOwner> StartAsync(
        WindowsProductionRuntimeOwnerFactory factory,
        ProductionRuntimePaths paths,
        ProductionDesktopBrokerManager desktopBroker,
        PebbleIndexPreferences preferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(desktopBroker);
        ArgumentNullException.ThrowIfNull(preferences);
        var lease = await desktopBroker.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtime = await PebbleIndexReceiverRuntime.StartAsync(
                    preferences,
                    paths.PebbleIndexSecret,
                    paths.PebbleIndexInbox,
                    lease.PipeName,
                    status => factory.PublishPebbleIndexStatus(status),
                    factory.WriteLog,
                    cancellationToken)
                .ConfigureAwait(false);
            return new PebbleIndexProductionOwner(runtime, lease);
        }
        catch (Exception startupFailure)
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new ProductionOwnershipCleanupException(
                    "Pebble Index startup failed and its Desktop bridge lease cleanup was incomplete.",
                    [startupFailure, cleanupFailure]);
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var failures = new List<Exception>();
        try
        {
            await _runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        try
        {
            await _desktopBrokerLease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("Pebble Index cleanup did not complete.", failures);
        }
    }
}
