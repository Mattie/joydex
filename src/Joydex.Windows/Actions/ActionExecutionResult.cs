namespace Joydex.Windows.Actions;

public sealed record ActionExecutionResult(bool Executed, bool DryRun, string Message)
{
    public static ActionExecutionResult Blocked(string message) => new(false, false, message);

    public static ActionExecutionResult Simulated(string message) => new(false, true, message);

    public static ActionExecutionResult Success(string message) => new(true, false, message);
}
