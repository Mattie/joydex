using Joydex.App;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class RoomVoiceSettingsControlTests
{
    [Fact]
    public void ReadPreferencesPreservesProvisionedWorkspaceIdentity()
    {
        var taskId = Guid.NewGuid().ToString("D");
        var workspace = Path.Combine(Path.GetTempPath(), "joydex_voice");
        var preferences = VoicePePreferences.Default with
        {
            SessionMode = VoicePeSessionMode.JoydexOwner,
            DedicatedTaskId = taskId,
            DedicatedTaskLabel = "Joydex Voice Chat — Owned (joydex_voice)",
            AgentWorkspacePath = workspace,
            AgentProjectId = "project-1",
            AgentProjectLabel = "Joydex Voice",
            DesktopTaskMessagingEnabled = true,
            VoiceTargetTaskId = Guid.NewGuid().ToString("D"),
            VoiceTargetHostId = "local",
            VoiceTargetTaskLabel = "Implementation task",
        };
        using var control = new RoomVoiceSettingsControl(
            preferences,
            _ => Task.FromResult(true),
            (_, _) => Task.FromResult(VoicePeWakeTuning.Default),
            (_, tuning, _) => Task.FromResult(tuning));

        var read = control.ReadPreferences();

        Assert.Equal(taskId, read.DedicatedTaskId);
        Assert.Equal(Path.GetFullPath(workspace), read.AgentWorkspacePath);
        Assert.Equal("project-1", read.AgentProjectId);
        Assert.Equal("Joydex Voice", read.AgentProjectLabel);
        Assert.True(read.DesktopTaskMessagingEnabled);
        Assert.Equal(preferences.VoiceTargetTaskId, read.VoiceTargetTaskId);
        Assert.Equal("local", read.VoiceTargetHostId);
        Assert.Equal("Implementation task", read.VoiceTargetTaskLabel);
    }

    [Fact]
    public async Task DisposingWhileWakeTuningLoadsCompletesAsCancellation()
    {
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preferences = VoicePePreferences.Default with
        {
            DeviceEndpoint = "http://voice-pe.local/",
        };
        using var control = new RoomVoiceSettingsControl(
            preferences,
            _ => Task.FromResult(true),
            async (_, cancellationToken) =>
            {
                readStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return VoicePeWakeTuning.Default;
            },
            (_, tuning, _) => Task.FromResult(tuning));

        var load = control.LoadWakeTuningAsync(showErrors: false);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        control.Dispose();

        await load.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AutomaticRuntimeSelectionStillLoadsCodexProjects()
    {
        string? receivedRuntimeOverride = null;
        using var control = new RoomVoiceSettingsControl(
            VoicePePreferences.Default with
            {
                SessionMode = VoicePeSessionMode.JoydexOwner,
                CodexAppServerPath = string.Empty,
            },
            _ => Task.FromResult(true),
            (_, _) => Task.FromResult(VoicePeWakeTuning.Default),
            (_, tuning, _) => Task.FromResult(tuning),
            (runtimeOverride, _) =>
            {
                receivedRuntimeOverride = runtimeOverride;
                return Task.FromResult(new CodexProjectCatalog([]));
            });

        await control.LoadProjectChoicesAsync();

        Assert.Equal(string.Empty, receivedRuntimeOverride);
    }

    [Fact]
    public void ExistingManagedRuntimePathIsPresentedAsAutomaticSelection()
    {
        var managedRuntimePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin",
            "old-build",
            "codex.exe");
        using var control = new RoomVoiceSettingsControl(
            VoicePePreferences.Default with
            {
                CodexAppServerPath = managedRuntimePath,
            },
            _ => Task.FromResult(true),
            (_, _) => Task.FromResult(VoicePeWakeTuning.Default),
            (_, tuning, _) => Task.FromResult(tuning));

        Assert.Empty(control.ReadPreferences().CodexAppServerPath);
    }
}
