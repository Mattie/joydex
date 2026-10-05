using Joydex.Contracts;

namespace Joydex.App;

internal sealed record RuntimePluginCommandOutcome(
    RuntimeCommandKind Kind,
    RuntimeCommandStatus Status,
    string Detail,
    RuntimePluginSnapshot Plugins);

/// <summary>
/// Runs the narrow bundled-plugin command surface and retains side-effecting operation IDs across
/// runtime-client reconnects. A lost Restart or Reload reply is looked up before another command
/// can be submitted.
/// </summary>
internal sealed class RuntimePluginConnectionServices
{
    private readonly object _connectionGate = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private long _connectionGeneration;
    private Guid _engineEpoch;
    private IRuntimeCommandRunner? _commands;
    private PendingOperation? _pendingMutation;
    private bool _supported;

    public bool IsAvailable
    {
        get
        {
            lock (_connectionGate)
            {
                return _commands is not null && _supported;
            }
        }
    }

    public void BeginConnection(
        long connectionGeneration,
        IRuntimeCommandRunner commands,
        bool supported,
        Guid engineEpoch = default)
    {
        if (connectionGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(connectionGeneration));
        }
        ArgumentNullException.ThrowIfNull(commands);
        lock (_connectionGate)
        {
            if (connectionGeneration <= _connectionGeneration)
            {
                throw new InvalidOperationException("The PAD connection generation must advance.");
            }

            _connectionGeneration = connectionGeneration;
            _commands = commands;
            _supported = supported;
            _engineEpoch = engineEpoch;
        }
    }

    public void EndConnection(long connectionGeneration)
    {
        lock (_connectionGate)
        {
            if (connectionGeneration != _connectionGeneration)
            {
                return;
            }
            _commands = null;
            _supported = false;
        }
    }

    public Task<RuntimePluginCommandOutcome> InspectAsync(CancellationToken cancellationToken) =>
        ExecuteInspectionAsync(cancellationToken);

    public Task<RuntimePluginCommandOutcome> RestartPadAsync(CancellationToken cancellationToken) =>
        ExecuteRecoverableAsync(RuntimeCommandKind.RestartPlugin, cancellationToken);

    public Task<RuntimePluginCommandOutcome> ReloadPadConfigurationAsync(
        CancellationToken cancellationToken) =>
        ExecuteRecoverableAsync(RuntimeCommandKind.ReloadPluginConfiguration, cancellationToken);

    private async Task<RuntimePluginCommandOutcome> ExecuteInspectionAsync(
        CancellationToken cancellationToken)
    {
        var (commands, generation, _) = GetConnection();
        var operationId = Guid.NewGuid();
        var result = await commands.ExecuteAsync(
                new RuntimeCommandRequest(operationId, RuntimeCommandKind.InspectPlugins),
                cancellationToken)
            .ConfigureAwait(true);
        EnsureCurrent(commands, generation);
        return ValidateResult(result, operationId, RuntimeCommandKind.InspectPlugins);
    }

    private async Task<RuntimePluginCommandOutcome> ExecuteRecoverableAsync(
        RuntimeCommandKind kind,
        CancellationToken cancellationToken)
    {
        if (kind is not (RuntimeCommandKind.RestartPlugin
            or RuntimeCommandKind.ReloadPluginConfiguration))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var (commands, generation, engineEpoch) = GetConnection();
            if (_pendingMutation is { } pending)
            {
                var recovered = await commands.GetOperationAsync(pending.OperationId, cancellationToken)
                    .ConfigureAwait(true);
                EnsureCurrent(commands, generation);
                if (recovered.OperationId != pending.OperationId)
                {
                    throw new InvalidDataException("The runtime returned a different PAD operation.");
                }
                if (recovered.State == RuntimeCommandOperationState.Completed
                    && recovered.Result is { } completed)
                {
                    ValidateIdentity(completed, pending.OperationId, pending.Kind);
                    RemovePending(pending.OperationId);
                    var recoveredOutcome = ValidateResult(
                        completed,
                        pending.OperationId,
                        pending.Kind);
                    return recoveredOutcome;
                }
                if (recovered.State == RuntimeCommandOperationState.NotFound
                    && pending.EngineEpoch != engineEpoch)
                {
                    RemovePending(pending.OperationId);
                    throw new InvalidOperationException(
                        "The runtime restarted before the PAD action could be recovered. Check PAD health before choosing another action.");
                }

                throw new InvalidOperationException(recovered.State switch
                {
                    RuntimeCommandOperationState.Running =>
                        $"The earlier {DisplayName(pending.Kind)} action is still running.",
                    _ =>
                        $"The earlier {DisplayName(pending.Kind)} action could not be reconciled and was not resubmitted.",
                });
            }

            var operationId = Guid.NewGuid();
            _pendingMutation = new PendingOperation(operationId, kind, engineEpoch);
            var request = new RuntimeCommandRequest(
                operationId,
                kind,
                new RuntimeCommandArguments(PluginId: RuntimePluginIds.Pad));
            // Exceptions retain the operation ID so a later mutation reconciles it first.
            var result = await commands.ExecuteAsync(request, cancellationToken).ConfigureAwait(true);
            EnsureCurrent(commands, generation);
            ValidateIdentity(result, operationId, kind);
            RemovePending(operationId);
            return ValidateResult(result, operationId, kind);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private (IRuntimeCommandRunner Commands, long Generation, Guid EngineEpoch) GetConnection()
    {
        lock (_connectionGate)
        {
            if (_commands is null || !_supported)
            {
                throw new InvalidOperationException(
                    "The connected Joydex runtime does not support PAD management.");
            }
            return (_commands, _connectionGeneration, _engineEpoch);
        }
    }

    private void EnsureCurrent(IRuntimeCommandRunner commands, long generation)
    {
        lock (_connectionGate)
        {
            if (!ReferenceEquals(_commands, commands)
                || _connectionGeneration != generation
                || !_supported)
            {
                throw new OperationCanceledException(
                    "The runtime connection changed before the PAD command completed.");
            }
        }
    }

    private void RemovePending(Guid operationId)
    {
        if (_pendingMutation?.OperationId == operationId)
        {
            _pendingMutation = null;
        }
    }

    private static void ValidateIdentity(
        RuntimeCommandResult result,
        Guid operationId,
        RuntimeCommandKind kind)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.OperationId != operationId || result.Kind != kind)
        {
            throw new InvalidDataException("The runtime returned a result for a different PAD command.");
        }
    }

    private static RuntimePluginCommandOutcome ValidateResult(
        RuntimeCommandResult result,
        Guid operationId,
        RuntimeCommandKind kind)
    {
        ValidateIdentity(result, operationId, kind);
        if (result.Status != RuntimeCommandStatus.Completed && result.Payload?.Plugins is null)
        {
            // Expired command results retain their identity but no longer carry UI payloads.
            throw new InvalidOperationException(result.Detail ?? "The PAD action failed. Refresh PAD health before trying again.");
        }
        var plugins = ValidateSnapshot(result.Payload?.Plugins);
        var detail = string.IsNullOrWhiteSpace(result.Detail)
            ? result.Status == RuntimeCommandStatus.Completed
                ? "PAD status refreshed."
                : "The PAD command did not complete."
            : result.Detail.Trim();
        if (detail.Length > RuntimePluginLimits.MaximumHealthDetailCharacters
            || detail.Any(char.IsControl))
        {
            throw new InvalidDataException("The runtime returned an invalid PAD command detail.");
        }
        return new RuntimePluginCommandOutcome(kind, result.Status, detail, plugins);
    }

    private static RuntimePluginSnapshot ValidateSnapshot(RuntimePluginSnapshot? snapshot)
    {
        if (snapshot?.Registrations is null || snapshot.Health is null)
        {
            throw new InvalidDataException("The runtime omitted the bundled plugin status.");
        }

        var registrations = snapshot.Registrations.ToArray();
        var health = snapshot.Health.ToArray();
        if (registrations.Length == 0
            || registrations.Length > RuntimePluginLimits.MaximumBundledPlugins
            || registrations.Any(registration => !ValidRegistration(registration))
            || registrations.Select(registration => registration.Id)
                .Distinct(StringComparer.Ordinal).Count() != registrations.Length)
        {
            throw new InvalidDataException("The runtime returned invalid bundled plugin metadata.");
        }

        var registered = registrations
            .Select(registration => registration.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (health.Length != registrations.Length
            || health.Length > RuntimePluginLimits.MaximumBundledPlugins
            || health.Any(item => !ValidHealth(item) || !registered.Contains(item.PluginId))
            || health.Select(item => item.PluginId)
                .Distinct(StringComparer.Ordinal).Count() != health.Length)
        {
            throw new InvalidDataException("The runtime returned invalid bundled plugin health.");
        }
        if (!registered.Contains(RuntimePluginIds.Pad)
            || !health.Any(item => string.Equals(
                item.PluginId,
                RuntimePluginIds.Pad,
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The runtime did not report the bundled PAD plugin.");
        }

        return new RuntimePluginSnapshot(registrations, health);
    }

    private static bool ValidRegistration(RuntimePluginRegistration registration) =>
        registration is not null
        && RuntimePluginLimits.IsCanonicalPluginId(registration.Id)
        && !string.IsNullOrWhiteSpace(registration.Version)
        && registration.Version.Length <= 64
        && !registration.Version.Any(char.IsControl)
        && registration.HostApiMajor >= 0
        && registration.MinimumHostApiMinor >= 0
        && registration.SettingsSchemaVersion >= 0;

    private static bool ValidHealth(RuntimePluginHealth health) =>
        health is not null
        && RuntimePluginLimits.IsCanonicalPluginId(health.PluginId)
        && Enum.IsDefined(health.State)
        && health.Generation >= 0
        && !string.IsNullOrWhiteSpace(health.Detail)
        && health.Detail.Length <= RuntimePluginLimits.MaximumHealthDetailCharacters
        && !health.Detail.Any(char.IsControl);

    private static string DisplayName(RuntimeCommandKind kind) => kind switch
    {
        RuntimeCommandKind.RestartPlugin => "Restart PAD",
        RuntimeCommandKind.ReloadPluginConfiguration => "Reload PAD configuration",
        _ => "PAD",
    };

    private sealed record PendingOperation(Guid OperationId, RuntimeCommandKind Kind, Guid EngineEpoch);
}
