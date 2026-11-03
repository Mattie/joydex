using System.Text.Json;
using Joydex.Core.T3;
using Joydex.Windows.T3;

namespace Joydex.Tests;

public sealed class T3ReaderTests
{
    private static T3ConnectionOptions Options => new() { SshHost = "configured-host", EnvironmentId = "env-one" };

    [Fact]
    public void OptionsRejectInvalidEnabledConfigurationsAndKeepDiscoveryAvailable()
    {
        Assert.False(new T3ConnectionOptions().Normalize().Enabled);
        Assert.Equal("host", (new T3ConnectionOptions { SshHost = " host " }).Normalize().SshHost);
        Assert.Throws<ArgumentException>(() => new T3ConnectionOptions { Enabled = true, SshHost = "host" }.Normalize());
        Assert.Throws<ArgumentException>(() => new T3ConnectionOptions { DisplayName = new string('x', 241) }.Normalize());
        Assert.Throws<ArgumentException>(() => new T3ConnectionOptions { EnvironmentId = "id\nnext" }.Normalize());
    }

    [Theory]
    [InlineData("host", true)]
    [InlineData("host.domain-1", true)]
    [InlineData("-oProxyCommand=oops", false)]
    [InlineData("name;whoami", false)]
    [InlineData("user@host", false)]
    [InlineData("host another", false)]
    [InlineData("host\ncommand", false)]
    [InlineData("", false)]
    public void OnlySshConfigurationAliasesAreAccepted(string host, bool expected)
        => Assert.Equal(expected, T3SshTransport.IsValidHost(host));

