using System.Security.Cryptography;
using System.Text;

namespace Joydex.Secrets;

/// <summary>Standard local paths shared by the broker, Settings page, and helper.</summary>
public static class SecretsPaths
{
    public static string GetDefaultDataRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("LocalApplicationData is unavailable.");
        }
        return Path.Combine(localData, "Joydex");
    }

    public static string GetSecretsRoot(string dataRoot) =>
        Path.Combine(Path.GetFullPath(dataRoot), "secrets");

    public static string GetConfigurationPath(string dataRoot) =>
        Path.Combine(GetSecretsRoot(dataRoot), "configuration.json");

    public static string GetClientsPath(string dataRoot) =>
        Path.Combine(GetSecretsRoot(dataRoot), "clients.json");

    public static string GetPolicyPath(string dataRoot) =>
        Path.Combine(GetSecretsRoot(dataRoot), "policy.json");

    public static string GetAuditPath(string dataRoot) =>
        Path.Combine(GetSecretsRoot(dataRoot), "audit.jsonl");

    public static string GetOperatingModePath(string dataRoot) =>
        Path.Combine(GetSecretsRoot(dataRoot), "operating-mode.json");

    public static string GetMetricsPath(string dataRoot) =>
        Path.Combine(GetSecretsRoot(dataRoot), "metrics.json");

    public static string GetCredentialDirectory(string dataRoot) =>
        Path.Combine(GetSecretsRoot(dataRoot), "credentials");
}

/// <summary>Deterministic local endpoint names for one Joydex data root.</summary>
public sealed record SecretsBrokerEndpoint(
    string DataRoot,
    string PipeName,
    string MutexName,
    string ReadyEventName)
{
    public static SecretsBrokerEndpoint Create(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(fullRoot.ToUpperInvariant()))).ToLowerInvariant()[..24];
        return new(
            fullRoot,
            $"joydex-secrets-v1-{hash}",
            $@"Local\Joydex.Secrets.Broker.{hash}",
            $@"Local\Joydex.Secrets.Ready.{hash}");
    }
}

/// <summary>Value-free saved recipe metadata returned to authenticated helpers.</summary>
public sealed record SecretsRecipeMetadata(
    string RecipeId,
    string DisplayName,
    long Generation,
    string ProjectReference,
    IReadOnlyList<string> Aliases,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Parameters);

public sealed record SecretsCatalogResult(
    SecretsRequestStatus Status,
    IReadOnlyList<SecretAliasMetadata> Aliases,
    IReadOnlyList<SecretsRecipeMetadata> Recipes);

/// <summary>A metadata-only request accepted by the broker transport.</summary>
public sealed record SecretsBrokerSubmission(
    string RequestId,
    string ClientId,
    string ProjectReference,
    string Reason,
    string? RecipeId = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    ExecOperationProposal? Operation = null);

/// <summary>
/// Keeps broker generations alive while requests use them and adopts source or recipe edits for
/// new requests. Secret values stay inside each generation's provider.
/// </summary>
public sealed class SecretsBrokerRuntime
{
    private const int MaximumRoutes = 4096;
    private readonly object _gate = new();
    private readonly SecretsConfigurationStore _configuration;
    private readonly NamedClientRegistry _clients;
    private readonly SecretsPolicyStore _policy;
    private readonly SecretsAuditJournal _audit;
    private readonly SecretsOperatingModeStore _operatingMode;
    private readonly Dictionary<long, BrokerGeneration> _generations = [];
    private readonly Dictionary<RequestRoute, BrokerGeneration> _routes = [];
    private long _currentEpoch;
    private bool _sessionLocked;

    public SecretsBrokerRuntime(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _configuration = new SecretsConfigurationStore(SecretsPaths.GetConfigurationPath(dataRoot));
        _clients = new NamedClientRegistry(SecretsPaths.GetClientsPath(dataRoot));
        _policy = new SecretsPolicyStore(SecretsPaths.GetPolicyPath(dataRoot));
        _audit = new SecretsAuditJournal(SecretsPaths.GetAuditPath(dataRoot));
        _operatingMode = new SecretsOperatingModeStore(SecretsPaths.GetOperatingModePath(dataRoot));
        _audit.PruneActivity(DateTimeOffset.UtcNow.AddDays(-30));
    }

