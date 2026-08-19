using Joydex.Core.TaskAlerts;

namespace Joydex.Windows.TaskAlerts;

public interface ITaskAlertLedOutput : IAsyncDisposable
{
    event EventHandler<string>? StatusChanged;

    event EventHandler<bool>? ProfileDirtyChanged;

    bool RestorePending { get; }

    void Apply(TaskAlertSnapshot snapshot);

    void RestoreAndReplay(bool replay);

    void SetPaused(bool paused);

    Task<bool> WaitForIdleAsync(TimeSpan timeout);
}
