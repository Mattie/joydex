using Joydex.Core.TaskAlerts;
using Joydex.Virpil;

namespace Joydex.Windows.TaskAlerts;

public sealed record VirpilTaskAlertFrames(
    byte[] Throttle,
    byte[] Alpha,
    bool HasAlert);

public static class VirpilLedFrameComposer
{
    public static VirpilTaskAlertFrames Compose(
        TaskAlertSnapshot snapshot,
        TaskAlertLedOptions options,
        bool suppressAlerts = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        options = options.Normalize();

        var throttle = VirpilLedProtocol.EmptyFrame();
        var baseline = options.BankColors(snapshot.Bank);
        for (var index = 0; index < VirpilDevices.Throttle.PhysicalLedCount; index++)
        {
            var color = baseline[index];
            throttle[index] = VirpilLedProtocol.EncodeColor(color.Red, color.Green, color.Blue);
        }

        var visibleAssignments = snapshot.Enabled && !suppressAlerts
            ? snapshot.Assignments.Where(assignment => IsVisible(assignment.Slot, snapshot.Bank)).ToArray()
            : [];
        foreach (var assignment in visibleAssignments)
        {
            var color = options.ColorFor(assignment.State);
            throttle[TaskAlertSlots.Button(assignment.Slot) - 1] =
                VirpilLedProtocol.EncodeColor(color.Red, color.Green, color.Blue);
        }

        var alpha = VirpilLedProtocol.EmptyFrame();
        var highest = snapshot.Enabled && !suppressAlerts
            ? snapshot.Assignments.OrderByDescending(assignment => Priority(assignment.State)).FirstOrDefault()
            : null;
        if (highest is not null)
        {
            var color = options.ColorFor(highest.State);
            alpha[0] = VirpilLedProtocol.EncodeColor(color.Red, color.Green, color.Blue);
        }
        else if (options.AlphaIdleColor() is { } idle)
        {
            alpha[0] = VirpilLedProtocol.EncodeColor(idle.Red, idle.Green, idle.Blue);
        }

        return new VirpilTaskAlertFrames(
            throttle,
            alpha,
            HasAlert: visibleAssignments.Length > 0 || highest is not null);
    }

    private static bool IsVisible(int slot, int bank) => TaskAlertSlots.Page(slot) switch
    {
        TaskAlertPage.Primary => bank is >= 2 and <= 4,
        TaskAlertPage.Overflow => bank == 1,
        _ => false,
    };

    private static int Priority(TaskAlertState state) => state switch
    {
        TaskAlertState.Fault => 4,
        TaskAlertState.Approval => 3,
        TaskAlertState.Completed => 2,
        TaskAlertState.Running => 1,
        _ => 0,
    };
}