    public bool SessionLocked
    {
        get { lock (_gate) return _sessionLocked; }
    }

    public void SetSessionLocked(bool locked)
    {
        lock (_gate) _sessionLocked = locked;
    }

    public SecretsCatalogResult ListCatalog(
        string clientId,
        ReadOnlySpan<byte> credential,
        string projectReference)
    {
        if (_clients.Authenticate(
                clientId,
                credential,
                projectReference,
                SecretDeliveryMode.ExecInject) is null)
        {
            return new(SecretsRequestStatus.IdentityUnverified, [], []);
        }

        BrokerGeneration generation;
        try
        {
            generation = CurrentGeneration();
        }
        catch (InvalidOperationException)
        {
            return new(SecretsRequestStatus.ProviderUnavailable, [], []);
        }
        var aliases = generation.Core.ListAliases(clientId, credential, projectReference);
        var recipes = generation.Configuration.Recipes
            .Where(recipe => string.Equals(
                recipe.ProjectReference,
                projectReference,
                StringComparison.Ordinal))
            .Select(recipe => new SecretsRecipeMetadata(
                recipe.RecipeId,
                recipe.DisplayName,
                recipe.Generation,
                recipe.ProjectReference,
                recipe.SecretEnvironment.Values.Distinct(StringComparer.Ordinal).ToArray(),
                recipe.Parameters))
            .ToArray();
        return new(SecretsRequestStatus.Completed, aliases, recipes);
    }

