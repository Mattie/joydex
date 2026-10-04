using Joydex.Contracts;
using Joydex.Core.Voice;
using Joydex.RuntimeHost.Production;

namespace Joydex.RuntimeHost.Tests;

public sealed class WindowsProductionRuntimeCommandsTests
{
    [Fact]
    public void RequestedLocalDesktopTaskCreatesDirectNavigationWithoutVoiceSettings()
    {
        var taskId = Guid.NewGuid();
        var request = new RuntimeCommandRequest(
            Guid.NewGuid(),
            RuntimeCommandKind.NavigateToDesktopTask,
            new RuntimeCommandArguments(
                Task: new RuntimeTaskReference(
                    $"codex://threads/{taskId:D}",
                    " LOCAL ")));

        Assert.True(RuntimeCommandCanonicalizer.TryNormalize(
            request,
            out var normalizedRequest,
            out var error), error);
        var navigation = WindowsProductionRuntimeOwnerFactory
            .CreateDesktopTaskNavigationRequest(normalizedRequest);

        Assert.Equal(0, navigation.Slot);
        Assert.Equal(0, navigation.Bank);
        Assert.Equal(0, navigation.Button);
        Assert.Equal(taskId.ToString("D"), navigation.SessionId);
    }

    [Fact]
    public void DirectDesktopTaskNavigationRejectsMissingNonLocalAndInvalidTargets()
    {
        var operationId = Guid.NewGuid();
        var taskId = Guid.NewGuid().ToString("D");
        RuntimeCommandRequest[] requests =
        [
            new(operationId, RuntimeCommandKind.NavigateToDesktopTask),
            new(
                operationId,
                RuntimeCommandKind.NavigateToDesktopTask,
                new RuntimeCommandArguments(
                    Task: new RuntimeTaskReference(taskId, "remote"))),
            new(
                operationId,
                RuntimeCommandKind.NavigateToDesktopTask,
                new RuntimeCommandArguments(
                    Task: new RuntimeTaskReference("not-a-task", "local"))),
        ];

        foreach (var request in requests)
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
                WindowsProductionRuntimeOwnerFactory.CreateDesktopTaskNavigationRequest(request));
            Assert.Equal("A valid local Desktop task target is required.", exception.Message);
        }
    }

    [Fact]
    public void OutboxFailureRecordsOnlyAnUnconfirmedDelivery()
    {
        var root = Path.Combine(Path.GetTempPath(), "Joydex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var outbox = new VoiceTaskOutbox(root);
            var target = new DesktopTaskSummary(
                Guid.NewGuid().ToString("D"),
                "local",
                "Target",
                "idle",
                null,
                null,
                1);
            var confirmed = outbox.Hold(target, "Confirmed", "session", "Unavailable");
            outbox.Remove(confirmed.Id);

            WindowsProductionRuntimeOwnerFactory.RecordUnconfirmedOutboxFailure(
                outbox,
                confirmed,
                deliveryConfirmed: true,
                exception: new IOException("Cleanup failed."));

            Assert.Empty(outbox.Load());

            var unconfirmed = outbox.Hold(target, "Unconfirmed", "session", "Unavailable");
            outbox.Remove(unconfirmed.Id);
            WindowsProductionRuntimeOwnerFactory.RecordUnconfirmedOutboxFailure(
                outbox,
                unconfirmed,
                deliveryConfirmed: false,
                exception: new IOException("Delivery failed."));

            var restored = Assert.Single(outbox.Load());
            Assert.Equal(unconfirmed.Id, restored.Id);
            Assert.Equal(2, restored.Attempts);
            Assert.Equal("Delivery failed.", restored.LatestError);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
