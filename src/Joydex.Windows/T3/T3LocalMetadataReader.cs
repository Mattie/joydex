using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Joydex.Core.T3;
using Joydex.Windows.Actions;

namespace Joydex.Windows.T3;

public sealed partial class T3Reader
{
    public async Task<T3ReadResult> ReadDesktopEnvironmentAsync(T3DesktopEnvironment environment,
        IReadOnlyCollection<string> knownThreadIds, CancellationToken cancellationToken = default)
    {
        if (!environment.Enabled) return Failure("Disabled", "This desktop environment is disabled.");
        if (!ValidId(environment.EnvironmentId)) return Failure("EnvironmentRequired", "The native environment identity is unavailable.");
        if (environment.UnsupportedReason is not null) return Failure("UnsupportedRoute", environment.UnsupportedReason);
        if (environment.TransportKind == "wsl")
        {
            if (!T3SshTransport.IsValidHost(environment.WslDistro)) return Failure("UnsupportedRoute", "The WSL distribution identity is unsupported.");
            var result = await new T3Reader(new T3WslTransport()).ReadAsync(new() { SshHost = environment.WslDistro!,
                EnvironmentId = environment.EnvironmentId, DisplayName = environment.DisplayName }, knownThreadIds, cancellationToken).ConfigureAwait(false);
            return result with { TransportKind = "wsl" };
        }
        if (!environment.IsLocal)
            return await ReadCoreAsync(new() { SshHost = environment.SshHost ?? "", EnvironmentId = environment.EnvironmentId,
                DisplayName = environment.DisplayName }, knownThreadIds, "read", cancellationToken).ConfigureAwait(false);
        if (knownThreadIds.Count > 128 || knownThreadIds.Any(id => !ValidId(id))) return Failure("InputLimit", "The tracked thread list exceeds its bounds.");
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Failure("Busy", "A T3 operation is already in progress.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            var result = await new T3LocalMetadataReader().ReadAsync(DesktopHome, environment.EnvironmentId, knownThreadIds, deadline.Token).ConfigureAwait(false);
            if (!result.Success) return result;
            if (!ValidHash(result.SourceEpoch) || !ValidCatalogObservation(result) || result.Tasks.Any(task => !ValidTask(task))) return Failure("InvalidMetadata", "The local environment returned invalid metadata.");
            return result with { DisplayName = environment.DisplayName, TransportKind = "local", ContextDiagnostic = UnknownContext };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return SanitizeFailure("Timeout"); }
        catch (OperationCanceledException) { throw; }
        catch (T3TransportException error) { return SanitizeFailure(error.ErrorClass, error.ExitCode); }
        catch { return Failure("LocalReadFailed", "The local metadata read failed; private details were withheld."); }
        finally { _operation.Release(); }
    }
}

/// <summary>Finite CLI grants and loopback HTTP only; no desktop bearer/cookie reuse or server launch.</summary>
internal sealed class T3LocalMetadataReader
{
    private readonly Func<string, string[], CancellationToken, Task<string>> _cli;
    private readonly Func<Uri, string?, CancellationToken, Task<JsonDocument>> _get;
    private readonly TimeProvider _clock;
    internal T3LocalMetadataReader(Func<string, string[], CancellationToken, Task<string>>? cli = null,
        Func<Uri, string?, CancellationToken, Task<JsonDocument>>? get = null, TimeProvider? clock = null)
    { _cli = cli ?? RunCliAsync; _get = get ?? GetAsync; _clock = clock ?? TimeProvider.System; }

