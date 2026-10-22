using Joydex.App;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;

namespace Joydex.Tests;

public sealed class RoomVoiceSettingsControlTests
{
    [Fact]
    public void VoiceEngineChoicePreservesCodexWorkspaceAndSavedDotWhenSwitching()
    {
        RunOnSta(() =>
        {
            var dot = new DotVoiceTarget("account-test", "user-test", "account-test~dot-test",
                "01000000-0000-0000-0000-000000000000", "Example dot");
            var preferences = VoicePePreferences.Default with
            {
                Enabled = true, DeviceEndpoint = "http://192.0.2.10/",
                SessionMode = VoicePeSessionMode.JoydexOwner,
                DedicatedTaskId = Guid.NewGuid().ToString("D"),
                AgentWorkspacePath = Path.Combine(Path.GetTempPath(), "joydex_voice"),
                RealtimeVoice = "sol", DotTarget = dot,
                DesktopTaskMessagingEnabled = true,
            };
            using var control = new RoomVoiceSettingsControl(preferences,
                _ => Task.FromResult(true), (_, _) => Task.FromResult(VoicePeWakeTuning.Default),
                (_, tuning, _) => Task.FromResult(tuning), allowExternalActions: false);
            var engine = (ComboBox)control.Controls.Find("RoomVoiceEngine", searchAllChildren: true).Single();
            var conversation = control.Controls.OfType<TableLayoutPanel>().Single()
                .Controls.OfType<GroupBox>().Single(group => group.Text == "Conversation");
            var codexThread = (TextBox)conversation.Controls.Find("RoomVoiceCodexThread", searchAllChildren: true).Single();
            var dotLabel = (TextBox)conversation.Controls.Find("RoomVoiceDot", searchAllChildren: true).Single();
            Assert.Equal("Codex task voice (legacy)", engine.SelectedItem!.ToString());
            Assert.True(codexThread.Visible);
            Assert.True(codexThread.ReadOnly);
            Assert.Equal(CodexTaskReference.BuildDeepLink(preferences.DedicatedTaskId), codexThread.Text);
            Assert.False(dotLabel.Visible);
            engine.SelectedIndex = 1;
            Assert.False(codexThread.Visible);
            Assert.True(dotLabel.Visible);
            Assert.Equal(dot.Label, dotLabel.Text);
            var dotMode = control.ReadPreferences();
            Assert.Equal(VoicePeSessionMode.NativeDot, dotMode.SessionMode);
            Assert.Empty(dotMode.Validate());
            Assert.Equal(dot, dotMode.DotTarget);
            Assert.Equal(preferences.DedicatedTaskId, dotMode.DedicatedTaskId);
            Assert.Equal(preferences.AgentWorkspacePath, dotMode.AgentWorkspacePath);
            Assert.Equal("sol", dotMode.RealtimeVoice);
            Assert.True(dotMode.DesktopTaskMessagingEnabled);
            control.ApplyPreferences(dotMode);
            engine.SelectedIndex = 0;
            Assert.True(codexThread.Visible);
            Assert.False(dotLabel.Visible);
            var codexMode = control.ReadPreferences();
            Assert.Equal(VoicePeSessionMode.JoydexOwner, codexMode.SessionMode);
            Assert.Empty(codexMode.Validate());
            Assert.Equal(dot, codexMode.DotTarget);
            Assert.Equal(preferences.DedicatedTaskId, codexMode.DedicatedTaskId);
            Assert.True(codexMode.DesktopTaskMessagingEnabled);
        });
    }

    [Theory]
    [InlineData(530, 400)]
    [InlineData(870, 1100)]
    public void AdvancedSettingsCanBeScrolledIntoView(int width, int height)
    {
        RunOnSta(() =>
        {
            using var control = CreateLayoutControl();
            control.Size = new Size(width, height);
            control.CreateControl();
            control.PerformLayout();

            Assert.True(control.VerticalScroll.Visible);
            Assert.False(control.HorizontalScroll.Visible);
            var advanced = FindAdvancedGroup(control);
            var advancedLayout = advanced.Controls.OfType<TableLayoutPanel>().Single();
            // Check the allocated content, rather than a preferred-size query with different constraints.
            Assert.True(advanced.DisplayRectangle.Contains(advancedLayout.Bounds),
                $"Advanced content {advancedLayout.Bounds} exceeds its display area {advanced.DisplayRectangle}.");
            var lastField = advancedLayout
                .Controls.OfType<TableLayoutPanel>().Single()
                .Controls.OfType<TextBox>().Last();
            control.ScrollControlIntoView(lastField);

            Assert.True(control.AutoScrollPosition.Y < 0);
            var location = control.PointToClient(lastField.PointToScreen(Point.Empty));
            Assert.InRange(location.Y, 0, control.ClientSize.Height - lastField.Height);
        });
    }

    [Fact]
    public void ResizingKeepsAdvancedSettingsReachableAndRemovesUnneededScrollbars()
    {
        RunOnSta(() =>
        {
            using var control = CreateLayoutControl();
            control.Size = new Size(530, 400);
            control.CreateControl();
            var advanced = FindAdvancedGroup(control);
            control.ScrollControlIntoView(advanced);
            Assert.True(control.VerticalScroll.Visible);

            control.Size = new Size(1200, 2400);
            control.PerformLayout();
            Assert.False(control.VerticalScroll.Visible);
            Assert.False(control.HorizontalScroll.Visible);
            Assert.Equal(Point.Empty, control.AutoScrollPosition);
            Assert.True(advanced.Bottom <= control.ClientSize.Height);

            control.Size = new Size(700, 600);
            control.PerformLayout();
            Assert.True(control.VerticalScroll.Visible);
            Assert.False(control.HorizontalScroll.Visible);
            control.ScrollControlIntoView(advanced);
            Assert.True(control.AutoScrollPosition.Y < 0);
        });
    }

