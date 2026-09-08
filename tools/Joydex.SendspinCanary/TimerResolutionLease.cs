using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Joydex.SendspinCanary;

internal sealed class TimerResolutionLease : IDisposable
{
    private const uint ResolutionMilliseconds = 1;
    private int _disposed;

    private TimerResolutionLease()
    {
    }

    public static TimerResolutionLease Acquire()
    {
        var result = timeBeginPeriod(ResolutionMilliseconds);
        if (result != 0)
        {
            throw new Win32Exception((int) result, "Could not request a 1 ms Windows timer resolution.");
        }
        return new TimerResolutionLease();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = timeEndPeriod(ResolutionMilliseconds);
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint periodMilliseconds);
}
