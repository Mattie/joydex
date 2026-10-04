namespace Joydex.RuntimeHost;

internal interface IRuntimeOwnershipLease : IDisposable;

internal interface IRuntimeOwnershipLeaseFactory
{
    IRuntimeOwnershipLease Acquire();
}

/// <summary>
/// Holds an already-acquired ownership lease until RuntimeEngine takes it. This lets Program take
/// the legacy production guard before it starts either listener while keeping the engine as the
/// final owner of that guard.
/// </summary>
internal sealed class TransferredRuntimeOwnershipLeaseFactory :
    IRuntimeOwnershipLeaseFactory,
    IDisposable
{
    private IRuntimeOwnershipLease? _lease;
    private int _acquired;
    private int _disposed;

    public TransferredRuntimeOwnershipLeaseFactory(IRuntimeOwnershipLease lease) =>
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));

    public IRuntimeOwnershipLease Acquire()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _acquired, 1) != 0)
        {
            throw new InvalidOperationException("The transferred runtime ownership lease was already claimed.");
        }

        return Interlocked.Exchange(ref _lease, null)
            ?? throw new InvalidOperationException("The transferred runtime ownership lease is unavailable.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}

/// <summary>
/// Preserves the legacy production exclusion used by the tray application. Holding this mutex for
/// the full engine lifetime prevents an older Joydex process from opening a second hardware owner.
/// </summary>
internal sealed class LegacyJoydexOwnershipLeaseFactory : IRuntimeOwnershipLeaseFactory
{
    internal const string MutexName = @"Local\Joydex";
    private readonly string _mutexName;

    public LegacyJoydexOwnershipLeaseFactory(string? mutexName = null)
    {
        _mutexName = string.IsNullOrWhiteSpace(mutexName)
            ? MutexName
            : mutexName.Trim();
    }

    public IRuntimeOwnershipLease Acquire()
    {
        var ready = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Mutex? mutex = null;
            var acquired = false;
            var readySignaled = false;
            try
            {
                mutex = new Mutex(initiallyOwned: false, _mutexName);
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    failure = new InvalidOperationException(
                        "Another Joydex production runtime already owns the current Windows session.");
                    return;
                }
                ready.Set();
                readySignaled = true;
                release.Wait();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                if (!readySignaled)
                {
                    ready.Set();
                }
                try
                {
                    if (acquired)
                    {
                        mutex!.ReleaseMutex();
                    }
                }
                finally
                {
                    mutex?.Dispose();
                }
            }
        })
        {
            IsBackground = true,
            Name = "Joydex production ownership",
        };
        thread.Start();
        ready.Wait();

        if (failure is not null)
        {
            release.Set();
            thread.Join();
            ready.Dispose();
            release.Dispose();
            throw failure;
        }
        return new MutexOwnershipLease(thread, ready, release);
    }

    private sealed class MutexOwnershipLease(
        Thread ownerThread,
        ManualResetEventSlim ready,
        ManualResetEventSlim release) : IRuntimeOwnershipLease
    {
        private Thread? _ownerThread = ownerThread;
        private ManualResetEventSlim? _ready = ready;
        private ManualResetEventSlim? _release = release;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var thread = Interlocked.Exchange(ref _ownerThread, null)!;
            var readiness = Interlocked.Exchange(ref _ready, null)!;
            var signal = Interlocked.Exchange(ref _release, null)!;

            signal.Set();
            thread.Join();
            signal.Dispose();
            readiness.Dispose();
        }
    }
}

internal sealed class SyntheticRuntimeOwnershipLeaseFactory : IRuntimeOwnershipLeaseFactory
{
    public IRuntimeOwnershipLease Acquire() => SyntheticRuntimeOwnershipLease.Instance;

    private sealed class SyntheticRuntimeOwnershipLease : IRuntimeOwnershipLease
    {
        public static SyntheticRuntimeOwnershipLease Instance { get; } = new();
        public void Dispose()
        {
        }
    }
}
