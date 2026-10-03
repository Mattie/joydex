namespace Joydex.Tests;

/// <summary>
/// Requires an explicit opt-in for tests that display native windows or tray icons.
/// </summary>
public sealed class AttendedFactAttribute : FactAttribute
{
    public AttendedFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("JOYDEX_RUN_ATTENDED_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Requires an attended desktop; explicitly set JOYDEX_RUN_ATTENDED_TESTS=1 to run.";
        }
    }
}
