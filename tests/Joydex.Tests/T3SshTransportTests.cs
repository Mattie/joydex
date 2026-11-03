using System.Diagnostics;
using Joydex.Windows.T3;

namespace Joydex.Tests;

public sealed class T3SshTransportTests
{
    [Fact]
    public async Task FiniteChildReturnsStdoutWithoutDisclosingStderr()
    {
        var transport = Fixture("$null = [Console]::In.ReadToEnd(); [Console]::Error.Write('private stderr'); [Console]::Out.Write('{\"safe\":true}')");
        Assert.Equal("{\"safe\":true}", await transport.ExecuteAsync("fixture", "synthetic stdin", CancellationToken.None));
    }

    [Fact]
    public async Task FailedChildReturnsOnlySafeError()
    {
        var transport = Fixture("$null = [Console]::In.ReadToEnd(); [Console]::Error.Write('private token or prompt'); exit 2");
        var error = await Assert.ThrowsAsync<T3TransportException>(() => transport.ExecuteAsync("fixture", "synthetic stdin", CancellationToken.None));
        Assert.Equal("SshFailed", error.ErrorClass);
        Assert.DoesNotContain("private token", error.ToString());
    }

    [Fact]
    public async Task DeadlineStopsOwnedChildAndReturnsBoundedFailure()
    {
        using var temporary = new TemporaryMarker();
        var transport = Fixture("$null = [Console]::In.ReadToEnd(); [IO.File]::WriteAllText($env:JOYDEX_T3_TEST_MARKER, [string]$PID); Start-Sleep -Seconds 60",
            TimeSpan.FromSeconds(10), temporary.Path);
        var error = await Assert.ThrowsAsync<T3TransportException>(() => transport.ExecuteAsync("fixture", "synthetic stdin", CancellationToken.None));
        Assert.Equal("Timeout", error.ErrorClass);
        AssertChildExited(temporary.Path);
    }

    [Fact]
    public async Task CancellationStopsOwnedChild()
    {
        using var temporary = new TemporaryMarker();
        var transport = Fixture("$null = [Console]::In.ReadToEnd(); [IO.File]::WriteAllText($env:JOYDEX_T3_TEST_MARKER, [string]$PID); Start-Sleep -Seconds 60",
            TimeSpan.FromSeconds(30), temporary.Path);
        using var cancellation = new CancellationTokenSource();
        var read = transport.ExecuteAsync("fixture", "synthetic stdin", cancellation.Token);
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(temporary.Path) && deadline.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(25);
        Assert.True(File.Exists(temporary.Path));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        AssertChildExited(temporary.Path);
    }

    [Fact]
    public async Task OutputCapStopsFloodingChild()
    {
        var transport = Fixture("$null = [Console]::In.ReadToEnd(); $chunk = 'x' * 65536; for ($i = 0; $i -lt 130; $i++) { [Console]::Out.Write($chunk) }; Start-Sleep -Seconds 60",
            TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<T3TransportException>(() => transport.ExecuteAsync("fixture", "synthetic stdin", CancellationToken.None));
        Assert.Equal("PayloadLimit", error.ErrorClass);
    }

    private static T3SshTransport Fixture(string command, TimeSpan? timeout = null, string? marker = null)
        => new(_ =>
        {
            var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(argument);
            if (marker is not null) start.Environment["JOYDEX_T3_TEST_MARKER"] = marker;
            return start;
        }, timeout ?? TimeSpan.FromSeconds(10));

    private static void AssertChildExited(string marker)
    {
        Assert.True(File.Exists(marker));
        var id = int.Parse(File.ReadAllText(marker));
        try { using var child = Process.GetProcessById(id); Assert.True(child.HasExited); }
        catch (ArgumentException) { }
    }

    private sealed class TemporaryMarker : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "joydex-t3-fixture-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }
}
