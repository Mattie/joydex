using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class PendingTaskQuestionsTests
{
    private static readonly string KeyA = new('A', 64);
    private static readonly string KeyB = new('B', 64);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void LaterUserTurnSupersedesQuestionsButSameTurnSteeringDoesNot()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "session", "old", Now, KeyA));
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "session", "old", Now.AddSeconds(1)));
        Assert.Equal([KeyA], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "session", "next", Now.AddSeconds(2)));
        Assert.Null(Assert.Single(pool.Assignments).PendingAsyncQuestions);
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
        var restored = new TaskAlertPool();
        restored.Restore(pool.CaptureState());
        Assert.Null(Assert.Single(restored.Assignments).PendingAsyncQuestions);
    }

    [Fact]
    public void NewTurnCardReplyPreservesOtherQuestionsAndOutOfOrderPromptsDoNotClearThem()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "session", "old", Now, KeyA));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "session", "old", Now.AddSeconds(1), KeyB));
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "session", "answer", Now.AddSeconds(2), IsQuestionReply: true));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputAnswered, "session", "answer", Now.AddSeconds(2), KeyA));
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "session", "outdated", Now));
        Assert.Equal([KeyB], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
        var restored = new TaskAlertPool();
        restored.Restore(pool.CaptureState());
        restored.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "session", "answer", Now.AddSeconds(3)));
        Assert.Null(Assert.Single(restored.Assignments).PendingAsyncQuestions);
    }

    [Fact]
    public void QuestionTitlesAreAvailableForDisplayButAreNotPersisted()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "session", "turn", Now, KeyA,
            QuestionTitle: "Private question fixture"));
        Assert.Equal("Private question fixture", Assert.Single(Assert.Single(pool.Assignments).Questions!).Title);
        var stored = System.Text.Json.JsonSerializer.Serialize(pool.CaptureState());
        Assert.DoesNotContain("Private question fixture", stored);
        var restored = new TaskAlertPool();
        restored.Restore(pool.CaptureState());
        Assert.Null(Assert.Single(Assert.Single(restored.Assignments).Questions!).Title);
    }

    [Fact]
    public void SupersedingOldQuestionsPreservesQuestionsAlreadyAskedInTheCurrentTurn()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "session", "old", Now, KeyA));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "session", "new", Now.AddSeconds(1), KeyB));
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "session", "new", Now.AddSeconds(2)));
        Assert.Equal([KeyB], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
    }

    [Fact]
    public async Task QuestionArrivingDuringDismissalIsNotIncludedInTheCommand()
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            var pool = new TaskAlertPool();
            pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "session", "turn", Now, KeyA));
            using var form = new PendingTaskQuestionsForm(
                () => new TaskAlertSnapshot(true, pool.Assignments, 0), (selection, _) =>
                {
                    pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "session", "turn", Now.AddSeconds(1), KeyB));
                    pool.DismissQuestions(selection.Slot, selection.SessionId, selection.QuestionKeys, Now.AddSeconds(2));
                    return Task.CompletedTask;
                });
            await form.DismissQuestionAsync("session", KeyA);
            Assert.Equal([KeyB], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
            Assert.Single(FindGrid(form).Rows.Cast<DataGridViewRow>());
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    [Fact]
    public async Task ListShowsAllTasksAndDismissesOnlyTheChosenQuestionWithoutRowSelection()
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            var pool = new TaskAlertPool();
            pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "one", "turn", Now, KeyA,
                QuestionTitle: "First question"));
            pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "one", "turn", Now.AddSeconds(1), KeyB,
                QuestionTitle: "Second question"));
            pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "two", "turn", Now.AddSeconds(2), KeyA,
                QuestionTitle: "Other task question"));
            pool.Apply(new(CodexLifecycleEvent.PermissionRequest, "one", "turn", Now.AddSeconds(3), KeyA));
            RuntimeTaskAlertQuestionDismissal? sent = null;
            using var form = new PendingTaskQuestionsForm(
                () => new TaskAlertSnapshot(true, pool.Assignments, 0), (selection, _) =>
                {
                    sent = selection;
                    pool.DismissQuestions(selection.Slot, selection.SessionId, selection.QuestionKeys, Now.AddSeconds(4));
                    return Task.CompletedTask;
                });
            var grid = FindGrid(form);
            Assert.Equal(3, grid.Rows.Count);
            Assert.Empty(grid.SelectedRows.Cast<DataGridViewRow>());
            Assert.Equal("First question", grid.Rows[0].Cells["Question"].Value);
            await form.DismissQuestionAsync("one", KeyA);
            Assert.Equal([KeyA], sent!.QuestionKeys);
            Assert.Equal(2, grid.Rows.Count);
            Assert.Equal([KeyB], pool.Assignments[0].PendingAsyncQuestions!);
            Assert.Equal(TaskAlertState.Approval, pool.Assignments[0].State);
            Assert.Equal([KeyA], pool.Assignments[1].PendingAsyncQuestions!);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static DataGridView FindGrid(Control parent) => parent.Controls.Cast<Control>()
        .SelectMany(Descendants).OfType<DataGridView>().Single();

    private static IEnumerable<Control> Descendants(Control control) =>
        new[] { control }.Concat(control.Controls.Cast<Control>().SelectMany(Descendants));
}
