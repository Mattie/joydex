using System.Diagnostics;
using System.Text.Json;
using Joydex.Core.Config;

namespace Joydex.RuntimeHost.Tests;

public sealed class RuntimeHostProcessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ActualHostUsesTheExactCustomConfigurationAndKeepsRuntimeIdentityAfterClientLoss()
    {
        using var scratch = new ScratchHostDirectory();
        await using var host = await RuntimeHostProcess.StartAsync(scratch);
        var first = await host.RunProbeAsync(0, "runtime-apply-crash", "5");
        Assert.Equal(137, first.ExitCode);
        var applied = Assert.Single(first.JsonLines);
        Assert.Equal(5, Int(applied, "applied", "snapshot", "desired", "taskAlerts", "bank"));
        Assert.True(File.Exists(scratch.ConfigPath));
        Assert.False(File.Exists(Path.Combine(scratch.Root, "config.json")));

        var captureId = Guid.NewGuid();
        var second = await host.RunProbeAsync(1, "runtime-inspect", captureId.ToString("D"));
        AssertSuccess(second);
        var inspected = Assert.Single(second.JsonLines);
        Assert.Equal(GuidValue(applied, "before", "engineEpoch"), GuidValue(inspected, "attach", "snapshot", "engineEpoch"));
        Assert.Equal(host.ProcessId, Int(inspected, "attach", "snapshot", "identity", "processId"));
        Assert.Equal(host.DataRootId, String(inspected, "attach", "snapshot", "identity", "dataRootId"));
        Assert.Equal(Long(applied, "before", "identity", "runtimeGeneration"),
            Long(inspected, "attach", "snapshot", "identity", "runtimeGeneration"));
        Assert.Equal(String(applied, "before", "input", "sources", 0, "sourceId"),
            String(inspected, "attach", "snapshot", "input", "sources", 0, "sourceId"));
        Assert.Equal(Long(applied, "before", "input", "sources", 0, "generation"),
            Long(inspected, "attach", "snapshot", "input", "sources", 0, "generation"));
    }

    [Fact]
    public async Task DisconnectReleasesCaptureWithoutTransferringItToReplacementClient()
    {
        using var scratch = new ScratchHostDirectory();
        await using var host = await RuntimeHostProcess.StartAsync(scratch);
        var first = await host.RunProbeAsync(0, "runtime-capture-crash");
        Assert.Equal(137, first.ExitCode);
        var captured = Assert.Single(first.JsonLines);
        var captureId = GuidValue(captured, "capture", "lease", "captureId");

        var second = await host.RunProbeAsync(1, "runtime-inspect", captureId.ToString("D"));
        AssertSuccess(second);
        var inspected = Assert.Single(second.JsonLines);
        Assert.Equal("notFound", String(inspected, "capture", "status"));
        Assert.Equal(0, Element(inspected, "attach", "snapshot", "input", "captures").GetArrayLength());
        Assert.Equal(GuidValue(captured, "snapshot", "engineEpoch"), GuidValue(inspected, "attach", "snapshot", "engineEpoch"));
        Assert.Equal(ResourceIdentities(captured, "snapshot"), ResourceIdentities(inspected, "attach", "snapshot"));
    }

    [Fact]
    public async Task ReconnectReplaysSettingsEventsAndRejectsAStaleRevision()
    {
        using var scratch = new ScratchHostDirectory();
        await using var host = await RuntimeHostProcess.StartAsync(scratch);
        var first = await host.RunProbeAsync(0, "runtime-apply-crash", "4");
        Assert.Equal(137, first.ExitCode);
        var applied = Assert.Single(first.JsonLines);
        var epoch = GuidValue(applied, "before", "engineEpoch");
        var cursor = Long(applied, "before", "eventCursor");
        var revision = Long(applied, "before", "settings", "revision");

        var second = await host.RunProbeAsync(1, "runtime-reconnect",
            epoch.ToString("D"), cursor.ToString(), revision.ToString());
        AssertSuccess(second);
        var reconnect = Assert.Single(second.JsonLines);
        Assert.False(Bool(reconnect, "attach", "resynchronizationRequired"));
        var kinds = Element(reconnect, "events").EnumerateArray()
            .Select(item => item.GetProperty("kind").GetString()).ToArray();
        Assert.Contains("settingsChanged", kinds);
        Assert.Contains("operationCompleted", kinds);
        Assert.Equal("conflict", String(reconnect, "stale", "status"));
    }

    [Fact]
    public async Task ReplacementClientRecoversACompletedOperationAfterClientLoss()
    {
        using var scratch = new ScratchHostDirectory();
        await using var host = await RuntimeHostProcess.StartAsync(scratch);
        var operationId = Guid.NewGuid();
        var first = await host.RunProbeAsync(0, "runtime-apply-client-loss", operationId.ToString("D"), "3");
        Assert.Equal(137, first.ExitCode);

        var second = await host.RunProbeAsync(1, "runtime-operation", operationId.ToString("D"));
        AssertSuccess(second);
        var result = Assert.Single(second.JsonLines);
        Assert.Equal("completed", String(result, "operation", "state"));
        Assert.Equal("applied", String(result, "operation", "result", "status"));
        Assert.Equal(3, Int(result, "operation", "result", "snapshot", "desired", "taskAlerts", "bank"));
    }

    private static void AssertSuccess(ProbeResult result) => Assert.True(result.ExitCode == 0, result.StandardError);
    private static JsonElement Element(JsonDocument document, params object[] path)
    {
        var value = document.RootElement;
        foreach (var segment in path) value = segment is string property ? value.GetProperty(property) : value[(int)segment];
        return value;
    }
    private static string String(JsonDocument document, params object[] path) => Element(document, path).GetString()!;
    private static int Int(JsonDocument document, params object[] path) => Element(document, path).GetInt32();
    private static long Long(JsonDocument document, params object[] path) => Element(document, path).GetInt64();
    private static bool Bool(JsonDocument document, params object[] path) => Element(document, path).GetBoolean();
    private static Guid GuidValue(JsonDocument document, params object[] path) => Element(document, path).GetGuid();
    private static string[] ResourceIdentities(JsonDocument document, params object[] snapshotPath) =>
        Element(document, snapshotPath.Concat(new object[] { "identity", "resources" }).ToArray())
            .EnumerateArray()
            .Select(resource => string.Join('|',
                resource.GetProperty("resource").GetString(),
                resource.GetProperty("ownerId").GetString(),
                resource.GetProperty("generation").GetInt64(),
                resource.GetProperty("state").GetString()))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private sealed class ScratchHostDirectory : IDisposable
    {
        public ScratchHostDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "joydex-runtimehost-process-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ConfigPath = Path.Combine(Root, $"custom-{Guid.NewGuid():N}.json");
            ConfigStore.Save(ConfigPath, CompanionConfig.CreateSafeDefault());
            Assert.True(Path.IsPathFullyQualified(ConfigPath));
            Assert.True(File.Exists(ConfigPath));
            Assert.True(ConfigStore.LoadOrCreate(ConfigPath).Safety.DryRun);
        }
        public string Root { get; }
        public string ConfigPath { get; }
        public void Dispose()
        {
            var expectedParent = Path.Combine(Path.GetTempPath(), "joydex-runtimehost-process-tests");
            if (Directory.Exists(Root) && Path.GetFullPath(Root).StartsWith(Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class RuntimeHostProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ScratchHostDirectory _scratch;
        private readonly Ready _ready;
        private readonly Task<string> _standardError;
        private RuntimeHostProcess(Process process, ScratchHostDirectory scratch, Ready ready, Task<string> standardError)
        { _process = process; _scratch = scratch; _ready = ready; _standardError = standardError; }
        public int ProcessId => _process.Id;
        public string DataRootId => _ready.DataRootId;

        public static async Task<RuntimeHostProcess> StartAsync(ScratchHostDirectory scratch)
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "Joydex.RuntimeHost.exe");
            var pipe = $"joydex-process-test-{Guid.NewGuid():N}";
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var argument in new[] { "--synthetic", "--config", scratch.ConfigPath, "--pipe-name", pipe })
                start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start RuntimeHost.");
            var standardError = process.StandardError.ReadToEndAsync();
            try
            {
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(Timeout);
                var ready = JsonSerializer.Deserialize<Ready>(line ?? throw new InvalidDataException("RuntimeHost emitted no ready record."),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Invalid ready record.");
                Assert.Equal(process.Id, ready.ProcessId);
                Assert.Equal(2, ready.SchemaVersion);
                Assert.Equal(2, ready.LaunchTickets.Length);
                return new RuntimeHostProcess(process, scratch, ready, standardError);
            }
            catch (Exception exception)
            {
                try
                {
                    await StopAsync(process);
                }
                finally
                {
                    process.Dispose();
                }
                throw new InvalidOperationException(
                    $"RuntimeHost did not publish a valid ready record. Standard error: {await standardError}",
                    exception);
            }
        }

        public async Task<ProbeResult> RunProbeAsync(int ticketIndex, string command, params string[] trailing)
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "ProcessProbe", "Joydex.ProcessProbe.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            };
            foreach (var argument in new[] { command, _scratch.ConfigPath, _ready.PipeName, _ready.DataRootId, _ready.LaunchTickets[ticketIndex].Value }.Concat(trailing))
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start ProcessProbe.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(Timeout);
            }
            catch (Exception exception)
            {
                try
                {
                    await StopAsync(process, cooperative: false);
                }
                finally
                {
                    await StopAsync(_process, cooperative: true);
                }
                throw new TimeoutException(
                    $"ProcessProbe {process.Id} did not finish. RuntimeHost {_process.Id} standard error: {await _standardError}",
                    exception);
            }
            return new ProbeResult(process.ExitCode, Parse(await outputTask), await errorTask);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync(_process, cooperative: true);
                var standardError = await _standardError;
                if (_process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"RuntimeHost {_process.Id} exited with {_process.ExitCode}. Standard error: {standardError}");
                }
            }
            finally
            {
                _process.Dispose();
            }
        }
        private static async Task StopAsync(Process process, bool cooperative = true)
        {
            if (process.HasExited) return;
            if (cooperative)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("shutdown");
                    process.StandardInput.Close();
                }
                catch (Exception) when (process.HasExited)
                {
                    return;
                }
                catch (IOException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
        private static IReadOnlyList<JsonDocument> Parse(string output) => output
            .Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
    }

    private sealed record Ticket(string Value, DateTimeOffset ExpiresAtUtc);
    private sealed record Ready(int SchemaVersion, int ProcessId, string PipeName, string DataRootId, Ticket[] LaunchTickets);
    private sealed record ProbeResult(int ExitCode, IReadOnlyList<JsonDocument> JsonLines, string StandardError);
}
