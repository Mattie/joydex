using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertSuppressionTests
{
    [Fact]
    public void WorkspaceRuleMatchesOnlyItsNormalizedWorkspaceKey()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "joydex-voice-chat");
        var rule = TaskAlertSuppression.Normalize(new TaskAlertSuppressionRule(
            TaskAlertSuppressionScope.Workspace,
            workspace + Path.DirectorySeparatorChar));

        Assert.NotNull(rule);
        Assert.True(TaskAlertSuppression.Matches(
            rule,
            "any-task",
            TaskAlertSuppression.CreateWorkspaceKey(workspace)));
        Assert.False(TaskAlertSuppression.Matches(
            rule,
            "any-task",
            TaskAlertSuppression.CreateWorkspaceKey(workspace + "-other")));
    }

    [Fact]
    public void TaskRuleUsesExactSessionIdentity()
    {
        var rule = new TaskAlertSuppressionRule(TaskAlertSuppressionScope.Task, "task-a");

        Assert.True(TaskAlertSuppression.Matches(rule, "task-a", null));
        Assert.False(TaskAlertSuppression.Matches(rule, "TASK-A", null));
    }
}
