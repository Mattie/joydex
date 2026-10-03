using System.Security.Principal;

namespace Joydex.RuntimeHost.Tests;

/// <summary>
/// Runs cross-process runtime IPC tests only when Windows can create supported unelevated peers.
/// </summary>
public sealed class NonElevatedRuntimeIpcFactAttribute : FactAttribute
{
    public NonElevatedRuntimeIpcFactAttribute()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Skip = "Runtime IPC deliberately rejects elevated peers; this cross-process test requires an unelevated test host.";
        }
    }
}
