using Joydex.App;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertsFormTests
{
    [Fact]
    public void LedSettingsEditorShowsFiveCompleteThrottleBanks()
    {
        using var form = new TaskAlertLedSettingsForm(TaskAlertLedOptions.CreateDefault());

        var grid = FindControl<DataGridView>(form, "Throttle bank LED colors");
        var mode = FindControl<ComboBox>(form, "Task alert LED output selection");

        Assert.Equal(5, grid.Rows.Count);
        Assert.Equal(7, grid.Columns.Count);
        Assert.Equal("VIRPIL LinkTool", mode.Text);
        Assert.Equal("#0000FF", grid.Rows[1].Cells[3].Value);
        Assert.Equal("#802060", grid.Rows[4].Cells[6].Value);
    }

    [Fact]
    public async Task ShowsConfiguredDirectUsbOutputMode()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"joydex-task-alert-form-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            using var form = new TaskAlertsForm(
                coordinator,
                new CodexHookManager(Path.Combine(directory, "hooks.json")),
                Path.Combine(directory, "Joydex.HookRelay.exe"),
                Path.Combine(directory, "joydex-link-tool-profile.json"),
                _ => Task.CompletedTask);
            form.SetSnapshotForDocumentation(coordinator.GetSnapshot() with
            {
                LedOutput = TaskAlertLedOptions.CreateDefault() with { Mode = TaskAlertLedOutputMode.DirectHid },
            });

            var output = FindControl<Label>(form, "Task alert LED output mode");
            Assert.Equal("LED output: Direct USB", output.Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TaskActionsAreSeparatedFromTabsAndRequireAnExplicitSelection()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"joydex-task-alert-form-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            using var form = new TaskAlertsForm(
                coordinator,
                new CodexHookManager(Path.Combine(directory, "hooks.json")),
                Path.Combine(directory, "Joydex.HookRelay.exe"),
                Path.Combine(directory, "joydex-link-tool-profile.json"),
                _ => Task.CompletedTask);
            var now = DateTimeOffset.UtcNow;
            var snapshot = Snapshot(
                now,
                new TaskAlertAssignment(1, "session-1", "turn-1", TaskAlertState.Running, now, Workspace: @"D:\Dev\one"));

            form.SetSnapshotForDocumentation(snapshot);

            var grid = FindControl<DataGridView>(form, "Current task-alert assignments");
            var currentStateTab = FindControl<PageTabButton>(form, "Current state page");
            var ignoreSelected = FindControl<RoundedButton>(form, "Choose ignore scope for selected Codex task source");
            var selectedSource = FindControl<Label>(form, "Selected task alert source");
            var ignoredSources = FindControl<RoundedButton>(form, "Manage 0 ignored Codex task status sources");
            Assert.Empty(grid.SelectedRows.Cast<DataGridViewRow>());
            Assert.False(ignoreSelected.Enabled);
            Assert.Equal("Ignore selected ▾", ignoreSelected.Text);
            Assert.Equal("Opens a menu for choosing task or workspace scope.", ignoreSelected.AccessibleDescription);
            Assert.True(ignoredSources.Enabled);
            Assert.Equal("Select a row to manage its signaling.", selectedSource.Text);
            Assert.NotSame(currentStateTab.Parent, ignoreSelected.Parent);
            Assert.Equal(ButtonVariant.Secondary, ignoreSelected.Variant);

            form.SetSnapshotForDocumentation(snapshot with
            {
                Suppressions =
                [
                    new TaskAlertSuppressionRule(TaskAlertSuppressionScope.Workspace, @"D:\Dev\one"),
                ],
            });

            Assert.Equal("Ignored sources (1)", ignoredSources.Text);
            Assert.True(ignoredSources.Enabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task IgnoredSourcesManagerStartsSafeAndShowsRecognizableRules()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"joydex-ignored-sources-form-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            Assert.True(coordinator.AddSuppression(
                TaskAlertSuppressionScope.Workspace,
                @"C:\Users\Example\Documents\Codex\realtime-voice-chat"));
            using var form = new IgnoredTaskSourcesForm(coordinator);

            var grid = FindControl<DataGridView>(form, "Ignored task status sources");
            var reenable = FindControl<RoundedButton>(form, "Re-enable selected task status source");
            var rule = Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal("WORKSPACE", rule.Cells["Scope"].Value);
            Assert.Equal("realtime-voice-chat", rule.Cells["Source"].Value);
            Assert.Equal(@"C:\Users\Example\Documents\Codex\realtime-voice-chat", rule.Cells["Match"].Value);
            Assert.Empty(grid.SelectedRows.Cast<DataGridViewRow>());
            Assert.False(reenable.Enabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotRefreshPreservesTheSelectedTask()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"joydex-task-alert-form-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var preferencesPath = Path.Combine(directory, "task-alerts.json");
            var hooksPath = Path.Combine(directory, "hooks.json");
            var relayPath = Path.Combine(directory, "Joydex.HookRelay.exe");
            var linkToolProfilePath = Path.Combine(directory, "joydex-link-tool-profile.json");
            await using var coordinator = new TaskAlertCoordinator(preferencesPath);
            using var form = new TaskAlertsForm(
                coordinator,
                new CodexHookManager(hooksPath),
                relayPath,
                linkToolProfilePath,
                _ => Task.CompletedTask);
            var now = DateTimeOffset.UtcNow;
            form.SetSnapshotForDocumentation(Snapshot(
                now,
                new TaskAlertAssignment(1, "session-1", "turn-1", TaskAlertState.Running, now, Workspace: @"D:\Dev\one"),
                new TaskAlertAssignment(2, "session-2", "turn-2", TaskAlertState.Running, now)));
            var grid = FindControl<DataGridView>(form, "Current task-alert assignments");
            grid.ClearSelection();
            grid.Rows[1].Selected = true;
            grid.CurrentCell = grid.Rows[1].Cells[0];

            form.SetSnapshotForDocumentation(Snapshot(
                now.AddSeconds(1),
                new TaskAlertAssignment(1, "session-1", "turn-1", TaskAlertState.Completed, now.AddSeconds(1), Workspace: @"D:\Dev\one"),
                new TaskAlertAssignment(2, "session-2", "turn-2", TaskAlertState.Running, now.AddSeconds(1), Workspace: @"D:\Dev\two")));

            var selected = Assert.Single(grid.SelectedRows.Cast<DataGridViewRow>());
            Assert.Equal("session-2", selected.Cells["Session"].Value);
            Assert.Equal("two", selected.Cells["Workspace"].Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedHooksFileProducesANonfatalStatus()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"joydex-task-alert-form-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var hooksPath = Path.Combine(directory, "hooks.json");
        var relayPath = Path.Combine(directory, "Joydex.HookRelay.exe");
        await File.WriteAllTextAsync(hooksPath, "[]");
        await File.WriteAllTextAsync(relayPath, "test relay");
        try
        {
            var status = TaskAlertsForm.InspectHookStatus(
                new CodexHookManager(hooksPath),
                relayPath);

            Assert.Equal("Hooks: status unavailable", status.Text);
            Assert.False(string.IsNullOrEmpty(status.Error));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TaskAlertSnapshot Snapshot(
        DateTimeOffset now,
        params TaskAlertAssignment[] assignments) => new(
            Enabled: true,
            Assignments: assignments,
            DroppedEventCount: 0,
            RecentEvents:
            [
                new TaskAlertEventTrace(
                    now,
                    CodexLifecycleEvent.UserPromptSubmit,
                    assignments[0].SessionId,
                    assignments[0].TurnId,
                    assignments[0].Slot,
                    assignments[0].State,
                    TaskAlertEventResult.Assigned,
                    assignments[0].Workspace),
            ]);

    private static T FindControl<T>(Control parent, string accessibleName)
        where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T match && string.Equals(match.AccessibleName, accessibleName, StringComparison.Ordinal))
            {
                return match;
            }

            try
            {
                return FindControl<T>(child, accessibleName);
            }
            catch (InvalidOperationException)
            {
                // Search the next branch.
            }
        }

        throw new InvalidOperationException($"Control '{accessibleName}' was not found.");
    }
}
