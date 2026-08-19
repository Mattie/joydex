using System.ComponentModel;
using System.Diagnostics;
using Joydex.Core.TaskAlerts;
using Joydex.Virpil;

namespace Joydex.Windows.TaskAlerts;

public sealed record VirpilLedColor(byte Red, byte Green, byte Blue)
{
    public static VirpilLedColor For(TaskAlertState state, TaskAlertLedOptions? options = null)
    {
        var color = TaskAlertColors.Get(state, options ?? TaskAlertLedOptions.CreateDefault());
        return new VirpilLedColor(color.Red, color.Green, color.Blue);
    }
}

public interface IVpcConflictDetector
{
    bool HasConflict();
}

public sealed class VpcConflictDetector : IVpcConflictDetector
{
    private static readonly string[] ProcessFragments =
    [
        "VPC Configurator",
        "VPC_Configurator",
        "VPC Shift",
        "VPC_Shift",
        "VPC LED",
        "VPC_LED",
        "VPC Test",
        "VPC_Test",
        "VPC Analysis",
        "VPC_Analysis",
        "VPC Device Setup",
        "VPC_Device_Setup",
        "VPC_JOY_SETUP",
        "VPC_JOY_TEST",
        "VPC_JOY_ANALYZER",
        "VPC_JOY_ANALYSIS",
    ];

    public bool HasConflict()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (ProcessFragments.Any(fragment =>
                            process.ProcessName.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }
            }
        }

        return false;
    }
}

public sealed class DirectVirpilConflictDetector : IVpcConflictDetector
{
    public bool HasConflict() => VirpilWriterProcessDetector.FindConflict() is not null;
}