    public SecretsRequestResponse Submit(
        SecretsBrokerSubmission submission,
        ReadOnlySpan<byte> credential)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var authentication = _clients.Authenticate(
                submission.ClientId,
                credential,
                submission.ProjectReference,
                SecretDeliveryMode.ExecInject);
        if (authentication is null)
        {
            return Unavailable(submission, SecretsRequestStatus.IdentityUnverified);
        }
        var route = CreateRoute(authentication, submission.RequestId);
        BrokerGeneration generation;
        SecretsRequestResponse response;
        lock (_gate)
        {
            if (!_routes.TryGetValue(route, out generation!))
            {
                try
                {
                    generation = CurrentGenerationLocked();
                }
                catch (InvalidOperationException)
                {
                    return Unavailable(submission, SecretsRequestStatus.ProviderUnavailable);
                }
            }
            var usesSavedRecipe = submission.Operation is null;
            var operation = ResolveOperation(generation.Configuration, submission);
            response = generation.Core.Submit(new SecretsRequestSubmission(
                submission.RequestId,
                submission.ClientId,
                submission.ProjectReference,
                operation,
                submission.Reason ?? string.Empty,
                usesSavedRecipe ? operation.OperationGeneration : 1), credential);
            if (response.Status is not SecretsRequestStatus.IdentityUnverified
                and not SecretsRequestStatus.InvalidRequest
                and not SecretsRequestStatus.ProviderUnavailable)
            {
                _routes.TryAdd(route, generation);
            }
        }
        PruneRoutes();
        return response;
    }

    public SecretsRequestResponse GetStatus(
        string clientId,
        ReadOnlySpan<byte> credential,
        string projectReference,
        string requestId)
    {
        var authentication = _clients.Authenticate(
            clientId,
            credential,
            projectReference,
            SecretDeliveryMode.ExecInject);
        if (authentication is null)
        {
            return new(
                SecretsRequestStatus.IdentityUnverified,
                requestId,
                Guid.Empty,
                null,
                DateTimeOffset.UtcNow);
        }
        var route = CreateRoute(authentication, requestId);
        BrokerGeneration? generation;
        lock (_gate)
        {
            _routes.TryGetValue(route, out generation);
        }
        var response = generation is not null
            ? generation.Core.GetStatus(clientId, credential, projectReference, requestId)
            : DurableStatus(clientId, credential, projectReference, requestId);
        PruneRoutes();
        return response;
    }

    public SecretsRequestResponse Cancel(
        string clientId,
        ReadOnlySpan<byte> credential,
        string projectReference,
        string requestId)
    {
        var authentication = _clients.Authenticate(
                clientId,
                credential,
                projectReference,
                SecretDeliveryMode.ExecInject);
        if (authentication is null)
        {
            return new(
                SecretsRequestStatus.IdentityUnverified,
                requestId,
                Guid.Empty,
                null,
                DateTimeOffset.UtcNow);
        }
        var route = CreateRoute(authentication, requestId);
        BrokerGeneration? generation;
        lock (_gate)
        {
            _routes.TryGetValue(route, out generation);
        }
        var response = generation is not null
            ? generation.Core.Cancel(clientId, credential, projectReference, requestId)
            : new(
                SecretsRequestStatus.InvalidRequest,
                requestId,
                Guid.Empty,
                null,
                DateTimeOffset.UtcNow);
        PruneRoutes();
        return response;
    }

    public SecretsRequestResponse Detach(
        string clientId,
        ReadOnlySpan<byte> credential,
        string projectReference,
        string requestId,
        SecretsDetachReason reason)
    {
        var authentication = _clients.Authenticate(
            clientId,
            credential,
            projectReference,
            SecretDeliveryMode.ExecInject);
        if (authentication is null)
        {
            return new(
                SecretsRequestStatus.IdentityUnverified,
                requestId,
                Guid.Empty,
                null,
                DateTimeOffset.UtcNow);
        }
        var route = CreateRoute(authentication, requestId);
        BrokerGeneration? generation;
        lock (_gate)
        {
            _routes.TryGetValue(route, out generation);
        }
        var response = generation is not null
            ? generation.Core.Detach(clientId, credential, projectReference, requestId, reason)
            : new(
                SecretsRequestStatus.InvalidRequest,
                requestId,
                Guid.Empty,
                null,
                DateTimeOffset.UtcNow);
        PruneRoutes();
        return response;
    }

    public async Task<SecretsExecResult> RunAsync(
        string clientId,
        ReadOnlyMemory<byte> credential,
        string projectReference,
        string requestId,
        string reservation,
        IReadOnlyDictionary<string, string> callerEnvironment,
        CancellationToken cancellationToken,
        SecretsStandardStreams? streams = null)
    {
        ArgumentNullException.ThrowIfNull(callerEnvironment);
        var authentication = _clients.Authenticate(
                clientId,
                credential.Span,
                projectReference,
                SecretDeliveryMode.ExecInject);
        if (authentication is null)
        {
            throw new UnauthorizedAccessException("The local requester could not be authenticated.");
        }
        BrokerGeneration generation;
        lock (_gate)
        {
            var route = CreateRoute(authentication, requestId);
            if (!_routes.TryGetValue(route, out generation!))
            {
                throw new InvalidOperationException("The request is no longer available in this broker session.");
            }
        }
        void VerifyProviderAccess()
        {
            lock (_gate)
            {
                if (_sessionLocked)
                {
                    throw new InvalidOperationException(
                        "Secrets is unavailable while the Windows session is locked.");
                }
                if (_configuration.Read().Epoch != generation.Configuration.Epoch)
                {
                    throw new InvalidOperationException(
                        "The Secrets configuration changed after this request was authorized.");
                }
            }
        }
        return await generation.Core.RunAsync(
            clientId,
            credential,
            projectReference,
            requestId,
            reservation,
            callerEnvironment,
            cancellationToken,
            VerifyProviderAccess,
            commit =>
            {
                lock (_gate)
                {
                    if (_sessionLocked)
                    {
                        throw new InvalidOperationException(
                            "Secrets is unavailable while the Windows session is locked.");
                    }
                    _configuration.WithSnapshotLock(snapshot =>
                    {
                        if (snapshot.Epoch != generation.Configuration.Epoch)
                        {
                            throw new InvalidOperationException(
                                "The Secrets configuration changed after this request was authorized.");
                        }
                        commit();
                        return true;
                    });
                }
            }, streams).ConfigureAwait(false);
    }

    public IReadOnlyList<SecretsPendingRequest> PendingRequests()
    {
        PruneRoutes();
        BrokerGeneration[] generations;
        lock (_gate)
        {
            if (_sessionLocked) return [];
            try { _ = CurrentGenerationLocked(); }
            catch (InvalidOperationException)
            {
                foreach (var staleGeneration in _generations.Values)
                {
                    staleGeneration.Core.RevokeActiveRequests();
                }
            }
            generations = _generations.Values.ToArray();
        }
        var pending = generations
            .SelectMany(generation => generation.Core.PendingRequests())
            .OrderBy(request => request.ExpiresAt)
            .ToArray();
        PruneRoutes();
        return pending;
    }

    public SecretsRequestResponse Decide(
        Guid attemptId,
        string displayChallenge,
        SecretsConsentChoice choice,
        bool requireOperation)
    {
        BrokerGeneration[] generations;
        lock (_gate)
        {
            if (_sessionLocked)
            {
                return new(
                    SecretsRequestStatus.Revoked,
                    "locked-request",
                    attemptId,
                    null,
                    DateTimeOffset.UtcNow);
            }
            try { _ = CurrentGenerationLocked(); }
            catch (InvalidOperationException)
            {
                foreach (var staleGeneration in _generations.Values)
                {
                    staleGeneration.Core.RevokeActiveRequests();
                }
            }
            generations = _generations.Values.ToArray();
        }
        var generation = generations.SingleOrDefault(candidate =>
            candidate.Core.PendingRequests().Any(request => request.AttemptId == attemptId));
        return generation?.Core.Decide(
                attemptId,
                displayChallenge,
                choice,
                requireOperation)
            ?? new(
                SecretsRequestStatus.Expired,
                "expired-request",
                attemptId,
                null,
                DateTimeOffset.UtcNow);
    }

    private BrokerGeneration CurrentGeneration()
    {
        lock (_gate) return CurrentGenerationLocked();
    }

    private BrokerGeneration CurrentGenerationLocked()
    {
        var snapshot = _configuration.Read();
        if (_generations.TryGetValue(snapshot.Epoch, out var existing)) return existing;
        if (_currentEpoch != 0)
        {
            foreach (var generation in _generations.Values)
            {
                generation.Core.RevokeActiveRequests();
            }
        }
        var sources = snapshot.Sources.Where(source => source.Enabled).ToArray();
        if (sources.Length == 0)
        {
            throw new InvalidOperationException("No dotenv source is configured.");
        }
        var provider = new ExactEnvSecretProvider(sources.Select(source =>
            new ExactEnvSecretSource(source.FilePath, source.Aliases)));
        var identities = sources.SelectMany(source => source.Aliases)
            .Select(alias => new SecretsAliasIdentity(
                alias.Alias,
                "env",
                alias.StableSecretId,
                alias.MappingGeneration))
            .ToArray();
        var created = new BrokerGeneration(
            snapshot,
            new SecretsBrokerCore(
                _clients,
                _policy,
                _audit,
                provider,
                identities,
                operatingMode: _operatingMode));
        _generations.Add(snapshot.Epoch, created);
        _currentEpoch = snapshot.Epoch;
        PruneGenerationsLocked();
        return created;
    }

    private SecretsRequestResponse DurableStatus(
        string clientId,
        ReadOnlySpan<byte> credential,
        string projectReference,
        string requestId)
    {
        var now = DateTimeOffset.UtcNow;
        var authentication = _clients.Authenticate(
            clientId,
            credential,
            projectReference,
            SecretDeliveryMode.ExecInject);
        if (authentication is null)
        {
            return new(SecretsRequestStatus.IdentityUnverified, requestId, Guid.Empty, null, now);
        }
        var committed = _audit.FindCommittedRequests()
            .Where(item => item.ClientRegistrationId == authentication.Principal.RegistrationId
                && item.ClientGeneration == authentication.Principal.Generation
                && string.Equals(item.RequestId, requestId, StringComparison.Ordinal))
            .ToArray();
        if (committed.Length == 0)
        {
            return new(SecretsRequestStatus.InvalidRequest, requestId, Guid.Empty, null, now);
        }
        var latest = committed[^1];
        return new(
            committed.Any(item => item.Outcome == SecretsCommittedOutcome.LaunchUnconfirmed)
                ? SecretsRequestStatus.LaunchUnconfirmed
                : committed.Any(item => item.Outcome is SecretsCommittedOutcome.FailedBeforeLaunch
                    or SecretsCommittedOutcome.Terminated)
                    ? SecretsRequestStatus.Revoked
                    : SecretsRequestStatus.Completed,
            requestId,
            latest.AttemptId,
            null,
            now);
    }

    private void PruneRoutes()
    {
        KeyValuePair<RequestRoute, BrokerGeneration>[] routes;
        lock (_gate) routes = _routes.ToArray();
        var removed = routes.Where(pair => !pair.Value.Core.ContainsTrackedRequest(
                pair.Key.ClientId,
                pair.Key.ProjectReference,
                pair.Key.RequestId))
            .ToArray();
        var terminal = routes.Where(pair => pair.Value.Core.ContainsTrackedRequest(
                pair.Key.ClientId,
                pair.Key.ProjectReference,
                pair.Key.RequestId)
                && !pair.Value.Core.ContainsActiveRequest(
                    pair.Key.ClientId,
                    pair.Key.ProjectReference,
                    pair.Key.RequestId))
            .ToArray();
        if (removed.Length == 0 && routes.Length <= MaximumRoutes) return;
        lock (_gate)
        {
            foreach (var pair in removed)
            {
                if (_routes.TryGetValue(pair.Key, out var current)
                    && ReferenceEquals(current, pair.Value))
                {
                    _routes.Remove(pair.Key);
                }
            }
            foreach (var pair in terminal)
            {
                if (_routes.Count <= MaximumRoutes) break;
                if (_routes.TryGetValue(pair.Key, out var current)
                    && ReferenceEquals(current, pair.Value))
                {
                    _routes.Remove(pair.Key);
                }
            }
            PruneGenerationsLocked();
        }
    }

    private void PruneGenerationsLocked()
    {
        var inUse = _routes.Values.ToHashSet();
        foreach (var epoch in _generations
                     .Where(pair => pair.Key != _currentEpoch && !inUse.Contains(pair.Value))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _generations.Remove(epoch);
        }
    }

    private SecretsRequestResponse Unavailable(
        SecretsBrokerSubmission submission,
        SecretsRequestStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var attemptId = Guid.NewGuid();
        var aliases = submission.Operation?.SecretEnvironment?.Values
            .Take(64)
            .Select(alias => SafeReference(alias, "invalid-alias"))
            .ToArray() ?? [];
        var client = SafeReference(submission.ClientId, "unverified-client");
        var project = SafeReference(submission.ProjectReference, "unverified-project");
        var request = SafeReference(submission.RequestId, "invalid-request");
        _audit.Append(new(
            attemptId,
            SecretsAuditEventKind.RequestReceived,
            now,
            request,
            client,
            project,
            aliases));
        _audit.Append(new(
            attemptId,
            SecretsAuditEventKind.RequestOutcome,
            now,
            request,
            client,
            project,
            aliases,
            Outcome: status.ToString().ToLowerInvariant()));
        return new(status, request, attemptId, null, now);
    }

    private static ExecOperationProposal ResolveOperation(
        SecretsConfigurationSnapshot configuration,
        SecretsBrokerSubmission submission)
    {
        try
        {
            if ((submission.RecipeId is null) == (submission.Operation is null))
            {
                throw new ArgumentException("A request must select one recipe or one inline operation.");
            }
            if (submission.Operation is not null) return submission.Operation;
            var recipe = configuration.Recipes.SingleOrDefault(candidate => string.Equals(
                candidate.RecipeId,
                submission.RecipeId,
                StringComparison.Ordinal))
                ?? throw new KeyNotFoundException("The selected recipe does not exist.");
            if (!string.Equals(
                    recipe.ProjectReference,
                    submission.ProjectReference,
                    StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("The selected recipe belongs to another project.");
            }
            return recipe.Resolve(submission.Parameters
                ?? new Dictionary<string, string>(StringComparer.Ordinal));
        }
        catch (Exception exception) when (exception is ArgumentException
            or KeyNotFoundException
            or UnauthorizedAccessException)
        {
            return new ExecOperationProposal(
                string.Empty,
                [],
                string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["INVALID"] = "invalid-alias" },
                [],
                SecretOutputDisclosure.None);
        }
    }

    private static string SafeReference(string? value, string fallback) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 96
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? value
            : fallback;

    private static RequestRoute CreateRoute(
        NamedClientAuthentication authentication,
        string requestId) => new(
            authentication.Principal.RegistrationId,
            authentication.Principal.Generation,
            authentication.Principal.ClientId,
            authentication.Project.ProjectReference,
            requestId);

    private sealed record RequestRoute(
        Guid ClientRegistrationId,
        long ClientGeneration,
        string ClientId,
        string ProjectReference,
        string RequestId);

    private sealed record BrokerGeneration(
        SecretsConfigurationSnapshot Configuration,
        SecretsBrokerCore Core);
}
