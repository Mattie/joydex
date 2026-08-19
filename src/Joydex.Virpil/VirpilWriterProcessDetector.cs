using System.ComponentModel;
using System.Diagnostics;

namespace Joydex.Virpil;

public static class VirpilWriterProcessDetector
{
    private static readonly string[] ProcessFragments =
    [
        "LinkTool",
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

    public static string? FindConflict()
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
                        return process.ProcessName;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                }
            }
        }

        return null;
    }
}
