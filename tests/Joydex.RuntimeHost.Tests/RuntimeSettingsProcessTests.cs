using System.Diagnostics;
using System.Text.Json;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;

namespace Joydex.RuntimeHost.Tests;

public sealed class RuntimeSettingsProcessTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData("IntentPersisted")]
    [InlineData("DocumentWritten")]
    public async Task RestartRestoresExactBytesAfterAPersistencePhaseCrash(string checkpoint)
    {
        using var scratch = new ScratchSettingsDirectory();
        using var processes = new ProbeProcesses();
        AssertSuccess(await processes.RunAsync("recover", scratch.Root, Guid.NewGuid().ToString("D")));
        var priorBytes = File.ReadAllBytes(scratch.TaskAlertsPath);
        var priorVoiceBytes = File.ReadAllBytes(scratch.VoicePath);
        var operationId = Guid.NewGuid();

        var crashed = await processes.RunAsync(
            "crash-apply",
            scratch.Root,
            checkpoint,
            operationId.ToString("D"),
            "4");

        Assert.Equal(137, crashed.ExitCode);
        var checkpointEvent = Assert.Single(crashed.JsonLines, line =>
            line.RootElement.TryGetProperty("value", out var value)
            && string.Equals(value.GetString(), CamelCase(checkpoint), StringComparison.Ordinal));
        if (checkpoint == "IntentPersisted")
        {
            Assert.Equal(priorBytes, File.ReadAllBytes(scratch.TaskAlertsPath));
            Assert.Equal(priorVoiceBytes, File.ReadAllBytes(scratch.VoicePath));
        }
        else
        {
            AssertOneDocumentWasWritten(checkpointEvent, scratch, priorBytes, priorVoiceBytes);
        }

        var recovered = await processes.RunAsync("recover", scratch.Root, operationId.ToString("D"));
        AssertSuccess(recovered);
        Assert.Equal(priorBytes, File.ReadAllBytes(scratch.TaskAlertsPath));
        Assert.Equal(priorVoiceBytes, File.ReadAllBytes(scratch.VoicePath));
        var result = Assert.Single(recovered.JsonLines);
        Assert.Equal("completed", StringValue(result, "operation", "state"));
        Assert.Equal("failedRolledBack", StringValue(result, "operation", "result", "status"));
        Assert.False(BooleanValue(result, "operation", "result", "desiredStateCommitted"));
        Assert.Equal(2, Int32Value(result, "snapshot", "desired", "taskAlerts", "bank"));
    }

    [Fact]
    public async Task RestartPreservesDesiredAndPriorActiveAfterActivationPhaseCrash()
    {
        using var scratch = new ScratchSettingsDirectory();
        using var processes = new ProbeProcesses();
        var operationId = Guid.NewGuid();

        var crashed = await processes.RunAsync(
            "crash-apply",
            scratch.Root,
            "DesiredCommitted",
            operationId.ToString("D"),
            "4");

        Assert.Equal(137, crashed.ExitCode);
        var recovered = await processes.RunAsync("recover", scratch.Root, operationId.ToString("D"));
        AssertSuccess(recovered);
        var result = Assert.Single(recovered.JsonLines);
        Assert.Equal("pendingIdle", StringValue(result, "operation", "result", "status"));
        Assert.True(BooleanValue(result, "operation", "result", "desiredStateCommitted"));
        Assert.Equal(4, Int32Value(result, "snapshot", "desired", "taskAlerts", "bank"));
        Assert.Equal(2, Int32Value(result, "snapshot", "active", "taskAlerts", "bank"));
        Assert.Equal("crash candidate", StringValue(result, "snapshot", "desired", "voice", "pinnedTaskLabel"));
        Assert.NotEqual(
            "crash candidate",
            StringValue(result, "snapshot", "active", "voice", "pinnedTaskLabel"));
    }

    [Fact]
    public async Task RecordedApplyReplaysAfterTheClientLosesItsReplyAndTheHostRestarts()
    {
        using var scratch = new ScratchSettingsDirectory();
        using var processes = new ProbeProcesses();
        var operationId = Guid.NewGuid();
        var first = await processes.RunAsync(
            "apply-lost-reply",
            scratch.Root,
            operationId.ToString("D"),
            "5");

        AssertSuccess(first);
        var prepared = Assert.Single(first.JsonLines);
        var token = StringValue(prepared, "preparationToken");
        var replayed = await processes.RunAsync(
            "recover-replay",
            scratch.Root,
            operationId.ToString("D"),
            token);

        AssertSuccess(replayed);
        var replay = Assert.Single(replayed.JsonLines);
        Assert.Equal("completed", StringValue(replay, "beforeReplay", "state"));
        Assert.Equal("applied", StringValue(replay, "beforeReplay", "result", "status"));
        Assert.Equal(operationId, GuidValue(replay, "replayed", "operationId"));
        Assert.Equal("applied", StringValue(replay, "replayed", "status"));
        Assert.Equal(5, Int32Value(replay, "replayed", "snapshot", "desired", "taskAlerts", "bank"));
    }

    [Fact]
    public async Task ExternalEditBetweenPrepareAndApplyIsPreservedAsAConflict()
    {
        using var scratch = new ScratchSettingsDirectory();
        using var processes = new ProbeProcesses();
        var operationId = Guid.NewGuid();
        var process = processes.Start(
            "external-conflict",
            scratch.Root,
            operationId.ToString("D"),
            "4");

        var preparedLine = await process.StandardOutput.ReadLineAsync().WaitAsync(ProcessTimeout);
        Assert.NotNull(preparedLine);
        using var prepared = JsonDocument.Parse(preparedLine);
        Assert.False(string.IsNullOrWhiteSpace(StringValue(prepared, "preparationToken")));
        var current = TaskAlertPreferencesStore.LoadOrCreate(scratch.TaskAlertsPath);
        TaskAlertPreferencesStore.Save(scratch.TaskAlertsPath, current with { Bank = 5 });
        var externalBytes = File.ReadAllBytes(scratch.TaskAlertsPath);
        await process.StandardInput.WriteLineAsync("apply");
        process.StandardInput.Close();

        var remainingOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await WaitForExitOrKillAsync(process, standardErrorTask);
        var remainingOutput = await remainingOutputTask;
        var standardError = await standardErrorTask;
        Assert.True(process.ExitCode == 0, standardError);
        var result = Assert.Single(ParseLines(remainingOutput));
        Assert.Equal("conflict", StringValue(result, "status"));
        Assert.False(BooleanValue(result, "desiredStateCommitted"));
        Assert.Equal(externalBytes, File.ReadAllBytes(scratch.TaskAlertsPath));
        Assert.Equal("taskAlerts", StringValue(result, "snapshot", "externalCandidates", 0, "aggregate"));
    }

    [Fact]
    public async Task LiveActivationAfterRestartCannotAdoptAnotherAggregatesPendingDesiredValue()
    {
        using var scratch = new ScratchSettingsDirectory();
        using var processes = new ProbeProcesses();
        const string pendingLabel = "pending across restart";
        var pending = await processes.RunAsync(
            "commit-pending-voice",
            scratch.Root,
            Guid.NewGuid().ToString("D"),
            pendingLabel);
        AssertSuccess(pending);
        var pendingResult = Assert.Single(pending.JsonLines);
        var priorActiveLabel = StringValue(pendingResult, "snapshot", "active", "voice", "pinnedTaskLabel");
        Assert.Equal(pendingLabel, StringValue(
            pendingResult,
            "snapshot",
            "desired",
            "voice",
            "pinnedTaskLabel"));

        var applied = await processes.RunAsync(
            "apply-task-alerts",
            scratch.Root,
            Guid.NewGuid().ToString("D"),
            "4");
        AssertSuccess(applied);
        var capture = Assert.Single(applied.JsonLines);
        Assert.Equal(priorActiveLabel, StringValue(capture, "candidates", 0, "voiceLabel"));
        Assert.Equal(pendingLabel, StringValue(
            capture,
            "result",
            "snapshot",
            "desired",
            "voice",
            "pinnedTaskLabel"));
        Assert.Equal(priorActiveLabel, StringValue(
            capture,
            "result",
            "snapshot",
            "active",
            "voice",
            "pinnedTaskLabel"));
        Assert.Equal(4, Int32Value(capture, "result", "snapshot", "active", "taskAlerts", "bank"));
    }

    private static string CamelCase(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private static void AssertOneDocumentWasWritten(
        JsonDocument checkpointEvent,
        ScratchSettingsDirectory scratch,
        byte[] priorTaskAlerts,
        byte[] priorVoice)
    {
        switch (StringValue(checkpointEvent, "aggregate"))
        {
            case "voice":
                Assert.False(priorVoice.SequenceEqual(File.ReadAllBytes(scratch.VoicePath)));
                Assert.Equal(
                    "crash candidate",
                    VoicePePreferencesStore.LoadOrCreate(scratch.VoicePath).PinnedTaskLabel);
                Assert.Equal(priorTaskAlerts, File.ReadAllBytes(scratch.TaskAlertsPath));
                break;
            case "taskAlerts":
                Assert.False(priorTaskAlerts.SequenceEqual(File.ReadAllBytes(scratch.TaskAlertsPath)));
                Assert.Equal(4, TaskAlertPreferencesStore.LoadOrCreate(scratch.TaskAlertsPath).Bank);
                Assert.Equal(priorVoice, File.ReadAllBytes(scratch.VoicePath));
                break;
            default:
                throw new InvalidDataException("The document-written checkpoint did not name a changed aggregate.");
        }
    }

    private static async Task WaitForExitOrKillAsync(Process process, Task<string> standardError)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(ProcessTimeout);
        }
        catch (TimeoutException exception)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw new TimeoutException(
                $"The process probe did not exit within {ProcessTimeout}. Standard error: {await standardError}",
                exception);
        }
    }

    private static void AssertSuccess(ProbeResult result) =>
        Assert.True(result.ExitCode == 0, result.StandardError);

    private static IReadOnlyList<JsonDocument> ParseLines(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonDocument.Parse(line))
        .ToArray();

    private static JsonElement Element(JsonDocument document, params object[] path)
    {
        var current = document.RootElement;
        foreach (var segment in path)
        {
            current = segment switch
            {
                string property => current.GetProperty(property),
                int index => current[index],
                _ => throw new ArgumentException("JSON paths contain only property names and array indexes."),
            };
        }
        return current;
    }

    private static string StringValue(JsonDocument document, params object[] path) =>
        Element(document, path).GetString() ?? throw new InvalidDataException("Expected a JSON string.");

    private static int Int32Value(JsonDocument document, params object[] path) => Element(document, path).GetInt32();
    private static bool BooleanValue(JsonDocument document, params object[] path) => Element(document, path).GetBoolean();
    private static Guid GuidValue(JsonDocument document, params object[] path) => Element(document, path).GetGuid();

    private sealed class ScratchSettingsDirectory : IDisposable
    {
        public ScratchSettingsDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "joydex-runtimehost-process-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string TaskAlertsPath => Path.Combine(Root, "task-alerts.json");
        public string VoicePath => Path.Combine(Root, "voice.json");

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class ProbeProcesses : IDisposable
    {
        private readonly List<Process> _owned = [];

        public Process Start(params string[] arguments)
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "ProcessProbe", "Joydex.ProcessProbe.exe");
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("The process probe was not copied to the test output.", executable);
            }
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start the process probe.");
            _owned.Add(process);
            return process;
        }

        public async Task<ProbeResult> RunAsync(params string[] arguments)
        {
            var process = Start(arguments);
            process.StandardInput.Close();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(ProcessTimeout);
            var output = await outputTask;
            var error = await errorTask;
            return new ProbeResult(process.ExitCode, ParseLines(output), error);
        }

        public void Dispose()
        {
            foreach (var process in _owned)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
                process.Dispose();
            }
            _owned.Clear();
        }
    }

    private sealed record ProbeResult(int ExitCode, IReadOnlyList<JsonDocument> JsonLines, string StandardError);
}
