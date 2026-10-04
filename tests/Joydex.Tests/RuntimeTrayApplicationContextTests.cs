using Joydex.App;
using Joydex.Contracts;

namespace Joydex.Tests;

public sealed class RuntimeTrayApplicationContextTests
{
    [Fact]
    public async Task UnexpectedShutdownRpcFailureContinuesExitFallback()
    {
        var invoked = false;

        var delivered = await RuntimeTrayApplicationContext.TryRequestRuntimeShutdownAsync(
            (_, _) =>
            {
                invoked = true;
                return Task.FromException<RuntimeCommandResult>(
                    new ApplicationException("The connection failed unexpectedly."));
            },
            CancellationToken.None);

        Assert.True(invoked);
        Assert.False(delivered);
    }
}
