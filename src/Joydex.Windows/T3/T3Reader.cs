using System.Reflection;
using System.Text;
using System.Text.Json;
using Joydex.Core.T3;

namespace Joydex.Windows.T3;

public sealed partial class T3Reader(IT3SshTransport? transport = null)
{
    private const string UnknownContext = "Active Electron environment and keymap are not verified.";
    private static readonly HashSet<string> ShortcutCommands = new(StringComparer.Ordinal) { "chat.new", "thread.previous", "thread.next", "thread.stop" };
    private static readonly HashSet<string> TaskStatuses = new(StringComparer.Ordinal)
        { "preparing", "queued", "starting", "running", "waiting", "completed", "failed", "interrupted", "cancelled", "rolled_back", "idle", "unknown" };
    private readonly IT3SshTransport _transport = transport ?? new T3SshTransport();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Task<T3ReadResult> ReadAsync(T3ConnectionOptions options, IReadOnlyCollection<string> knownThreadIds, CancellationToken cancellationToken = default)
        => ReadCoreAsync(options, knownThreadIds, "read", cancellationToken);

    public async Task<T3ReadResult> TestConnectionAsync(T3ConnectionOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.FollowDesktop) return await ReadCoreAsync(options, [], "test", cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try { return await TestDesktopConnectionAsync(options, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return SanitizeFailure("Timeout"); }
    }

    private async Task<T3ReadResult> TestDesktopConnectionAsync(T3ConnectionOptions options, CancellationToken cancellationToken)
    {
        var discovery = await DiscoverDesktopAsync(cancellationToken).ConfigureAwait(false);
        if (!discovery.Success)
            return Failure("DiscoveryFailed", discovery.Error ?? "Desktop environment discovery is incomplete.");
        var candidates = DesktopTestCandidates(discovery, options);
        if (candidates.Length == 0) return Failure("UnsupportedRoute", "No enabled desktop environment has a supported independent metadata route.");
        T3ReadResult? failure = null;
        foreach (var candidate in candidates)
        {
            var result = await ReadDesktopEnvironmentAsync(candidate, [], cancellationToken).ConfigureAwait(false);
            if (result.Success) return result;
            failure = result;
        }
        return failure ?? Failure("ReadFailed", "No enabled desktop environment could be verified.");
    }

    internal static T3DesktopEnvironment[] DesktopTestCandidates(T3DesktopDiscoveryResult discovery, T3ConnectionOptions options) =>
        discovery.Environments.Where(environment => environment.Enabled && ValidId(environment.EnvironmentId))
            .Select(environment => environment.UnsupportedReason is not null && !environment.IsLocal
                && environment.EnvironmentId == options.EnvironmentId && T3SshTransport.IsValidHost(options.SshHost)
                ? environment with { SshHost = options.SshHost, UnsupportedReason = null, TransportKind = "ssh" } : environment)
            .Where(environment => environment.UnsupportedReason is null)
            .OrderByDescending(environment => environment.IsLocal).ToArray();

    private async Task<T3ReadResult> ReadCoreAsync(T3ConnectionOptions options, IReadOnlyCollection<string> knownThreadIds, string mode, CancellationToken cancellationToken)
    {
        options = options.Normalize();
        if (!T3SshTransport.IsValidHost(options.SshHost)) return Failure("InvalidHost", "Choose a valid current-user SSH configuration alias.");
        if (mode == "read" && string.IsNullOrEmpty(options.EnvironmentId)) return Failure("EnvironmentRequired", "Test the connection and save the discovered identity first.");
        if (knownThreadIds.Count > 128 || knownThreadIds.Any(id => string.IsNullOrEmpty(id) || id.Length > 512 || id.Any(char.IsControl)))
            return Failure("InputLimit", "The tracked thread list exceeds its supported bounds.");
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Failure("Busy", "A T3 operation is already in progress.");
        try
        {
            var raw = await _transport.ExecuteAsync(options.SshHost, BuildScript(mode, options.EnvironmentId, knownThreadIds), cancellationToken).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(raw) > T3SshTransport.MaximumOutputBytes) return Failure("PayloadLimit", "The metadata response exceeds its size limit.");
            var result = JsonSerializer.Deserialize<T3ReadResult>(raw, JsonOptions);
            if (result is null) return Failure("InvalidMetadata", "The environment returned no metadata.");
            if (!result.Success) return SanitizeFailure(result.ErrorClass);
            if (!string.IsNullOrEmpty(options.EnvironmentId) && options.EnvironmentId != result.EnvironmentId)
                return Failure("EnvironmentMismatch", "The discovered environment does not match the saved identity.");
            if (!ValidId(result.EnvironmentId) || !ValidHash(result.SourceEpoch) || result.Sequence < 0
                || result.CapturedAt <= DateTimeOffset.UnixEpoch || !ValidCatalogObservation(result) || result.Tasks is null || result.Tasks.Length > 1128
                || result.Tasks.Any(t => !ValidTask(t)))
                return Failure("InvalidMetadata", "The environment returned incomplete or invalid metadata.");
            return result with
            {
                Reason = null, ErrorClass = null, DisplayName = DisplayLabel(options.DisplayName),
                CliExitCode = null,
                BindingFingerprint = ValidHash(result.BindingFingerprint) ? result.BindingFingerprint : null,
                ContextDiagnostic = UnknownContext,
                Tasks = result.Tasks.Select(task => task with
                {
                    Status = task.Status is { } status && TaskStatuses.Contains(status) ? status : "unknown",
                    Title = DisplayLabel(task.Title), Project = DisplayLabel(task.Project),
                    Provider = DisplayLabel(task.Provider), Model = DisplayLabel(task.Model),
                }).ToArray(),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (T3TransportException error) { return SanitizeFailure(error.ErrorClass, error.ExitCode); }
        catch { return Failure("InvalidMetadata", "The environment read failed; private details were withheld."); }
        finally { _operation.Release(); }
    }

    public async Task<T3ShortcutSetupResult> SetupShortcutsAsync(T3ConnectionOptions options, bool remove = false, CancellationToken cancellationToken = default)
    {
        if (options.FollowDesktop)
            return new() { ErrorClass = "FollowDesktopReadOnly", Reason = "Desktop-following monitoring never changes environment shortcuts. Use an explicitly selected manual environment." };
        options = options.Normalize();
        if (!T3SshTransport.IsValidHost(options.SshHost) || string.IsNullOrEmpty(options.EnvironmentId))
            return new() { ErrorClass = "EnvironmentRequired", Reason = "Test the connection and save its identity before configuring shortcuts." };
        if (!await _operation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new() { ErrorClass = "Busy", Reason = "A T3 operation is already in progress." };
        try
        {
            var raw = await _transport.ExecuteAsync(options.SshHost, BuildScript(remove ? "remove" : "setup", options.EnvironmentId, []), cancellationToken).ConfigureAwait(false);
            if (Encoding.UTF8.GetByteCount(raw) > T3SshTransport.MaximumOutputBytes) throw new JsonException();
            var result = JsonSerializer.Deserialize<T3ShortcutSetupResult>(raw, JsonOptions);
            if (result is not null && !ValidSetupResult(result))
                return new() { ErrorClass = "InvalidMetadata", Reason = "The shortcut operation returned unverified metadata; check the environment's keybinding settings." };
            if (result is null || !result.Success)
            {
                var failure = SanitizeFailure(result?.ErrorClass);
                return new()
                {
                    ErrorClass = failure.ErrorClass, Reason = failure.Reason,
                    BindingFingerprint = ValidHash(result?.BindingFingerprint) ? result!.BindingFingerprint : null,
                    Rules = result?.Rules ?? [],
                    Diagnostics = result?.ErrorClass == "UnsupportedShortcuts"
                        ? ["Read-only monitoring remains available. Shortcut setup requires a compatible server.getConfig response."] : [],
                };
            }
            return result with
            {
                ErrorClass = null, Reason = null, ContextDiagnostic = UnknownContext,
                Diagnostics = result.Diagnostics.Select(DisplayLabel).Where(value => value is not null).Select(value => value!).ToArray(),
                CapabilitySource = result.CapabilitySource is "T3 server.getConfig effective keybindings read with orchestration:read" or "Private exact-addition manifest"
                    ? result.CapabilitySource : null,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (T3TransportException error)
        {
            var failure = SanitizeFailure(error.ErrorClass);
            return new() { ErrorClass = failure.ErrorClass, Reason = failure.Reason };
        }
        catch { return new() { ErrorClass = "SetupFailed", Reason = "Shortcut configuration failed; private details were withheld." }; }
        finally { _operation.Release(); }
    }

    private static bool ValidTask(T3TaskMetadata task) => task is not null && ValidId(task.Id) && (task.RunId is null || ValidId(task.RunId))
        && task.PendingRequests is { Length: <= 128 } && task.ResolvedQuestions is { Length: <= 128 }
        && task.PendingRequests.Concat(task.ResolvedQuestions).All(r => r is not null && ValidId(r.Id) && (r.RunId is null || ValidId(r.RunId)))
        && new[] { task.StartedAt, task.CompletedAt, task.LatestUserAuthoredMessageAt }.Concat(task.PendingRequests.Concat(task.ResolvedQuestions).Select(r => r.CreatedAt))
            .All(t => t is null || t > DateTimeOffset.UnixEpoch);

    private static bool ValidCatalogObservation(T3ReadResult result) => result.CatalogObservedAt is not { } observed
        || observed > DateTimeOffset.UnixEpoch && observed <= result.CapturedAt;

    private static bool ValidSetupResult(T3ShortcutSetupResult result) =>
        (result.BindingFingerprint is null || ValidHash(result.BindingFingerprint))
        && result.Rules is { Length: <= 128 } && result.Rules.All(rule => rule is not null && ShortcutCommands.Contains(rule.Command)
            && !string.IsNullOrWhiteSpace(rule.Key) && rule.Key.Length <= 128 && !rule.Key.Any(char.IsControl)
            && (rule.When is null || rule.When.Length <= 256 && !rule.When.Any(char.IsControl)))
        && result.Diagnostics is { Length: <= 128 }
        && result.AddedCommands is { Length: <= 4 } && result.AddedCommands.All(ShortcutCommands.Contains) && result.AddedCommands.Distinct().Count() == result.AddedCommands.Length
        && result.RemovedCommands is { Length: <= 4 } && result.RemovedCommands.All(ShortcutCommands.Contains) && result.RemovedCommands.Distinct().Count() == result.RemovedCommands.Length;

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string? DisplayLabel(string? value)
    {
        if (value is null) return null;
        var result = string.Join(" ", new string(value.Where(character => !char.IsControl(character) || char.IsWhiteSpace(character)).ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return result.Length == 0 ? null : result[..Math.Min(result.Length, 256)];
    }

    private static bool ValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)) return false;
        try { new UTF8Encoding(false, true).GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    private static T3ReadResult Failure(string kind, string reason) => new() { ErrorClass = kind, Reason = reason };
    private static T3ReadResult SanitizeFailure(string? kind, int? exitCode = null) =>
        Joydex.Windows.Actions.InstalledT3ForegroundResolver.TryParseMissingRegistrationDiagnostic(kind, out var userStatus, out var classesStatus)
            ? Failure($"LocalCliRegistrationMissing:hkcu={userStatus}:hkcr={classesStatus}",
                $"The local T3 registration lookup failed (HKCU status {userStatus}; HKCR status {classesStatus}); private registration values were withheld.")
            : kind switch
    {
        "EnvironmentMismatch" => Failure(kind, "The discovered environment does not match the saved identity."),
        "EnvironmentRequired" => Failure(kind, "Test the connection and save the discovered identity first."),
        "Timeout" or "ReadTimeout" or "CliTimeout" => Failure("Timeout", "The environment operation timed out."),
        "PayloadLimit" => Failure(kind, "The environment response exceeds its size limit."),
        "RevokeFailed" => Failure(kind, "The temporary read grant could not be revoked; it expires within five minutes."),
        "BindingConflict" => Failure(kind, "No conflict-free dedicated shortcut is available."),
        "UnsupportedShortcuts" => Failure(kind, "The server's effective keybindings could not be verified; no shortcuts were changed."),
        "BindingsChanged" => Failure(kind, "The keybinding file changed during setup; retry after reviewing it."),
        "LoopbackRequired" or "RedirectRejected" => Failure(kind, "The environment must expose its own loopback HTTP API without redirects."),
        "UnexpectedScopes" => Failure(kind, "The temporary grant did not have exactly read-only permissions."),
        "CliFailed" => Failure(kind, exitCode is { } code
            ? $"The local T3 CLI operation exited with code {code}; private process output was withheld."
            : "The local T3 CLI operation failed; private process output was withheld.") with { CliExitCode = exitCode },
        "LocalCliUnavailable" => Failure(kind, "The installed local T3 executable could not be verified for the metadata reader."),
        "LocalCliRegistrationMissing" => Failure(kind, "No local T3 protocol executable registration was found."),
        "LocalCliRegistrationInvalid" => Failure(kind, "The local T3 protocol command did not match the supported executable and URI argument shape."),
        "LocalCliExecutableMissing" => Failure(kind, "The registered local T3 executable is missing."),
        "LocalCliProductMismatch" => Failure(kind, "The registered executable did not identify itself as a supported T3 Desktop product."),
        "LocalCliVerificationFailed" => Failure(kind, "The local T3 installation metadata could not be read or verified; private details were withheld."),
        "RuntimeChanged" => Failure(kind, "The T3 environment restarted or changed runtime identity during the read; retrying is safe."),
        "Busy" => Failure(kind, "A T3 operation is already in progress."),
        "SshFailed" or "SshUnavailable" => Failure(kind, "The SSH reader failed. Check the saved alias and noninteractive current-user authentication."),
        _ => Failure("ReadFailed", "The environment operation failed; private details were withheld."),
    };

    private static string BuildScript(string mode, string environmentId, IReadOnlyCollection<string> ids)
    {
        var assembly = typeof(T3Reader).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".T3.t3-projector.py", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var configuration = JsonSerializer.Serialize(new { mode, environmentId, knownThreadIds = ids }, JsonOptions);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(configuration));
        return "import base64, json\nJOYDEX_OPTIONS = json.loads(base64.b64decode('" + encoded + "'))\n" + reader.ReadToEnd();
    }
}
