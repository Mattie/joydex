using Joydex.App;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertsFormTests
{
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
}