    internal async Task<T3ReadResult> ReadAsync(string home, string expectedId, IReadOnlyCollection<string> knownIds, CancellationToken cancellationToken)
    {
        var root = Path.Combine(home, "userdata");
        if (Encoding.UTF8.GetString(T3Reader.ReadBoundedFile(Path.Combine(root, "environment-id"))).Trim() != expectedId)
            throw new T3TransportException("EnvironmentMismatch", "The local environment identity changed.");
        var runtimePath = Path.Combine(root, "server-runtime.json");
        var runtimeRaw = T3Reader.ReadBoundedFile(runtimePath);
        using var runtime = JsonDocument.Parse(runtimeRaw);
        var origin = new Uri(runtime.RootElement.GetProperty("origin").GetString()!);
        if (!IsSupportedLoopbackOrigin(origin))
            throw new T3TransportException("LoopbackRequired", "Only the local loopback API is permitted.");
        if (!runtime.RootElement.TryGetProperty("startedAt", out var started) || !DateTimeOffset.TryParse(started.GetString(), out _))
            throw new InvalidDataException();
        using (var descriptor = await _get(new(origin, "/.well-known/t3/environment"), null, cancellationToken).ConfigureAwait(false))
            if (descriptor.RootElement.GetProperty("environmentId").GetString() != expectedId)
                throw new T3TransportException("EnvironmentMismatch", "The local endpoint identifies another environment.");

        using var credential = JsonDocument.Parse(await _cli(home,
            ["auth", "session", "issue", "--scope", "orchestration:read", "--ttl", "5m", "--label", "Joydex metadata reader", "--json"], cancellationToken).ConfigureAwait(false));
        var sessionId = credential.RootElement.GetProperty("sessionId").GetString();
        T3ReadResult result;
        try
        {
            var scopes = credential.RootElement.GetProperty("scopes");
            if (scopes.GetArrayLength() != 1 || scopes[0].GetString() != "orchestration:read")
                throw new T3TransportException("UnexpectedScopes", "The temporary grant was not exactly read-only.");
            var token = credential.RootElement.GetProperty("token").GetString();
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(sessionId)) throw new InvalidDataException();
            var catalogObservedAt = _clock.GetUtcNow();
            using var shell = await _get(new(origin, "/api/orchestration/shell?omitIdleSubagents=true"), token, cancellationToken).ConfigureAwait(false);
            var threads = shell.RootElement.GetProperty("threads");
            var sequence = shell.RootElement.GetProperty("snapshotSequence").GetInt64();
            if (threads.GetArrayLength() > 1000) throw new T3TransportException("PayloadLimit", "The local thread list exceeds its bound.");
            var rows = threads.EnumerateArray().Where(thread => !IsPresent(thread, "deletedAt"))
                .ToDictionary(thread => RequiredId(thread, "id"), StringComparer.Ordinal);
            var tasks = new List<T3TaskMetadata>();
            var complete = true;
            var detailCount = 0;
            foreach (var id in knownIds.Concat(rows.Keys).Distinct(StringComparer.Ordinal))
            {
                var hasShell = rows.TryGetValue(id, out var thread);
                var task = ShellTask(id, thread, shell.RootElement);
                var needsDetail = knownIds.Contains(id) || IsPresent(thread, "pendingRuntimeRequest")
                    || IsLive(task.Status) && (task.RunId is null || task.StartedAt is null);
                if (needsDetail)
                {
                    if (++detailCount > 128) { complete = false; task = task with { ReadComplete = false }; }
                    else
                    {
                        try
                        {
                            using var detail = await _get(new(origin, "/api/orchestration/threads/" + Uri.EscapeDataString(id)), token, cancellationToken).ConfigureAwait(false);
                            task = DetailTask(task, detail.RootElement, hasShell, shell.RootElement);
                            if (detail.RootElement.TryGetProperty("snapshotSequence", out var detailSequence))
                                sequence = Math.Max(sequence, detailSequence.GetInt64());
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { complete = false; task = task with { ReadComplete = false, RequestsComplete = false }; }
                    }
                }
                if (!task.ReadComplete) complete = false;
                tasks.Add(task);
            }
            var current = T3Reader.ReadBoundedFile(runtimePath);
            if (!runtimeRaw.AsSpan().SequenceEqual(current)) throw new T3TransportException("RuntimeChanged", "The local server restarted during the read.");
            result = new() { Success = true, EnvironmentId = expectedId, SourceEpoch = Convert.ToHexString(SHA256.HashData(runtimeRaw)),
                Sequence = sequence, CapturedAt = _clock.GetUtcNow(), CatalogObservedAt = catalogObservedAt,
                IsComplete = complete, Tasks = tasks.ToArray(), TransportKind = "local" };
        }
        finally
        {
            try
            {
                if (string.IsNullOrEmpty(sessionId)) throw new InvalidDataException();
                using var revokeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await _cli(home, ["auth", "session", "revoke", sessionId], revokeDeadline.Token).ConfigureAwait(false);
            }
            catch { throw new T3TransportException("RevokeFailed", "The temporary read grant could not be revoked; it expires within five minutes."); }
        }
        return result;
    }

