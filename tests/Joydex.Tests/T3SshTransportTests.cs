using System.Diagnostics;
using System.IO.Pipes;
using Joydex.Windows.T3;

namespace Joydex.Tests;

public sealed class T3SshTransportTests
{
    [Fact]
    public async Task FiniteChildReturnsStdoutWithoutDisclosingStderr()
    {
        var transport = Fixture("finite");
        Assert.Equal("{\"safe\":true}", await transport.ExecuteAsync("fixture", "synthetic stdin", CancellationToken.None));
    }

    [Fact]
    public async Task FailedChildReturnsOnlySafeError()
    {
        var transport = Fixture("fail");
        var error = await Assert.ThrowsAsync<T3TransportException>(() => transport.ExecuteAsync("fixture", "synthetic stdin", CancellationToken.None));
        Assert.Equal("SshFailed", error.ErrorClass);
        Assert.DoesNotContain("private token", error.ToString());
    }

    [Fact]
    public async Task WholeOperationDeadlineReturnsBoundedFailureIncludingStartup()
    {
        var transport = Fixture("wait", TimeSpan.FromMilliseconds(300));
        var error = await Assert.ThrowsAsync<T3TransportException>(() => transport.ExecuteAsync("fixture", "synthetic stdin", CancellationToken.None));
        Assert.Equal("Timeout", error.ErrorClass);
    }

    [Fact]
    public async Task CancellationStopsOwnedChild()
    {
        var pipeName = "joydex-t3-fixture-" + Guid.NewGuid().ToString("N");
        using var ready = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var transport = Fixture("wait", TimeSpan.FromSeconds(30), pipeName);
        using var cancellation = new CancellationTokenSource();
        var read = transport.ExecuteAsync("fixture", "synthetic stdin", cancellation.Token);
        try
        {
            using var readinessTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var connected = ready.WaitForConnectionAsync(readinessTimeout.Token);
            if (await Task.WhenAny(connected, read) == read) await read;
            await connected;
            using var reader = new StreamReader(ready);
            var childId = int.Parse((await reader.ReadLineAsync(readinessTimeout.Token))!, System.Globalization.CultureInfo.InvariantCulture);
            using var child = Process.GetProcessById(childId);
            Assert.False(child.HasExited);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.True(child.HasExited);
        }
        finally
        {
            // An assertion or readiness timeout must not leave the owned read running.
            cancellation.Cancel();
            try { await read.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (T3TransportException) { }
        }
    }

    [Fact]
    public async Task OutputCapStopsFloodingChild()
    {
        var transport = Fixture("flood", TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<T3TransportException>(() => transport.ExecuteAsync("fixture", "synthetic stdin", CancellationToken.None));
        Assert.Equal("PayloadLimit", error.ErrorClass);
    }

    private static T3SshTransport Fixture(string mode, TimeSpan? timeout = null, string readinessPipe = "-")
        => new(_ =>
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Joydex.Secrets.ProcessFixture.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "--t3-transport", mode, readinessPipe }) start.ArgumentList.Add(argument);
            return start;
        }, timeout ?? TimeSpan.FromSeconds(10));
}