    [Fact]
    public void SshUsesArgumentListAndConstantRemoteCommand()
    {
        var start = T3SshTransport.CreateStartInfo("configured-host");
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal("", start.Arguments);
        Assert.Equal(new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5", "-o", "ConnectionAttempts=1", "--", "configured-host", "python3", "-" }, start.ArgumentList);
    }

    [Fact]
    public async Task ReaderProjectsMetadataAndRejectsPinMismatchBeforeReturningTasks()
    {
        var transport = new FakeTransport(Snapshot("different-environment"));
        var result = await new T3Reader(transport).ReadAsync(Options, ["thread-1"]);
        Assert.False(result.Success);
        Assert.Equal("EnvironmentMismatch", result.ErrorClass);
        Assert.Empty(result.Tasks);
        Assert.Contains("JOYDEX_OPTIONS = json.loads(base64.b64decode('", transport.Script);
        Assert.DoesNotContain("ssh ", transport.Script);
    }

    [Fact]
    public async Task ConnectionTestDiscoversIdentityWithoutEnablingMonitor()
    {
        var result = await new T3Reader(new FakeTransport(Snapshot())).TestConnectionAsync(Options with { EnvironmentId = "", Enabled = false });
        Assert.True(result.Success);
        Assert.Equal("env-one", result.EnvironmentId);
        Assert.Contains("not verified", result.ContextDiagnostic);
    }

    [Fact]
    public async Task MissingPinAndInvalidAliasesDoNotStartSsh()
    {
        var transport = new FakeTransport(Snapshot());
        var reader = new T3Reader(transport);
        Assert.Equal("EnvironmentRequired", (await reader.ReadAsync(Options with { EnvironmentId = "" }, [])).ErrorClass);
        Assert.Equal("InvalidHost", (await reader.TestConnectionAsync(Options with { SshHost = "host;remote-command" })).ErrorClass);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task RemoteErrorTextIsNeverReturnedAsDiagnostics()
    {
        var result = await new T3Reader(new FakeTransport("{\"success\":false,\"errorClass\":\"private-token\",\"reason\":\"secret-prompt-and-token\"}")).ReadAsync(Options, []);
        Assert.Equal("ReadFailed", result.ErrorClass);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ReadsDoNotOverlapAndCancellationPropagates()
    {
        var transport = new WaitingTransport();
        var reader = new T3Reader(transport);
        using var cancel = new CancellationTokenSource();
        var active = reader.ReadAsync(Options, [], cancel.Token);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Busy", (await reader.ReadAsync(Options, [])).ErrorClass);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
    }

    [Fact]
    public async Task SetupAndRemovalAreExplicitSeparateModes()
    {
        var fingerprint = new string('a', 64);
        var transport = new FakeTransport(JsonSerializer.Serialize(new { success = true, bindingFingerprint = fingerprint }));
        var reader = new T3Reader(transport);
        var result = await reader.SetupShortcutsAsync(Options);
        Assert.True(result.Success);
        Assert.Equal(fingerprint, result.BindingFingerprint);
        Assert.Contains("not verified", result.ContextDiagnostic);
        Assert.Equal("setup", ScriptOptions(transport.Script).GetProperty("mode").GetString());
        await reader.SetupShortcutsAsync(Options, remove: true);
        Assert.Equal("remove", ScriptOptions(transport.Script).GetProperty("mode").GetString());
    }

    [Fact]
    public async Task UnsupportedShortcutPreviewLeavesMonitoringAvailableAndIncludesRules()
    {
        var transport = new FakeTransport("{\"success\":false,\"errorClass\":\"UnsupportedShortcuts\",\"reason\":\"PRIVATE DETAILS\",\"rules\":[{\"command\":\"chat.new\",\"key\":\"alt+n\"}]}");
        var result = await new T3Reader(transport).SetupShortcutsAsync(Options);
        Assert.False(result.Success);
        Assert.Equal("UnsupportedShortcuts", result.ErrorClass);
        Assert.Single(result.Rules);
        Assert.Equal("alt+n", result.Rules[0].Key);
        Assert.Contains("Read-only monitoring remains available", result.Diagnostics.Single());
        Assert.DoesNotContain("PRIVATE", result.Reason);
    }

    [Fact]
    public async Task MetadataLabelsAreBoundedAndUnknownStatusesRemainUnknown()
    {
        var raw = JsonSerializer.Serialize(new
        {
            success = true, environmentId = "env-one", sourceEpoch = new string('a', 64), sequence = 7,
            capturedAt = "2026-10-20T10:20:30Z", isComplete = true,
            contextDiagnostic = "unverified remote claim", bindingFingerprint = "private arbitrary content",
            tasks = new[] { new { id = "thread-1", title = "a\0b\r\n" + new string('x', 300), status = "future-status" } },
        });
        var result = await new T3Reader(new FakeTransport(raw)).ReadAsync(Options, []);
        Assert.True(result.Success);
        Assert.Null(result.BindingFingerprint);
        Assert.Contains("not verified", result.ContextDiagnostic);
        Assert.Equal("unknown", result.Tasks.Single().Status);
        Assert.Equal(256, result.Tasks.Single().Title!.Length);
        Assert.DoesNotContain(result.Tasks.Single().Title!, char.IsControl);
    }

    [Fact]
    public async Task SetupRejectsInvalidCommandsAndUntrustedFingerprintsInFailurePreview()
    {
        foreach (var raw in new[]
        {
            "{\"success\":false,\"errorClass\":\"UnsupportedShortcuts\",\"bindingFingerprint\":\"private data\"}",
            "{\"success\":true,\"rules\":[{\"command\":\"arbitrary.command\",\"key\":\"alt+n\"}]}",
            "{\"success\":true,\"rules\":[{\"command\":\"chat.new\",\"key\":\"alt+n\\nPRIVATE\"}]}",
        })
        {
            var result = await new T3Reader(new FakeTransport(raw)).SetupShortcutsAsync(Options);
            Assert.False(result.Success);
            Assert.Equal("InvalidMetadata", result.ErrorClass);
            Assert.Empty(result.Rules);
            Assert.Null(result.BindingFingerprint);
        }
    }

    [Fact]
    public async Task InvalidOrOversizedPayloadCannotCreateObservations()
    {
        foreach (var raw in new[] { "not-json", Snapshot().Replace("\"sequence\":7", "\"sequence\":-1"), Snapshot().Replace("\"env-one\"", "\"\"") })
            Assert.False((await new T3Reader(new FakeTransport(raw)).ReadAsync(Options, [])).Success);
        var huge = new string('x', T3SshTransport.MaximumOutputBytes + 1);
        Assert.Equal("PayloadLimit", (await new T3Reader(new FakeTransport(huge)).ReadAsync(Options, [])).ErrorClass);
    }

    private static JsonElement ScriptOptions(string script)
    {
        const string prefix = "JOYDEX_OPTIONS = json.loads(base64.b64decode('";
        var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = script.IndexOf("'", start, StringComparison.Ordinal);
        return JsonDocument.Parse(Convert.FromBase64String(script[start..end])).RootElement.Clone();
    }

    private static string Snapshot(string environment = "env-one") => JsonSerializer.Serialize(new
    {
        success = true, environmentId = environment, sourceEpoch = new string('a', 64), sequence = 7,
        capturedAt = "2026-10-20T10:20:30Z", isComplete = true,
        tasks = new[] { new { id = "thread-1", runId = "run-1", status = "running", readComplete = true, requestsComplete = false } },
    });

    private sealed class FakeTransport(string response) : IT3SshTransport
    {
        public string Script { get; private set; } = "";
        public int Calls { get; private set; }
        public Task<string> ExecuteAsync(string host, string script, CancellationToken cancellationToken)
        {
            Calls++;
            Script = script;
            return Task.FromResult(response);
        }
    }

    private sealed class WaitingTransport : IT3SshTransport
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> ExecuteAsync(string host, string script, CancellationToken cancellationToken)
        {
            Started.SetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "";
        }
    }
}
