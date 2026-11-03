using Joydex.Contracts;
using Joydex.Core.TaskAlerts;
using Joydex.RuntimeHost.Production;

namespace Joydex.RuntimeHost.Tests;

public sealed class MixedSourceRuntimeCommandTests
{
    [Fact]
    public void TypedRemoteNavigationPreservesExactSourceAndNativeIdentity()
    {
        var identity = TaskAlertIdentity.RemoteT3("Environment", "Thread");
        var request = new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.NavigateToDesktopTask,
            new(Task: new(identity.NativeThreadId, identity.EnvironmentId, identity)));
        Assert.True(RuntimeCommandCanonicalizer.TryNormalize(request, out var normalized, out var error), error);
        var navigation = WindowsProductionRuntimeOwnerFactory.CreateDesktopTaskNavigationRequest(normalized);
        Assert.Equal(identity, navigation.TaskIdentity);
        Assert.Equal(identity.PoolKey, navigation.SessionId);
    }

    [Theory]
    [InlineData(RuntimeCommandKind.RetargetVoiceOutboxDelivery)]
    [InlineData(RuntimeCommandKind.ListDesktopTasks)]
    public void T3TaskCannotBeUsedAsCodexVoiceOrMessagingTarget(RuntimeCommandKind kind)
    {
        var identity = TaskAlertIdentity.RemoteT3("environment", "thread");
        var request = new RuntimeCommandRequest(Guid.NewGuid(), kind,
            new(Task: new(identity.NativeThreadId, identity.EnvironmentId, identity), DeliveryId: kind == RuntimeCommandKind.RetargetVoiceOutboxDelivery ? "delivery" : null));
        Assert.False(RuntimeCommandCanonicalizer.TryNormalize(request, out _, out var error));
        Assert.Contains("T3 targets support task navigation only", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExplicitUnknownOrContradictoryIdentityIsNotSilentlyTreatedAsCodex(int shape)
    {
        var native = Guid.NewGuid().ToString("D");
        var identity = shape switch
        {
            0 => new TaskAlertIdentity((TaskAlertBackend)999, "local", native),
            1 => TaskAlertIdentity.LocalCodex(Guid.NewGuid().ToString("D")),
            _ => new TaskAlertIdentity(TaskAlertBackend.LocalCodex, "wrong", native),
        };
        var request = new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.NavigateToDesktopTask,
            new(Task: new(native, "local", identity)));
        Assert.False(RuntimeCommandCanonicalizer.TryNormalize(request, out _, out _));
        Assert.Throws<InvalidDataException>(() => WindowsProductionRuntimeOwnerFactory.CreateDesktopTaskNavigationRequest(request));
    }

    [Fact]
    public void RemoteQuestionDismissalKeepsCanonicalLongIdentityAndOnlyNormalizesHashes()
    {
        var identity = TaskAlertIdentity.RemoteT3(new string('\u3042', 512), new string('\u3044', 512));
        var request = new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.DismissTaskAlertQuestions,
            new(TaskAlertQuestions: new(1, identity.PoolKey, [new string('b', 64)])));
        Assert.True(RuntimeCommandCanonicalizer.TryNormalize(request, out var normalized, out var error), error);
        Assert.Equal(identity.PoolKey, normalized.Arguments!.TaskAlertQuestions!.SessionId);
        Assert.Equal([new string('B', 64)], normalized.Arguments.TaskAlertQuestions.QuestionKeys);
    }
}