    [Theory]
    [InlineData(90, 530, 400)]
    [InlineData(90, 870, 1100)]
    [InlineData(1200, 530, 400)]
    [InlineData(1200, 870, 1100)]
    public void LayoutKeepsSectionContentsInsideTheirGroupsAfterWidthChanges(int initialWidth, int width, int height)
    {
        RunOnSta(() =>
        {
            using var control = CreateLayoutControl();
            control.Size = new Size(initialWidth, 400);
            control.CreateControl();
            control.Size = new Size(width, height);
            control.PerformLayout();

            foreach (var group in control.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<GroupBox>())
            {
                var content = Assert.Single(group.Controls.OfType<TableLayoutPanel>());
                Assert.True(group.DisplayRectangle.Contains(content.Bounds),
                    $"{group.Text} content {content.Bounds} exceeds its display area {group.DisplayRectangle}.");
            }

            var advanced = FindAdvancedGroup(control);
            var lastField = advanced.Controls.OfType<TableLayoutPanel>().Single()
                .Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TextBox>().Last();
            control.ScrollControlIntoView(lastField);
            var location = control.PointToClient(lastField.PointToScreen(Point.Empty));
            Assert.InRange(location.Y, 0, control.ClientSize.Height - lastField.Height);
        });
    }

    [Fact]
    public void SectionMeasurementUsesProposedWidthBeforeTheParentArrangesIt()
    {
        RunOnSta(() =>
        {
            using var control = CreateLayoutControl();
            var advanced = FindAdvancedGroup(control);
            advanced.Parent!.Controls.Remove(advanced);
            using (advanced)
            {
                advanced.Width = 260;
                var narrow = advanced.GetPreferredSize(new Size(300, 0));
                var wide = advanced.GetPreferredSize(new Size(800, 0));

                Assert.True(wide.Height < narrow.Height,
                    $"Proposed width did not reflow wrapped section content: narrow {narrow}, wide {wide}.");
                Assert.Equal(260, advanced.Width);

                foreach (var (width, preferred) in new[] { (300, narrow), (800, wide) })
                {
                    advanced.Size = new Size(width, preferred.Height);
                    advanced.CreateControl();
                    advanced.PerformLayout();
                    var content = Assert.Single(advanced.Controls.OfType<TableLayoutPanel>());
                    Assert.True(advanced.DisplayRectangle.Contains(content.Bounds),
                        $"Measured section at width {width} clips its content: {content.Bounds} in {advanced.DisplayRectangle}.");
                }
            }
        });
    }

    private static RoomVoiceSettingsControl CreateLayoutControl() => new(
        VoicePePreferences.Default with { SessionMode = VoicePeSessionMode.JoydexOwner },
        _ => Task.FromResult(true),
        (_, _) => Task.FromResult(VoicePeWakeTuning.Default),
        (_, tuning, _) => Task.FromResult(tuning),
        allowExternalActions: false);

    private static GroupBox FindAdvancedGroup(Control control) =>
        control.Controls.OfType<TableLayoutPanel>().Single()
            .Controls.OfType<GroupBox>().Single(group => group.Text == "Advanced");

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The hidden settings layout did not complete.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public async Task DemoInspectorDisablesExternalCommandsAndSkipsTheirCallbacks()
    {
        var targetCalls = 0;
        var readCalls = 0;
        var writeCalls = 0;
        var projectCalls = 0;
        using var control = new RoomVoiceSettingsControl(
            VoicePePreferences.Default with { DeviceEndpoint = "http://voice-pe.local/" },
            _ =>
            {
                targetCalls++;
                return Task.FromResult(true);
            },
            (_, _) =>
            {
                readCalls++;
                return Task.FromResult(VoicePeWakeTuning.Default);
            },
            (_, tuning, _) =>
            {
                writeCalls++;
                return Task.FromResult(tuning);
            },
            (_, _) =>
            {
                projectCalls++;
                return Task.FromResult(new CodexProjectCatalog([]));
            },
            allowExternalActions: false);

        var commandNames = new[]
        {
            "RoomVoiceBrowseWorkspace",
            "RoomVoiceProvisionWorkspace",
            "RoomVoiceOpenWorkspace",
            "RoomVoiceOpenSessionRecords",
            "RoomVoiceInstallDesktopBridge",
            "RoomVoiceRemoveDesktopBridge",
            "RoomVoiceTestTarget",
            "RoomVoiceLoadTuning",
            "RoomVoiceApplyTuning",
            "RoomVoiceOpenCaptures",
            "RoomVoiceDeleteCaptures",
        };
        foreach (var commandName in commandNames)
        {
            Assert.False(Assert.Single(control.Controls.Find(
                commandName,
                searchAllChildren: true)).Enabled);
        }

        await control.LoadProjectChoicesAsync();
        await control.LoadWakeTuningAsync(showErrors: false);
        foreach (var commandName in new[]
                 {
                     "RoomVoiceTestTarget",
                     "RoomVoiceLoadTuning",
                     "RoomVoiceApplyTuning",
                 })
        {
            var button = Assert.IsAssignableFrom<Button>(Assert.Single(
                control.Controls.Find(commandName, searchAllChildren: true)));
            button.Enabled = true;
            button.PerformClick();
        }

        Assert.Equal(0, targetCalls);
        Assert.Equal(0, readCalls);
        Assert.Equal(0, writeCalls);
        Assert.Equal(0, projectCalls);
    }

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
