using Joydex.Core.TaskAlerts;

namespace Joydex.Windows.TaskAlerts;

/// <summary>
/// A successful visit acknowledges the questions present when navigation began.
/// Later questions remain pending, and unsuccessful navigation changes no attention state.
/// </summary>
public sealed class QuestionAcknowledgingTaskNavigator(
    ITaskAlertNavigator navigator,
    Func<TaskAlertSnapshot> getSnapshot,
    Func<int, string, IReadOnlyCollection<string>, bool> dismissQuestions,
    Action<string> log) : ITaskAlertNavigator
{
    public async Task<bool> NavigateAsync(TaskAlertNavigationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = getSnapshot();
        var displayed = snapshot.Enabled
            ? snapshot.Assignments.FirstOrDefault(item => string.Equals(item.SessionId, request.SessionId, StringComparison.OrdinalIgnoreCase))
            : null;
        var questionKeys = displayed?.PendingAsyncQuestions?.ToArray() ?? [];
        if (!await navigator.NavigateAsync(request, cancellationToken).ConfigureAwait(false)) return false;
        if (displayed is not null && questionKeys.Length > 0)
        {
            try
            {
                // Overflow promotion can move the same chat while its URI is opening.
                var current = getSnapshot().Assignments.FirstOrDefault(item => item.SessionId == displayed.SessionId);
                if (current is not null) dismissQuestions(current.Slot, current.SessionId, questionKeys);
            }
            catch (Exception exception)
            {
                // The thread already opened. Do not report a failed visit or retry navigation.
                log($"Task-alert question acknowledgement after navigation failed ({exception.GetType().Name}).");
            }
        }
        return true;
    }
}
