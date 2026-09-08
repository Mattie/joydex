using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Joydex.VoicePeUdpPrototype;

/// <summary>
/// Keeps Windows timer waits close to the 10 ms speaker packet cadence for one canary scope.
/// </summary>
internal sealed class TimerResolutionLease : IDisposable
{
    private const uint PeriodMilliseconds = 1;
    private int _disposed;

    private TimerResolutionLease()
    {
        var result = TimeBeginPeriod(PeriodMilliseconds);
        if (result != 0)
        {
            throw new Win32Exception(checked((int) result), "Could not request 1 ms Windows timer resolution.");
        }
    }

    public static TimerResolutionLease Acquire() => new();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = TimeEndPeriod(PeriodMilliseconds);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint periodMilliseconds);
}
