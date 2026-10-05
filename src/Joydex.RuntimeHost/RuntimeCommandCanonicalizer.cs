using Joydex.Contracts;
using Joydex.Core.Voice;

namespace Joydex.RuntimeHost;

internal static class RuntimeCommandCanonicalizer
{
    public static bool TryNormalize(
        RuntimeCommandRequest request,
        out RuntimeCommandRequest normalized,
        out string? error)
    {
        normalized = request;
        error = null;
        try
        {
            var arguments = NormalizeArguments(request.Arguments);
            normalized = request with { Arguments = arguments };
            error = ValidateShape(normalized.Kind, arguments);
            return error is null;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidDataException or
            NotSupportedException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static RuntimeCommandArguments? NormalizeArguments(RuntimeCommandArguments? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var aggregates = arguments.ExternalSettingsAggregates?
            .Distinct()
            .Order()
            .ToArray();
        var task = NormalizeTask(arguments.Task);
        var workspace = NormalizeWorkspace(arguments.VoiceWorkspace);
        var appServerPath = NormalizeOptionalPath(arguments.CodexAppServerPath);
        var deliveryId = NormalizeOptional(arguments.DeliveryId);
        var endpoint = NormalizeEndpoint(arguments.VoiceEndpoint);
        var continuationToken = NormalizeOptional(arguments.ContinuationToken);
        var pluginId = NormalizePluginId(arguments.PluginId);
        if (continuationToken?.Length > 256)
        {
            throw new ArgumentException("The continuation token is too long.");
        }
        if (arguments.VoiceWakeTuning is { } tuning && tuning.Validate().Count > 0)
        {
            throw new InvalidDataException(string.Join(" ", tuning.Validate()));
        }

        var normalized = new RuntimeCommandArguments(
            aggregates,
            arguments.Enabled,
            task,
            deliveryId,
            workspace,
            appServerPath,
            endpoint,
            arguments.VoiceWakeTuning,
            continuationToken,
            pluginId);
        return HasAny(normalized) ? normalized : null;
    }

    private static string? ValidateShape(
        RuntimeCommandKind kind,
        RuntimeCommandArguments? arguments)
    {
        bool Only(params string[] allowed)
        {
            var names = allowed.ToHashSet(StringComparer.Ordinal);
            return arguments is null
                   || ((!HasAggregates(arguments) || names.Contains(nameof(arguments.ExternalSettingsAggregates)))
                       && (arguments.Enabled is null || names.Contains(nameof(arguments.Enabled)))
                       && (arguments.Task is null || names.Contains(nameof(arguments.Task)))
                       && (arguments.DeliveryId is null || names.Contains(nameof(arguments.DeliveryId)))
                       && (arguments.VoiceWorkspace is null || names.Contains(nameof(arguments.VoiceWorkspace)))
                       && (arguments.CodexAppServerPath is null || names.Contains(nameof(arguments.CodexAppServerPath)))
                       && (arguments.VoiceEndpoint is null || names.Contains(nameof(arguments.VoiceEndpoint)))
                       && (arguments.VoiceWakeTuning is null || names.Contains(nameof(arguments.VoiceWakeTuning)))
                       && (arguments.ContinuationToken is null || names.Contains(nameof(arguments.ContinuationToken)))
                       && (arguments.PluginId is null || names.Contains(nameof(arguments.PluginId))));
        }

        var valid = kind switch
        {
            RuntimeCommandKind.AdoptExternalSettings =>
                HasAggregates(arguments) && Only(nameof(arguments.ExternalSettingsAggregates)),
            RuntimeCommandKind.ListVoiceProjects => Only(nameof(arguments.CodexAppServerPath)),
            RuntimeCommandKind.ProvisionVoiceWorkspace =>
                arguments?.VoiceWorkspace is not null
                && Only(nameof(arguments.VoiceWorkspace), nameof(arguments.CodexAppServerPath)),
            RuntimeCommandKind.ListDesktopTasks =>
                (arguments?.Task is null
                    || string.Equals(arguments.Task.HostId, "local", StringComparison.OrdinalIgnoreCase))
                && Only(nameof(arguments.Task)),
            RuntimeCommandKind.NavigateToDesktopTask =>
                arguments?.Task is not null && Only(nameof(arguments.Task)),
            RuntimeCommandKind.ReadVoiceWakeTuning =>
                arguments?.VoiceEndpoint is not null && Only(nameof(arguments.VoiceEndpoint)),
            RuntimeCommandKind.WriteVoiceWakeTuning =>
                arguments?.VoiceEndpoint is not null
                && arguments.VoiceWakeTuning is not null
                && Only(nameof(arguments.VoiceEndpoint), nameof(arguments.VoiceWakeTuning)),
            RuntimeCommandKind.ReadVoiceConversationPage =>
                Only(nameof(arguments.ContinuationToken)),
            RuntimeCommandKind.ReadVoiceOutboxDelivery or
                RuntimeCommandKind.RetryVoiceOutboxDelivery or
                RuntimeCommandKind.DiscardVoiceOutboxDelivery =>
                arguments?.DeliveryId is not null && Only(nameof(arguments.DeliveryId)),
            RuntimeCommandKind.RetargetVoiceOutboxDelivery =>
                arguments?.DeliveryId is not null
                && arguments.Task is not null
                && Only(nameof(arguments.DeliveryId), nameof(arguments.Task)),
            RuntimeCommandKind.SetJoydexStartAtLogin or
                RuntimeCommandKind.SetLinkToolStartAtLogin =>
                arguments?.Enabled is not null && Only(nameof(arguments.Enabled)),
            RuntimeCommandKind.RestartPlugin or
                RuntimeCommandKind.ReloadPluginConfiguration =>
                arguments?.PluginId is not null && Only(nameof(arguments.PluginId)),
            _ => arguments is null,
        };
        return valid ? null : $"The {kind} command arguments are missing or contain unused fields.";
    }

    private static RuntimeTaskReference? NormalizeTask(RuntimeTaskReference? task)
    {
        if (task is null)
        {
            return null;
        }
        if (!CodexTaskReference.TryParse(task.TaskId, out var taskId))
        {
            throw new ArgumentException("A valid Codex task ID is required.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(task.HostId);
        return new RuntimeTaskReference(taskId, task.HostId.Trim());
    }

    private static RuntimeVoiceWorkspaceRequest? NormalizeWorkspace(
        RuntimeVoiceWorkspaceRequest? workspace)
    {
        if (workspace is null)
        {
            return null;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.WorkspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.TaskName);
        if (!Path.IsPathFullyQualified(workspace.WorkspacePath.Trim()))
        {
            throw new ArgumentException("The Voice Agent Workspace path must be fully qualified.");
        }
        return workspace with
        {
            WorkspacePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.WorkspacePath.Trim())),
            ProjectId = workspace.ProjectId.Trim(),
            ProjectLabel = workspace.ProjectLabel.Trim(),
            TaskName = workspace.TaskName.Trim(),
        };
    }

    private static string? NormalizeOptionalPath(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
        {
            return null;
        }
        if (!Path.IsPathFullyQualified(normalized))
        {
            throw new ArgumentException("The Codex App Server path must be fully qualified.");
        }
        return Path.GetFullPath(normalized);
    }

    private static Uri? NormalizeEndpoint(Uri? endpoint)
    {
        if (endpoint is null)
        {
            return null;
        }
        if (!endpoint.IsAbsoluteUri
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The Voice PE endpoint must be an absolute HTTP URL.");
        }
        return new Uri(endpoint.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizePluginId(string? value)
    {
        if (value is null)
        {
            return null;
        }
        if (!RuntimePluginLimits.IsCanonicalPluginId(value))
        {
            throw new ArgumentException("A canonical plugin ID is required.");
        }
        return value;
    }

    private static bool HasAggregates(RuntimeCommandArguments? arguments) =>
        arguments?.ExternalSettingsAggregates is { Length: > 0 };

    private static bool HasAny(RuntimeCommandArguments arguments) =>
        HasAggregates(arguments)
        || arguments.Enabled is not null
        || arguments.Task is not null
        || arguments.DeliveryId is not null
        || arguments.VoiceWorkspace is not null
        || arguments.CodexAppServerPath is not null
        || arguments.VoiceEndpoint is not null
        || arguments.VoiceWakeTuning is not null
        || arguments.ContinuationToken is not null
        || arguments.PluginId is not null;
}
