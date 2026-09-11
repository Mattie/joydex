using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Joydex.Windows.Voice;

public sealed record CodexLocalProjectRoot(
    string ProjectId,
    string ProjectName,
    string RootPath);

public sealed record CodexProjectCatalog(
    IReadOnlyList<CodexLocalProjectRoot> Roots,
    string? Warning = null);

public sealed record CodexVoiceWorkspaceProvisioningRequest(
    string WorkspacePath,
    string ProjectId = "",
    string ProjectLabel = "",
    bool RegisterProject = false,
    string TaskName = "Joydex Voice Chat — Owned");

public sealed record CodexVoiceWorkspaceProvisioningResult(
    string WorkspacePath,
    string ProjectId,
    string ProjectLabel,
    string TaskId,
    string? Warning = null);

/// <summary>
/// Lists local Codex project roots and provisions a fresh Dedicated Voice Task in one workspace.
/// The filesystem working directory is authoritative; project identity is optional UI metadata.
/// </summary>
public sealed class CodexVoiceWorkspaceService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly Func<string?, CancellationToken, Task<ICodexAppServerClient>> _createClient;
    private readonly Action<string>? _log;

    public CodexVoiceWorkspaceService(string? appServerPath, Action<string>? log = null)
    {
        _log = log;
        _createClient = async (workingDirectory, cancellationToken) =>
        {
            var binary = await CodexAppServerRuntimeResolver
                .ResolveAsync(appServerPath, cancellationToken)
                .ConfigureAwait(false);
            log?.Invoke(
                $"Room Voice selected {(binary.IsManagedRuntime ? "automatic" : "override")} "
                + $"Codex App Server runtime '{binary.ExecutablePath}'.");
            return new CodexAppServerClient(binary, workingDirectory, log);
        };
    }

    internal CodexVoiceWorkspaceService(
        Func<string?, CancellationToken, Task<ICodexAppServerClient>> createClient,
        Action<string>? log = null)
    {
        _createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
        _log = log;
    }

    public async Task<CodexProjectCatalog> ListProjectRootsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var client = await _createClient(null, cancellationToken).ConfigureAwait(false);
        await client.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new CodexProjectCatalog(await ReadAllProjectRootsAsync(client, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (IsProjectApiFailure(exception))
        {
            var warning = "Codex project selection is unavailable; a custom working folder can still be used.";
            _log?.Invoke($"{warning} error={exception.Message}");
            return new CodexProjectCatalog([], warning);
        }
    }

    public async Task<CodexVoiceWorkspaceProvisioningResult> ProvisionAsync(
        CodexVoiceWorkspaceProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workspacePath = NormalizeAbsolutePath(request.WorkspacePath);
        Directory.CreateDirectory(workspacePath);

        await using var client = await _createClient(workspacePath, cancellationToken).ConfigureAwait(false);
        await client.StartAsync(cancellationToken).ConfigureAwait(false);

        var projectId = request.ProjectId.Trim();
        var projectLabel = request.ProjectLabel.Trim();
        string? warning = null;
        if (request.RegisterProject && projectId.Length == 0)
        {
            try
            {
                var roots = await ReadAllProjectRootsAsync(client, cancellationToken).ConfigureAwait(false);
                var existing = roots.FirstOrDefault(root => PathsEqual(root.RootPath, workspacePath));
                if (existing is not null)
                {
                    projectId = existing.ProjectId;
                    projectLabel = existing.ProjectName;
                }
                else
                {
                    var created = await CreateProjectAsync(
                            client,
                            workspacePath,
                            projectLabel.Length == 0 ? "Joydex Voice" : projectLabel,
                            cancellationToken)
                        .ConfigureAwait(false);
                    projectId = created.ProjectId;
                    projectLabel = created.ProjectName;
                }
            }
            catch (Exception exception) when (IsProjectApiFailure(exception))
            {
                warning = "Codex project registration failed; Joydex provisioned a folder-only workspace.";
                _log?.Invoke($"{warning} error={exception.Message}");
                projectId = string.Empty;
                projectLabel = string.Empty;
            }
        }

        var startResult = await client.RequestAsync(
                "thread/start",
                new
                {
                    cwd = workspacePath,
                    projectId = projectId.Length == 0 ? null : projectId,
                    runtimeWorkspaceRoots = new[] { workspacePath },
                    approvalPolicy = "never",
                    sandbox = "danger-full-access",
                    ephemeral = false,
                },
                RequestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var (taskId, returnedWorkspace) = ReadThreadIdentity(startResult, "thread/start");
        if (!PathsEqual(workspacePath, returnedWorkspace))
        {
            throw new InvalidOperationException(
                $"Codex created the Dedicated Voice Task in '{returnedWorkspace}' instead of configured workspace '{workspacePath}'.");
        }

        try
        {
            await client.RequestAsync(
                    "thread/name/set",
                    new
                    {
                        threadId = taskId,
                        name = request.TaskName.Trim(),
                    },
                    RequestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CodexAppServerRpcException exception)
        {
            warning = AppendWarning(warning, "The task was created, but Codex did not accept its display name.");
            _log?.Invoke($"Could not name new Dedicated Voice Task {taskId}; error={exception.Message}");
        }

        return new CodexVoiceWorkspaceProvisioningResult(
            workspacePath,
            projectId,
            projectLabel,
            taskId,
            warning);
    }

    public static bool PathsEqual(string left, string right) => string.Equals(
        NormalizeAbsolutePath(left),
        NormalizeAbsolutePath(right),
        StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<CodexLocalProjectRoot>> ReadAllProjectRootsAsync(
        ICodexAppServerClient client,
        CancellationToken cancellationToken)
    {
        var roots = new List<CodexLocalProjectRoot>();
        string? cursor = null;
        do
        {
            var result = await client.RequestAsync(
                    "project/list",
                    new { cursor, limit = 100 },
                    RequestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("data", out var projects)
                || projects.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("project/list returned no project collection.");
            }

            foreach (var project in projects.EnumerateArray())
            {
                if (!TryReadString(project, "id", out var projectId)
                    || !TryReadString(project, "name", out var projectName)
                    || !project.TryGetProperty("roots", out var projectRoots)
                    || projectRoots.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var root in projectRoots.EnumerateArray())
                {
                    if (TryReadString(root, "path", out var rootPath)
                        && Path.IsPathFullyQualified(rootPath))
                    {
                        roots.Add(new CodexLocalProjectRoot(
                            projectId,
                            projectName,
                            NormalizeAbsolutePath(rootPath)));
                    }
                }
            }

            cursor = TryReadString(result, "nextCursor", out var nextCursor)
                ? nextCursor
                : null;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return roots
            .DistinctBy(root => (root.ProjectId, root.RootPath), ProjectRootComparer.Instance)
            .OrderBy(root => root.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(root => root.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<CodexLocalProjectRoot> CreateProjectAsync(
        ICodexAppServerClient client,
        string workspacePath,
        string projectLabel,
        CancellationToken cancellationToken)
    {
        var result = await client.RequestAsync(
                "project/create",
                new
                {
                    name = projectLabel,
                    roots = new[] { new { path = workspacePath } },
                    idempotencyKey = CreateProjectIdempotencyKey(workspacePath),
                    metadata = new Dictionary<string, string>
                    {
                        ["owner"] = "joydex-room-voice",
                    },
                },
                RequestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("project", out var project)
            || !TryReadString(project, "id", out var projectId)
            || !TryReadString(project, "name", out var returnedLabel))
        {
            throw new InvalidOperationException("project/create returned no project identity.");
        }

        return new CodexLocalProjectRoot(projectId, returnedLabel, workspacePath);
    }

    private static (string TaskId, string WorkspacePath) ReadThreadIdentity(JsonElement result, string method)
    {
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("thread", out var thread)
            && TryReadString(thread, "id", out var taskId)
            && TryReadString(thread, "cwd", out var workspacePath)
            && Guid.TryParseExact(taskId, "D", out _)
            && Path.IsPathFullyQualified(workspacePath))
        {
            return (taskId, NormalizeAbsolutePath(workspacePath));
        }

        throw new InvalidOperationException($"{method} returned no valid task id and working directory.");
    }

    private static string CreateProjectIdempotencyKey(string workspacePath)
    {
        var normalized = NormalizeAbsolutePath(workspacePath).ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"joydex-voice-{Convert.ToHexString(digest)[..24].ToLowerInvariant()}";
    }

    private static string NormalizeAbsolutePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(value.Trim()))
        {
            throw new ArgumentException("The Voice Agent Workspace path must be fully qualified.", nameof(value));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { Length: > 0 } text)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static string AppendWarning(string? existing, string next) =>
        string.IsNullOrWhiteSpace(existing) ? next : $"{existing} {next}";

    private static bool IsProjectApiFailure(Exception exception) => exception is
        CodexAppServerRpcException
        or InvalidOperationException
        or JsonException;

    private sealed class ProjectRootComparer : IEqualityComparer<(string ProjectId, string RootPath)>
    {
        public static ProjectRootComparer Instance { get; } = new();

        public bool Equals(
            (string ProjectId, string RootPath) left,
            (string ProjectId, string RootPath) right) =>
            string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal)
            && string.Equals(left.RootPath, right.RootPath, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ProjectId, string RootPath) value) => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(value.ProjectId),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.RootPath));
    }
}
