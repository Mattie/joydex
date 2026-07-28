using System.Diagnostics;

namespace Joydex.WirelessPanel.Tests;

public sealed class ProvisioningToolProcessTests
{
    [Fact]
    public async Task Tool_rejects_all_arguments_without_echoing_them()
    {
        const string sentinel = "secret-sentinel-argument-ecfa13";

        var result = await RunToolAsync([sentinel]);

        Assert.Equal(64, result.ExitCode);
        Assert.DoesNotContain(sentinel, result.StandardOutput);
        Assert.DoesNotContain(sentinel, result.StandardError);
        Assert.Contains("accepts no command-line options", result.StandardError);
    }

    [Fact]
    public async Task Tool_rejects_redirected_input_before_prompting_for_password()
    {
        var result = await RunToolAsync([]);

        Assert.Equal(64, result.ExitCode);
        Assert.Contains("interactive console", result.StandardError);
        Assert.DoesNotContain("Digest password", result.StandardOutput);
        Assert.DoesNotContain("Digest password", result.StandardError);
    }

    private static async Task<ProcessResult> RunToolAsync(IReadOnlyList<string> arguments)
    {
        var toolAssembly = typeof(Joydex.WirelessPanel.Configure.Program).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet.exe",
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(toolAssembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The provisioning tool process did not start.");
        process.StandardInput.Close();
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);

        return new ProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