    internal static T3TaskMetadata ShellTask(string id, JsonElement row, JsonElement shell)
    {
        var projectId = Text(row, "projectId");
        var project = shell.TryGetProperty("projects", out var projects) ? projects.EnumerateArray().FirstOrDefault(project => Text(project, "id") == projectId) : default;
        var selection = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("modelSelection", out var model) ? model : default;
        return new() { Id = id, RunId = Text(row, "activeRunId") ?? Text(row, "latestRunId"),
            Status = Text(row, "activityRunStatus") ?? Text(row, "status") ?? "unknown",
            StartedAt = Time(row, "activityRunStartedAt") ?? Time(row, "latestRunStartedAt"), CompletedAt = Time(row, "latestRunCompletedAt"),
            LatestUserAuthoredMessageAt = Time(row, "latestUserAuthoredMessageAt"), ReadComplete = Text(row, "status") is not null,
            Title = Label(Text(row, "title")), Project = Label(Text(project, "title")), Provider = Label(Text(row, "providerInstanceId")), Model = Label(Text(selection, "model")) };
    }

    internal static T3TaskMetadata DetailTask(T3TaskMetadata task, JsonElement detail, bool hasShell, JsonElement shell = default)
    {
        var projection = detail.GetProperty("projection");
        var native = projection.GetProperty("thread");
        if (RequiredId(native, "id") != task.Id) throw new InvalidDataException();
        var nodes = projection.GetProperty("nodes"); var runs = projection.GetProperty("runs"); var requests = projection.GetProperty("runtimeRequests");
        if (nodes.GetArrayLength() > 10000 || runs.GetArrayLength() > 10000 || requests.GetArrayLength() > 128) throw new InvalidDataException();
        var nodeRuns = nodes.EnumerateArray().ToDictionary(node => RequiredId(node, "id"), node => Text(node, "runId"), StringComparer.Ordinal);
        var requestsComplete = !detail.TryGetProperty("hasMoreHistory", out var more) || more.ValueKind != JsonValueKind.True;
        var pending = new List<T3PendingRequest>(); var resolved = new List<T3PendingRequest>();
        foreach (var request in requests.EnumerateArray())
        {
            var status = Text(request, "status");
            if (status is not ("pending" or "resolved")) continue;
            if (!nodeRuns.TryGetValue(Text(request, "nodeId") ?? "", out var runId) || runId is null) { requestsComplete = false; continue; }
            var blocking = !request.TryGetProperty("responseCapability", out var capability) || Text(capability, "type") != "message";
            var projected = new T3PendingRequest { Id = RequiredId(request, "id"), RunId = runId, CreatedAt = Time(request, "createdAt"), Blocking = blocking };
            if (status == "pending") pending.Add(projected); else if (!blocking) resolved.Add(projected);
        }
        var selected = runs.EnumerateArray().FirstOrDefault(run => Text(run, "id") == task.RunId && task.RunId is not null);
        if (task.RunId is null && IsLive(task.Status))
        {
            var active = runs.EnumerateArray().Where(run => IsLive(Text(run, "status") ?? "unknown")).ToArray();
            if (active.Length == 1) selected = active[0];
        }
        if (!hasShell) selected = runs.EnumerateArray().OrderByDescending(run => run.TryGetProperty("ordinal", out var ordinal) ? ordinal.GetInt64() : 0).FirstOrDefault();
        if (selected.ValueKind == JsonValueKind.Object)
            task = task with { RunId = RequiredId(selected, "id"), Status = Text(selected, "status") ?? "unknown",
                StartedAt = Time(selected, "startedAt") ?? Time(selected, "requestedAt") ?? task.StartedAt,
                CompletedAt = Time(selected, "completedAt") ?? task.CompletedAt };
        var proof = !IsLive(task.Status) || task.RunId is not null && task.StartedAt is not null;
        var project = shell.ValueKind == JsonValueKind.Object && shell.TryGetProperty("projects", out var projects)
            ? projects.EnumerateArray().FirstOrDefault(project => Text(project, "id") == Text(native, "projectId")) : default;
        return task with { ReadComplete = proof, RequestsComplete = requestsComplete && proof, PendingRequests = pending.ToArray(), ResolvedQuestions = resolved.ToArray(),
            Title = task.Title ?? Label(Text(native, "title")), Project = task.Project ?? Label(Text(project, "title")) };
    }

