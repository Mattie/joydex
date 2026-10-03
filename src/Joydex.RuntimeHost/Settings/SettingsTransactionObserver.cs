using Joydex.Contracts;

namespace Joydex.RuntimeHost.Settings;

internal enum SettingsTransactionCheckpoint
{
    IntentPersisted,
    DocumentWritten,
    DesiredCommitted,
}

internal interface ISettingsTransactionObserver
{
    void OnCheckpoint(SettingsTransactionCheckpoint checkpoint, SettingsAggregateId? aggregate = null);
}

internal sealed class SettingsTransactionObserver : ISettingsTransactionObserver
{
    public static SettingsTransactionObserver Instance { get; } = new();

    public void OnCheckpoint(SettingsTransactionCheckpoint checkpoint, SettingsAggregateId? aggregate = null)
    {
    }
}

/// <summary>Fault-injection signal that models process loss and deliberately skips handled rollback.</summary>
internal sealed class SettingsTransactionInterruptedException(string message) : Exception(message);
