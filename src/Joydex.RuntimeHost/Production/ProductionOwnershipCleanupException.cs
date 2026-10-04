namespace Joydex.RuntimeHost.Production;

/// <summary>
/// Reports that a production owner failed during startup and could not confirm cleanup. The
/// aggregate must stay terminal for the rest of this process.
/// </summary>
internal sealed class ProductionOwnershipCleanupException(
    string message,
    IEnumerable<Exception> failures) : AggregateException(message, failures)
{
}