    private static async Task<JsonDocument> GetAsync(Uri uri, string? token, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("x-t3-orchestration-protocol", "2");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400) throw new T3TransportException("RedirectRejected", "Redirects are not permitted.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(await ReadBoundedAsync(stream, T3SshTransport.MaximumOutputBytes, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<string> RunCliAsync(string home, string[] arguments, CancellationToken cancellationToken)
    {
        var executable = InstalledT3ForegroundResolver.FindInstalledExecutable(out var failureClass)
            ?? throw new T3TransportException(failureClass ?? "LocalCliUnavailable", "The installed T3 executable could not be verified.");
        var entry = Path.Combine(Path.GetDirectoryName(executable)!, "resources", "server.asar", "apps", "server", "dist", "bin.mjs");
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "resources", "server.asar"))) throw new InvalidDataException();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        start.Environment["T3CODE_DESKTOP_CLI_LAUNCHER"] = "1";
        start.Environment["T3CODE_HOME"] = home;
        start.ArgumentList.Add(entry);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--base-dir"); start.ArgumentList.Add(home);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException();
            using var registration = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, 64 * 1024, deadline.Token);
            var errors = ReadBoundedAsync(process.StandardError.BaseStream, 64 * 1024, deadline.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new T3TransportException("CliFailed", "The T3 CLI operation failed; private output was withheld.", process.ExitCode);
            return Encoding.UTF8.GetString(await output.ConfigureAwait(false));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { if (process.Id > 0) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(); var buffer = new byte[16384];
        while (true) { var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); if (read == 0) break;
            if (output.Length + read > limit) throw new T3TransportException("PayloadLimit", "The metadata response exceeds its limit."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    private static bool IsLive(string status) => status is "queued" or "preparing" or "starting" or "running" or "waiting";
    internal static bool IsSupportedLoopbackOrigin(Uri origin) => origin.Scheme == "http" && origin.IsLoopback
        && string.IsNullOrEmpty(origin.UserInfo) && origin.AbsolutePath == "/"
        && string.IsNullOrEmpty(origin.Query) && string.IsNullOrEmpty(origin.Fragment);
    private static bool IsPresent(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    private static string? Text(JsonElement row, string name) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredId(JsonElement row, string name) => Text(row, name) is { Length: > 0 and <= 512 } value && !value.Any(char.IsControl) ? value : throw new InvalidDataException();
    private static DateTimeOffset? Time(JsonElement row, string name) => DateTimeOffset.TryParse(Text(row, name), out var value) && value > DateTimeOffset.UnixEpoch ? value : null;
    private static string? Label(string? text) => text is null ? null : new string(text.Where(c => !char.IsControl(c)).Take(256).ToArray());
}
