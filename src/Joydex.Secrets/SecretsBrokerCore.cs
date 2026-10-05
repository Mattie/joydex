using System.Security.Cryptography;
using System.Text;

namespace Joydex.Secrets;

public enum SecretsRequestStatus
{
    Pending,
    AgentDetached,
    Allowed,
    Denied,
    Expired,
    Revoked,
    IdentityUnverified,
    ProviderUnavailable,
    QueueFull,
    Conflict,
    Completed,
    LaunchUnconfirmed,
    InvalidRequest,
}

/// <summary>Why the requesting helper stopped waiting while local policy review remained open.</summary>
public enum SecretsDetachReason
{
    RunWithoutSecrets,
    LeavePending,
}

/// <summary>Metadata-only request submitted by one local requester.</summary>
public sealed record SecretsRequestSubmission(
    string RequestId,
    string ClientId,
    string ProjectReference,
    ExecOperationProposal Operation,
    string Reason,
    long OperationGeneration = 1);

/// <summary>Requester-visible result. The opaque reservation still requires client authentication.</summary>
public sealed record SecretsRequestResponse(
    SecretsRequestStatus Status,
    string RequestId,
    Guid AttemptId,
    string? Reservation,
    DateTimeOffset ExpiresAt);

/// <summary>Safe display model consumed only by the broker-owned consent surface.</summary>
public sealed record SecretsPendingRequest(
    Guid AttemptId,
    string RequestId,
    string ClientLabel,
    string ProjectReference,
    string WorktreeReference,
    string ProgramLabel,
    IReadOnlyList<string> EnvironmentVariables,
    string CommandLine,
    bool CommandLineTruncated,
    IReadOnlyList<string> Aliases,
    string OperationDigest,
    string Reason,
    string DisplayChallenge,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> FingerprintInputs,
    SecretOutputDisclosure OutputDisclosure,
    IReadOnlyList<string> NewAliases,
    SecretsDetachReason? DetachedReason,
    SecretsExecutionLifetime Lifetime = SecretsExecutionLifetime.Attached,
    int? DetachedTimeoutSeconds = null);

/// <summary>
/// Coordinates authenticated named-client requests, exact policy, pending consent and one-use exec.
/// </summary>
public sealed class SecretsBrokerCore
{
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DetachedReviewLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan TerminalRequestRetention = TimeSpan.FromMinutes(30);
    private const int MaximumTrackedRequests = 2048;
    private readonly object _gate = new();
    private readonly NamedClientRegistry _clients;
    private readonly SecretsPolicyStore _policy;
    private readonly SecretsAuditJournal _audit;
    private readonly ExactEnvSecretProvider _provider;
    private readonly SecretsOperatingModeStore? _operatingMode;
    private readonly IReadOnlyDictionary<string, SecretsAliasIdentity> _aliases;
    private readonly SecretsReservationStore _reservations;
    private readonly SecretsExecLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly Dictionary<RequestKey, RequestState> _requests = [];
    private readonly int _maximumPending;
    private readonly int _maximumPendingPerClient;

    public SecretsBrokerCore(
        NamedClientRegistry clients,
        SecretsPolicyStore policy,
        SecretsAuditJournal audit,
        ExactEnvSecretProvider provider,
        IEnumerable<SecretsAliasIdentity> aliases,
        TimeProvider? timeProvider = null,
        int maximumPending = 64,
        int maximumPendingPerClient = 8,
        int maximumReservations = 2048,
        SecretsOperatingModeStore? operatingMode = null)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _operatingMode = operatingMode;
        ArgumentNullException.ThrowIfNull(aliases);
        _aliases = aliases.ToDictionary(alias => alias.Alias, StringComparer.Ordinal);
        if (_aliases.Count == 0 || maximumPending < 1 || maximumPendingPerClient < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPending));
        }
        _time = timeProvider ?? TimeProvider.System;
        _maximumPending = maximumPending;
        _maximumPendingPerClient = maximumPendingPerClient;
        _reservations = new(maximumReservations);
        _launcher = new SecretsExecLauncher(audit);
    }

    /// <summary>Authenticates and evaluates one request after durably recording its arrival.</summary>
    public SecretsRequestResponse Submit(
        SecretsRequestSubmission submission,
        ReadOnlySpan<byte> credential)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var now = _time.GetUtcNow();
        var attemptId = Guid.NewGuid();
        var safeRequestId = SafeIdentifier(submission.RequestId, "invalid-request");
        var safeClient = SafeIdentifier(submission.ClientId, "unverified-client");
        var safeProject = SafeIdentifier(submission.ProjectReference, "unverified-project");
        var requestedAliases = SafeAliases(submission.Operation?.SecretEnvironment?.Values);
        _audit.Append(new(
            attemptId,
            SecretsAuditEventKind.RequestReceived,
            now,
            safeRequestId,
            safeClient,
            safeProject,
            requestedAliases));

        if (!ValidSubmission(submission))
        {
            return FinishAttempt(
                attemptId, safeRequestId, safeClient, safeProject, requestedAliases,
                SecretsRequestStatus.InvalidRequest, now, operationDigest: null);
        }

        var authentication = _clients.Authenticate(
            submission.ClientId,
            credential,
            submission.ProjectReference,
            SecretDeliveryMode.ExecInject);
        if (authentication is null)
        {
            return FinishAttempt(
                attemptId, safeRequestId, safeClient, safeProject, requestedAliases,
                SecretsRequestStatus.IdentityUnverified, now, operationDigest: null);
        }

        if (submission.Operation!.SecretEnvironment.Values.Any(alias => !_aliases.ContainsKey(alias)))
        {
            return FinishAttempt(
                attemptId,
                submission.RequestId,
                authentication.Principal.ClientId,
                authentication.Project.ProjectReference,
                requestedAliases,
                SecretsRequestStatus.ProviderUnavailable,
                now,
                operationDigest: null);
        }

        ResolvedExecOperation operation;
        SecretsAliasIdentity[] aliasIdentities;
        try
        {
            operation = ExecOperationCanonicalizer.Resolve(
                authentication.Project,
                _aliases.Keys.ToArray(),
                submission.Operation!,
                submission.OperationGeneration);
            aliasIdentities = operation.SecretEnvironment.Values
                .Distinct(StringComparer.Ordinal)
                .Select(alias => _aliases[alias])
                .ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return FinishAttempt(
                attemptId,
                submission.RequestId,
                authentication.Principal.ClientId,
                authentication.Project.ProjectReference,
                requestedAliases,
                SecretsRequestStatus.InvalidRequest,
                now,
                operationDigest: null);
        }

        if (!ProviderAliasesAreCurrent(aliasIdentities))
        {
            return FinishAttempt(
                attemptId,
                submission.RequestId,
                authentication.Principal.ClientId,
                authentication.Project.ProjectReference,
                aliasIdentities.Select(alias => alias.Alias).ToArray(),
                SecretsRequestStatus.ProviderUnavailable,
                now,
                operation.Identity.Digest);
        }

        var scope = SecretsScopeFactory.Create(
            authentication.Principal,
            authentication.Project,
            aliasIdentities,
            operation.Identity);
        var key = new RequestKey(
            authentication.Principal.RegistrationId,
            authentication.Principal.Generation,
            submission.RequestId);

        lock (_gate)
        {
            MaintainRequests(now);
            var committed = _audit.FindCommittedRequests()
                .Where(item => item.ClientRegistrationId == key.RegistrationId
                    && item.ClientGeneration == key.Generation
                    && string.Equals(item.RequestId, key.RequestId, StringComparison.Ordinal))
                .ToArray();
            if (committed.Length > 0)
            {
                var status = !committed.All(item => FixedEquals(
                        item.OperationDigest,
                        operation.Identity.Digest))
                    ? SecretsRequestStatus.Conflict
                    : CommittedStatus(committed);
                _audit.Append(OutcomeRecord(
                    attemptId,
                    now,
                    submission.RequestId,
                    authentication.Principal.ClientId,
                    authentication.Project.ProjectReference,
                    aliasIdentities,
                    operation.Identity.Digest,
                    status,
                    rule: null));
                return new(status, submission.RequestId, attemptId, null, now);
            }
            if (_requests.TryGetValue(key, out var existing))
            {
                var status = FixedEquals(existing.Scope.ExactScopeDigest, scope.ExactScopeDigest)
                    ? existing.Response.Status
                    : SecretsRequestStatus.Conflict;
                _audit.Append(OutcomeRecord(
                    attemptId,
                    now,
                    submission.RequestId,
                    authentication.Principal.ClientId,
                    authentication.Project.ProjectReference,
                    aliasIdentities,
                    operation.Identity.Digest,
                    status,
                    rule: null));
                return status == SecretsRequestStatus.Conflict
                    ? new(status, submission.RequestId, attemptId, null, now)
                    : existing.Response;
            }

            var snapshot = _policy.Read(now);
            var operatingMode = ReadOperatingMode(now);
            if (operatingMode.Mode == SecretsOperatingMode.DenyAll)
            {
                var response = new SecretsRequestResponse(
                    SecretsRequestStatus.Denied,
                    submission.RequestId,
                    attemptId,
                    null,
                    now);
                _requests.Add(key, new(
                    scope, operation, response, null, submission.Reason, null, AutoAllowed: false, [], null));
                _audit.Append(OutcomeRecord(
                    attemptId,
                    now,
                    submission.RequestId,
                    authentication.Principal.ClientId,
                    authentication.Project.ProjectReference,
                    aliasIdentities,
                    operation.Identity.Digest,
                    response.Status,
                    rule: null));
                return response;
            }

            if (operatingMode.Mode == SecretsOperatingMode.AutoAllow24Hours)
            {
                var response = TryAllowed(
                    attemptId,
                    submission.RequestId,
                    scope,
                    snapshot.Epoch,
                    operatingMode.Epoch,
                    now);
                _requests.Add(key, new(
                    scope,
                    operation,
                    response,
                    null,
                    submission.Reason,
                    null,
                    AutoAllowed: response.Status == SecretsRequestStatus.Allowed,
                    [],
                    null));
                _audit.Append(OutcomeRecord(
                    attemptId,
                    now,
                    submission.RequestId,
                    authentication.Principal.ClientId,
                    authentication.Project.ProjectReference,
                    aliasIdentities,
                    operation.Identity.Digest,
                    response.Status,
                    rule: null));
                return response;
            }

            var evaluation = SecretsPolicyEvaluator.Evaluate(scope, snapshot.Rules, now);
            if (evaluation.Disposition == SecretsPolicyDisposition.Denied)
            {
                var response = new SecretsRequestResponse(
                    SecretsRequestStatus.Denied,
                    submission.RequestId,
                    attemptId,
                    null,
                    now);
                _requests.Add(key, new(
                    scope, operation, response, null, submission.Reason, null, AutoAllowed: false, [], null));
                _audit.Append(OutcomeRecord(
                    attemptId,
                    now,
                    submission.RequestId,
                    authentication.Principal.ClientId,
                    authentication.Project.ProjectReference,
                    aliasIdentities,
                    operation.Identity.Digest,
                    response.Status,
                    evaluation.MatchedRule));
                return response;
            }

            if (evaluation.Disposition == SecretsPolicyDisposition.Allowed)
            {
                var response = TryAllowed(
                    attemptId,
                    submission.RequestId,
                    scope,
                    snapshot.Epoch,
                    operatingMode.Epoch,
                    now);
                _requests.Add(key, new(
                    scope,
                    operation,
                    response,
                    null,
                    submission.Reason,
                    evaluation.MatchedRule?.RuleId,
                    AutoAllowed: false,
                    [],
                    null));
                _audit.Append(OutcomeRecord(
                    attemptId,
                    now,
                    submission.RequestId,
                    authentication.Principal.ClientId,
                    authentication.Project.ProjectReference,
                    aliasIdentities,
                    operation.Identity.Digest,
                    response.Status,
                    evaluation.MatchedRule));
                return response;
            }

            var pendingCount = _requests.Values.Count(request => request.Response.Status
                is SecretsRequestStatus.Pending or SecretsRequestStatus.AgentDetached);
            var clientPending = _requests.Count(pair =>
                pair.Key.RegistrationId == authentication.Principal.RegistrationId
                && pair.Value.Response.Status
                    is SecretsRequestStatus.Pending or SecretsRequestStatus.AgentDetached);
            if (pendingCount >= _maximumPending || clientPending >= _maximumPendingPerClient)
            {
                return FinishAttempt(
                    attemptId,
                    submission.RequestId,
                    authentication.Principal.ClientId,
                    authentication.Project.ProjectReference,
                    aliasIdentities.Select(alias => alias.Alias).ToArray(),
                    SecretsRequestStatus.QueueFull,
                    now,
                    operation.Identity.Digest);
            }

            var expiresAt = now + RequestLifetime;
            var previouslyRequestedAliases = _audit.ReadAll()
                .Where(record => record.AttemptId != attemptId
                    && record.Kind is SecretsAuditEventKind.RequestOutcome
                        or SecretsAuditEventKind.ConsentDecision
                    && record.OperationDigest is not null
                    && string.Equals(
                        record.ClientReference,
                        authentication.Principal.ClientId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        record.ProjectReference,
                        authentication.Project.ProjectReference,
                        StringComparison.Ordinal))
                .SelectMany(record => record.Aliases)
                .ToHashSet(StringComparer.Ordinal);
            var newAliases = aliasIdentities
                .Select(alias => alias.Alias)
                .Where(alias => !previouslyRequestedAliases.Contains(alias))
                .ToArray();
            var responsePending = new SecretsRequestResponse(
                SecretsRequestStatus.Pending,
                submission.RequestId,
                attemptId,
                null,
                expiresAt);
            var challenge = Base64Url(RandomNumberGenerator.GetBytes(24));
            _requests.Add(key, new(
                scope,
                operation,
                responsePending,
                challenge,
                submission.Reason,
                null,
                AutoAllowed: false,
                newAliases,
                null));
            _audit.Append(OutcomeRecord(
                attemptId,
                now,
                submission.RequestId,
                authentication.Principal.ClientId,
                authentication.Project.ProjectReference,
                aliasIdentities,
                operation.Identity.Digest,
                responsePending.Status,
                rule: null));
            return responsePending;
        }
    }

    /// <summary>Lists requests that still need a decision in the broker-owned consent window.</summary>
    public IReadOnlyList<SecretsPendingRequest> PendingRequests()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            MaintainRequests(now);
            ReconcileOperatingMode(now);
            return _requests.Values
                .Where(request => request.Response.Status is SecretsRequestStatus.Pending
                    or SecretsRequestStatus.AgentDetached)
                .OrderBy(request => request.Response.AttemptId)
                .Select(CreatePendingRequest)
                .ToArray();
        }
    }

    private static SecretsPendingRequest CreatePendingRequest(RequestState request)
    {
        var commandLine = CommandLineForDisplay(request.Operation, out var commandLineTruncated);
        return new(
            request.Response.AttemptId,
            request.Response.RequestId,
            request.Scope.Principal.DisplayLabel,
            request.Scope.Project.ProjectReference,
            request.Scope.Project.WorktreeReference
                ?? Path.GetFileName(request.Scope.Project.CanonicalWorktreeRoot),
            Path.GetFileName(request.Operation.Executable),
            request.Operation.SecretEnvironment.Keys.ToArray(),
            commandLine,
            commandLineTruncated,
            request.Scope.Aliases.Select(alias => alias.Alias).ToArray(),
            request.Operation.Identity.Digest,
            BoundReason(request.Reason),
            request.Challenge!,
            request.Response.ExpiresAt,
            request.Operation.Fingerprints.Keys.ToArray(),
            request.Operation.OutputDisclosure,
            request.NewAliases,
            request.DetachedReason, request.Operation.Lifetime, request.Operation.DetachedTimeoutSeconds);
    }

    private void ReconcileOperatingMode(DateTimeOffset now)
    {
        var operatingMode = ReadOperatingMode(now);
        if (operatingMode.Mode == SecretsOperatingMode.Ask) return;

        var policyEpoch = operatingMode.Mode == SecretsOperatingMode.AutoAllow24Hours
            ? _policy.Read(now).Epoch
            : 1;
        foreach (var pair in _requests
                     .Where(candidate => candidate.Value.Response.Status == SecretsRequestStatus.Pending)
                     .ToArray())
        {
            var request = pair.Value;
            var response = operatingMode.Mode == SecretsOperatingMode.DenyAll
                ? request.Response with
                {
                    Status = SecretsRequestStatus.Denied,
                    Reservation = null,
                    ExpiresAt = now,
                }
                : TryAllowed(
                    request.Response.AttemptId,
                    request.Response.RequestId,
                    request.Scope,
                    policyEpoch,
                    operatingMode.Epoch,
                    now);
            _requests[pair.Key] = request with
            {
                Response = response,
                Challenge = null,
                AuthorizingRuleId = null,
                AutoAllowed = operatingMode.Mode == SecretsOperatingMode.AutoAllow24Hours
                    && response.Status == SecretsRequestStatus.Allowed,
            };
            _audit.Append(OutcomeRecord(
                request.Response.AttemptId,
                now,
                request.Response.RequestId,
                request.Scope.Principal.ClientId,
                request.Scope.Project.ProjectReference,
                request.Scope.Aliases,
                request.Operation.Identity.Digest,
                response.Status,
                rule: null));
        }
    }

    /// <summary>Lists value-free aliases after authenticating the local requester and project.</summary>
    public IReadOnlyList<SecretAliasMetadata> ListAliases(
        string clientId,
        ReadOnlySpan<byte> credential,
        string projectReference)
    {
        _ = _clients.Authenticate(
            clientId,
            credential,
            projectReference,
            SecretDeliveryMode.ExecInject)
            ?? throw new UnauthorizedAccessException("The local requester could not be authenticated.");
        return _provider.ListAliases();
    }

    /// <summary>Returns the current state of one request owned by the authenticated client.</summary>
    public SecretsRequestResponse GetStatus(
        string clientId,
        ReadOnlySpan<byte> credential,
        string projectReference,
        string requestId)
    {
        var now = _time.GetUtcNow();
        var authentication = _clients.Authenticate(
            clientId,
            credential,
            projectReference,
            SecretDeliveryMode.ExecInject);
        if (authentication is null)
        {
            return new(SecretsRequestStatus.IdentityUnverified, requestId, Guid.Empty, null, now);
        }

        lock (_gate)
        {
            MaintainRequests(now);
            ReconcileOperatingMode(now);
            var key = new RequestKey(
                authentication.Principal.RegistrationId,
                authentication.Principal.Generation,
                requestId);
            return _requests.TryGetValue(key, out var request)
                ? request.Response
                : new(SecretsRequestStatus.InvalidRequest, requestId, Guid.Empty, null, now);
        }
    }

    /// <summary>Cancels one pending request owned by the authenticated client.</summary>
    public SecretsRequestResponse Cancel(
        string clientId,
        ReadOnlySpan<byte> credential,
        string projectReference,
        string requestId)
    {
        var now = _time.GetUtcNow();
        var authentication = _clients.Authenticate(
            clientId,
            credential,
            projectReference,
            SecretDeliveryMode.ExecInject);
        if (authentication is null)
        {
            return new(SecretsRequestStatus.IdentityUnverified, requestId, Guid.Empty, null, now);
        }

        lock (_gate)
        {
            MaintainRequests(now);
            ReconcileOperatingMode(now);
            var key = new RequestKey(
                authentication.Principal.RegistrationId,
                authentication.Principal.Generation,
                requestId);
            if (!_requests.TryGetValue(key, out var request))
            {
                return new(SecretsRequestStatus.InvalidRequest, requestId, Guid.Empty, null, now);
            }
            if (request.Response.Status != SecretsRequestStatus.Pending)
            {
                return request.Response;
            }

            var response = request.Response with
            {
                Status = SecretsRequestStatus.Denied,
                ExpiresAt = now,
            };
            _requests[key] = request with { Response = response, Challenge = null };
            _audit.Append(OutcomeRecord(
                request.Response.AttemptId,
                now,
                requestId,
                request.Scope.Principal.ClientId,
                request.Scope.Project.ProjectReference,
                request.Scope.Aliases,
                request.Operation.Identity.Digest,
                response.Status,
                rule: null));
            return response;
        }
    }

    /// <summary>
    /// Ends the helper's authority over one pending request while keeping a short, policy-only
    /// review window open for the local user. A detached request can never create a reservation.
    /// </summary>
    public SecretsRequestResponse Detach(
        string clientId,
        ReadOnlySpan<byte> credential,
        string projectReference,
        string requestId,
        SecretsDetachReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        var now = _time.GetUtcNow();
        var authentication = _clients.Authenticate(
            clientId,
            credential,
            projectReference,
            SecretDeliveryMode.ExecInject);
        if (authentication is null)
        {
            return new(SecretsRequestStatus.IdentityUnverified, requestId, Guid.Empty, null, now);
        }

        lock (_gate)
        {
            MaintainRequests(now);
            ReconcileOperatingMode(now);
            var key = new RequestKey(
                authentication.Principal.RegistrationId,
                authentication.Principal.Generation,
                requestId);
            if (!_requests.TryGetValue(key, out var request))
            {
                return new(SecretsRequestStatus.InvalidRequest, requestId, Guid.Empty, null, now);
            }
            if (request.Response.Status != SecretsRequestStatus.Pending)
            {
                return request.Response;
            }

            var response = request.Response with
            {
                Status = SecretsRequestStatus.AgentDetached,
                Reservation = null,
                ExpiresAt = now + DetachedReviewLifetime,
            };
            _requests[key] = request with
            {
                Response = response,
                DetachedReason = reason,
            };
            _audit.Append(OutcomeRecord(
                request.Response.AttemptId,
                now,
                requestId,
                request.Scope.Principal.ClientId,
                request.Scope.Project.ProjectReference,
                request.Scope.Aliases,
                request.Operation.Identity.Digest,
                response.Status,
                rule: null) with
            {
                Kind = SecretsAuditEventKind.RequestDetached,
                Outcome = reason == SecretsDetachReason.RunWithoutSecrets
                    ? "continued-without-broker-injection"
                    : "agent-stopped-waiting",
            });
            return response;
        }
    }

    /// <summary>Applies one trusted local consent response to the matching displayed challenge.</summary>
    public SecretsRequestResponse Decide(
        Guid attemptId,
        string displayChallenge,
        SecretsConsentChoice choice,
        bool requireOperation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayChallenge);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            MaintainRequests(now);
            ReconcileOperatingMode(now);
            var pair = _requests.SingleOrDefault(candidate =>
                candidate.Value.Response.AttemptId == attemptId);
            var request = pair.Value;
            if (request is null
                || request.Response.Status is not SecretsRequestStatus.Pending
                    and not SecretsRequestStatus.AgentDetached
                || !FixedEquals(request.Challenge!, displayChallenge))
            {
                return new(
                    SecretsRequestStatus.Expired,
                    request?.Response.RequestId ?? "expired-request",
                    attemptId,
                    null,
                    now);
            }

            var detached = request.Response.Status == SecretsRequestStatus.AgentDetached;
            if (detached && (choice == SecretsConsentChoice.Yes
                || (requireOperation && choice is SecretsConsentChoice.Yes24Hours
                    or SecretsConsentChoice.YesAlways
                    or SecretsConsentChoice.Never)))
            {
                return request.Response;
            }

            SecretsPolicyRule? rule = null;
            SecretsRequestResponse? response = null;
            if (choice is SecretsConsentChoice.YesAlways or SecretsConsentChoice.Yes24Hours)
            {
                rule = SecretsPolicyRule.Create(
                    choice,
                    requireOperation
                        ? RememberedGrantScopeKind.ExactOperation
                        : RememberedGrantScopeKind.Client,
                    request.Scope,
                    now,
                    "native-ui");
                try
                {
                    var epoch = _policy.Add(rule);
                    response = detached
                        ? request.Response with
                        {
                            Status = SecretsRequestStatus.Denied,
                            Reservation = null,
                            ExpiresAt = now,
                        }
                        : TryAllowed(
                            attemptId,
                            request.Response.RequestId,
                            request.Scope,
                            epoch,
                            ReadOperatingMode(now).Epoch,
                            now);
                }
                catch (InvalidOperationException)
                {
                    rule = null;
                    response = request.Response with
                    {
                        Status = SecretsRequestStatus.QueueFull,
                        Reservation = null,
                        ExpiresAt = now,
                    };
                }
            }
            else if (choice == SecretsConsentChoice.Yes)
            {
                response = TryAllowed(
                    attemptId,
                    request.Response.RequestId,
                    request.Scope,
                    _policy.Read(now).Epoch,
                    ReadOperatingMode(now).Epoch,
                    now);
            }
            else
            {
                if (choice == SecretsConsentChoice.Never)
                {
                    rule = SecretsPolicyRule.Create(
                        choice,
                        requireOperation
                            ? RememberedGrantScopeKind.ExactOperation
                            : RememberedGrantScopeKind.Client,
                        request.Scope,
                        now,
                        "native-ui");
                    try
                    {
                        _policy.Add(rule);
                    }
                    catch (InvalidOperationException)
                    {
                        rule = null;
                        response = request.Response with
                        {
                            Status = SecretsRequestStatus.QueueFull,
                            Reservation = null,
                            ExpiresAt = now,
                        };
                    }
                }
                response ??= request.Response with
                {
                    Status = SecretsRequestStatus.Denied,
                    Reservation = null,
                    ExpiresAt = now,
                };
            }

            _requests[pair.Key] = request with
            {
                Response = response,
                Challenge = null,
                AuthorizingRuleId = rule is { Allow: true } ? rule.RuleId : null,
                AutoAllowed = false,
            };
            _audit.Append(OutcomeRecord(
                attemptId,
                now,
                request.Response.RequestId,
                request.Scope.Principal.ClientId,
                request.Scope.Project.ProjectReference,
                request.Scope.Aliases,
                request.Operation.Identity.Digest,
                response.Status,
                rule) with
            {
                Kind = SecretsAuditEventKind.ConsentDecision,
                Outcome = response.Status == SecretsRequestStatus.QueueFull
                    ? StatusName(response.Status)
                    : detached
                    ? choice is SecretsConsentChoice.YesAlways or SecretsConsentChoice.Yes24Hours
                        ? "allowed"
                        : choice == SecretsConsentChoice.Never
                            ? "denied"
                            : "dismissed"
                    : StatusName(response.Status),
            });
            return response;
        }
    }

    /// <summary>Consumes an allowed reservation and runs the exact frozen operation.</summary>
    public async Task<SecretsExecResult> RunAsync(
        string clientId,
        ReadOnlyMemory<byte> credential,
        string projectReference,
        string requestId,
        string reservation,
        IReadOnlyDictionary<string, string> callerEnvironment,
        CancellationToken cancellationToken,
        Action? authorizeProviderAccess = null,
        Action<Action>? coordinateFinalAuthorization = null,
        SecretsStandardStreams? streams = null)
    {
        ArgumentNullException.ThrowIfNull(callerEnvironment);
        var now = _time.GetUtcNow();
        var authentication = _clients.Authenticate(
            clientId,
            credential.Span,
            projectReference,
            SecretDeliveryMode.ExecInject)
            ?? throw new UnauthorizedAccessException("The local requester could not be authenticated.");
        RequestState request;
        RequestKey key;
        long authorizedPolicyEpoch;
        long authorizedOperatingModeEpoch;
        lock (_gate)
        {
            key = new RequestKey(
                authentication.Principal.RegistrationId,
                authentication.Principal.Generation,
                requestId);
            if (!_requests.TryGetValue(key, out request!)
                || request.Response.Status != SecretsRequestStatus.Allowed)
            {
                throw new InvalidOperationException("The request is not allowed for execution.");
            }
            if (authentication.Principal.RegistrationId != request.Scope.Principal.RegistrationId
                || authentication.Principal.Generation != request.Scope.Principal.Generation
                || authentication.Project != request.Scope.Project)
            {
                throw new UnauthorizedAccessException(
                    "The authenticated client and project do not own this request.");
            }
            var currentPolicy = _policy.Read(now);
            var currentOperatingMode = ReadOperatingMode(now);
            authorizedPolicyEpoch = currentPolicy.Epoch;
            authorizedOperatingModeEpoch = currentOperatingMode.Epoch;
            EnsureOperatingModeAuthorizes(request, currentOperatingMode);
            if (!request.AutoAllowed && request.AuthorizingRuleId is { } ruleId)
            {
                var evaluation = SecretsPolicyEvaluator.Evaluate(
                    request.Scope,
                    currentPolicy.Rules,
                    now);
                if (evaluation.Disposition != SecretsPolicyDisposition.Allowed
                    || evaluation.MatchedRule?.RuleId != ruleId)
                {
                    throw new InvalidOperationException(
                        "The remembered approval expired or no longer authorizes this request.");
                }
            }
            var consumed = request.Operation.Lifetime == SecretsExecutionLifetime.Detached
                ? SecretsReservationConsumeResult.Consumed : _reservations.Consume(
                reservation,
                authentication.Principal,
                requestId,
                request.Scope,
                currentPolicy.Epoch,
                now,
                currentOperatingMode.Epoch);
            if (consumed != SecretsReservationConsumeResult.Consumed)
            {
                throw new InvalidOperationException($"The reservation cannot be used: {consumed}.");
            }
        }

        try
        {
            authorizeProviderAccess?.Invoke();
            SecretValueLease values;
            try
            {
                EnsureProviderAliasesCurrent(request.Scope.Aliases);
                values = _provider.Fetch(request.Scope.Aliases.Select(alias => alias.Alias));
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException
                or InvalidOperationException
                or KeyNotFoundException)
            {
                throw new SecretsProviderUnavailableException(exception);
            }
            using (values)
            {
                var result = await _launcher.RunAsync(
                    request.Response.AttemptId,
                    requestId,
                    authentication.Principal.ClientId,
                    authentication.Project.ProjectReference,
                    request.Scope.Aliases.Select(alias => alias.Alias).ToArray(),
                    request.Operation,
                    values,
                    callerEnvironment,
                    cancellationToken,
                    authentication.Principal.RegistrationId,
                    authentication.Principal.Generation,
                    launchCommit => CommitAuthorizedLaunch(
                        authentication,
                        credential,
                        request,
                        authorizedPolicyEpoch,
                        authorizedOperatingModeEpoch,
                        cancellationToken,
                        launchCommit,
                        coordinateFinalAuthorization, reservation), streams).ConfigureAwait(false);
                SetTerminalStatus(key, result.Task?.State == "unknown"
                    ? SecretsRequestStatus.LaunchUnconfirmed : SecretsRequestStatus.Completed);
                return result;
            }
        }
        catch (Exception exception)
        {
            var unconfirmed = _audit.FindUnconfirmedLaunches().Contains(request.Response.AttemptId);
            SetTerminalStatus(
                key,
                unconfirmed
                    ? SecretsRequestStatus.LaunchUnconfirmed
                    : exception is SecretsProviderUnavailableException
                        ? SecretsRequestStatus.ProviderUnavailable
                        : SecretsRequestStatus.Revoked);
            throw;
        }
    }

    private void CommitAuthorizedLaunch(
        NamedClientAuthentication originalAuthentication,
        ReadOnlyMemory<byte> credential,
        RequestState request,
        long authorizedPolicyEpoch,
        long authorizedOperatingModeEpoch,
        CancellationToken cancellationToken,
        SecretsAuditRecord launchCommit,
        Action<Action>? coordinateFinalAuthorization,
        string reservation)
    {
        void Commit()
        {
            _clients.WithLock(() => _policy.WithLock(() =>
                WithOperatingModeSnapshotLock(_time.GetUtcNow(), operatingMode =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FinalAuthorization(
                        originalAuthentication,
                        credential,
                        request,
                        authorizedPolicyEpoch,
                        authorizedOperatingModeEpoch,
                        operatingMode);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (request.Operation.Lifetime == SecretsExecutionLifetime.Detached)
                    {
                        var consumed = _reservations.Consume(reservation, originalAuthentication.Principal,
                            request.Response.RequestId, request.Scope, authorizedPolicyEpoch, _time.GetUtcNow(), authorizedOperatingModeEpoch);
                        if (consumed != SecretsReservationConsumeResult.Consumed)
                            throw new InvalidOperationException("The detached reservation is no longer usable.");
                    }
                    _audit.Append(launchCommit);
                    return true;
                })));
        }

        if (coordinateFinalAuthorization is null) Commit();
        else coordinateFinalAuthorization(Commit);
    }

    private void SetTerminalStatus(RequestKey key, SecretsRequestStatus status)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_requests.TryGetValue(key, out var request)) return;
            if (request.Response.Status == status && request.Response.Reservation is null) return;
            _audit.Append(OutcomeRecord(
                request.Response.AttemptId,
                now,
                request.Response.RequestId,
                request.Scope.Principal.ClientId,
                request.Scope.Project.ProjectReference,
                request.Scope.Aliases,
                request.Operation.Identity.Digest,
                status,
                rule: null));
            _requests[key] = request with
            {
                Response = request.Response with
                {
                    Status = status,
                    Reservation = null,
                    ExpiresAt = now,
                },
            };
        }
    }

    private void FinalAuthorization(
        NamedClientAuthentication originalAuthentication,
        ReadOnlyMemory<byte> credential,
        RequestState request,
        long authorizedPolicyEpoch,
        long authorizedOperatingModeEpoch,
        SecretsOperatingModeSnapshot operatingMode)
    {
        var now = _time.GetUtcNow();
        var currentAuthentication = _clients.Authenticate(
            originalAuthentication.Principal.ClientId,
            credential.Span,
            originalAuthentication.Project.ProjectReference,
            SecretDeliveryMode.ExecInject);
        if (currentAuthentication is null
            || currentAuthentication.Principal != originalAuthentication.Principal
            || currentAuthentication.Project != originalAuthentication.Project
            || currentAuthentication.Principal != request.Scope.Principal
            || currentAuthentication.Project != request.Scope.Project)
        {
            throw new UnauthorizedAccessException(
                "The local requester or project changed before launch.");
        }

        var currentPolicy = _policy.Read(now);
        if (currentPolicy.Epoch != authorizedPolicyEpoch)
        {
            throw new InvalidOperationException("The secrets policy changed before launch.");
        }
        if (operatingMode.Epoch != authorizedOperatingModeEpoch)
        {
            throw new InvalidOperationException("The Secrets operating mode changed before launch.");
        }
        EnsureOperatingModeAuthorizes(request, operatingMode);
        if (!request.AutoAllowed && request.AuthorizingRuleId is { } ruleId)
        {
            var evaluation = SecretsPolicyEvaluator.Evaluate(
                request.Scope,
                currentPolicy.Rules,
                now);
            if (evaluation.Disposition != SecretsPolicyDisposition.Allowed
                || evaluation.MatchedRule?.RuleId != ruleId)
            {
                throw new InvalidOperationException(
                    "The remembered approval expired or no longer authorizes this request.");
            }
        }

        EnsureProviderAliasesCurrent(request.Scope.Aliases);
    }

    private bool ProviderAliasesAreCurrent(IEnumerable<SecretsAliasIdentity> aliases)
    {
        var providerAliases = _provider.ListAliases()
            .ToDictionary(alias => alias.Alias, StringComparer.Ordinal);
        foreach (var expected in aliases)
        {
            if (!string.Equals(expected.ProviderId, "env", StringComparison.Ordinal)
                || !providerAliases.TryGetValue(expected.Alias, out var actual)
                || !actual.Available
                || !string.Equals(actual.StableSecretId, expected.SecretId, StringComparison.Ordinal)
                || actual.MappingGeneration != expected.MappingGeneration)
            {
                return false;
            }
        }
        return true;
    }

    private SecretsOperatingModeSnapshot ReadOperatingMode(DateTimeOffset now) =>
        _operatingMode?.Read(now)
        ?? new(1, SecretsOperatingMode.Ask, DateTimeOffset.UnixEpoch, null);

    private TResult WithOperatingModeSnapshotLock<TResult>(
        DateTimeOffset now,
        Func<SecretsOperatingModeSnapshot, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _operatingMode is null
            ? action(ReadOperatingMode(now))
            : _operatingMode.WithSnapshotLock(now, action);
    }

    private static void EnsureOperatingModeAuthorizes(
        RequestState request,
        SecretsOperatingModeSnapshot operatingMode)
    {
        if (operatingMode.Mode == SecretsOperatingMode.DenyAll)
        {
            throw new InvalidOperationException("Secrets is denying all requests.");
        }
        if (request.AutoAllowed && operatingMode.Mode != SecretsOperatingMode.AutoAllow24Hours)
        {
            throw new InvalidOperationException("The temporary Secrets auto-allow window ended.");
        }
        if (!request.AutoAllowed && operatingMode.Mode != SecretsOperatingMode.Ask)
        {
            throw new InvalidOperationException("The Secrets operating mode changed before launch.");
        }
    }

    private void EnsureProviderAliasesCurrent(IEnumerable<SecretsAliasIdentity> aliases)
    {
        if (!ProviderAliasesAreCurrent(aliases))
        {
            throw new InvalidOperationException(
                "A secret alias is unavailable or its identity changed before delivery.");
        }
    }

    private SecretsRequestResponse Allowed(
        Guid attemptId,
        string requestId,
        SecretsAuthorizationScope scope,
        long policyEpoch,
        long operatingModeEpoch,
        DateTimeOffset now)
    {
        var expiresAt = now + ReservationLifetime;
        var reservation = _reservations.Reserve(
            scope.Principal,
            requestId,
            scope,
            policyEpoch,
            expiresAt,
            now,
            operatingModeEpoch);
        return new(SecretsRequestStatus.Allowed, requestId, attemptId, reservation, expiresAt);
    }

    private SecretsRequestResponse TryAllowed(
        Guid attemptId,
        string requestId,
        SecretsAuthorizationScope scope,
        long policyEpoch,
        long operatingModeEpoch,
        DateTimeOffset now)
    {
        try
        {
            return Allowed(
                attemptId,
                requestId,
                scope,
                policyEpoch,
                operatingModeEpoch,
                now);
        }
        catch (InvalidOperationException)
        {
            return new(
                SecretsRequestStatus.QueueFull,
                requestId,
                attemptId,
                null,
                now);
        }
    }

    private static SecretsRequestStatus CommittedStatus(
        IReadOnlyList<SecretsCommittedRequest> committed) =>
        committed.Any(item => item.Outcome == SecretsCommittedOutcome.LaunchUnconfirmed)
            ? SecretsRequestStatus.LaunchUnconfirmed
            : committed.Any(item => item.Outcome is SecretsCommittedOutcome.FailedBeforeLaunch
                or SecretsCommittedOutcome.Terminated)
                ? SecretsRequestStatus.Revoked
                : SecretsRequestStatus.Completed;

    private SecretsRequestResponse FinishAttempt(
        Guid attemptId,
        string requestId,
        string clientReference,
        string projectReference,
        IReadOnlyList<string> aliases,
        SecretsRequestStatus status,
        DateTimeOffset now,
        string? operationDigest)
    {
        _audit.Append(new(
            attemptId,
            SecretsAuditEventKind.RequestOutcome,
            now,
            requestId,
            clientReference,
            projectReference,
            aliases,
            operationDigest,
            StatusName(status)));
        return new(status, requestId, attemptId, null, now);
    }

    internal bool ContainsTrackedRequest(
        string clientId,
        string projectReference,
        string requestId)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            MaintainRequests(now);
            return _requests.Any(pair =>
                string.Equals(pair.Key.RequestId, requestId, StringComparison.Ordinal)
                && string.Equals(pair.Value.Scope.Principal.ClientId, clientId, StringComparison.Ordinal)
                && string.Equals(
                    pair.Value.Scope.Project.ProjectReference,
                    projectReference,
                    StringComparison.Ordinal));
        }
    }

    internal bool ContainsActiveRequest(
        string clientId,
        string projectReference,
        string requestId)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            MaintainRequests(now);
            return _requests.Any(pair =>
                string.Equals(pair.Key.RequestId, requestId, StringComparison.Ordinal)
                && string.Equals(pair.Value.Scope.Principal.ClientId, clientId, StringComparison.Ordinal)
                && string.Equals(
                    pair.Value.Scope.Project.ProjectReference,
                    projectReference,
                    StringComparison.Ordinal)
                && pair.Value.Response.Status is SecretsRequestStatus.Pending
                    or SecretsRequestStatus.AgentDetached
                    or SecretsRequestStatus.Allowed);
        }
    }

    internal void RevokeActiveRequests()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            MaintainRequests(now);
            foreach (var pair in _requests.ToArray())
            {
                if (pair.Value.Response.Status is not SecretsRequestStatus.Pending
                    and not SecretsRequestStatus.AgentDetached
                    and not SecretsRequestStatus.Allowed)
                {
                    continue;
                }
                _audit.Append(OutcomeRecord(
                    pair.Value.Response.AttemptId,
                    now,
                    pair.Value.Response.RequestId,
                    pair.Value.Scope.Principal.ClientId,
                    pair.Value.Scope.Project.ProjectReference,
                    pair.Value.Scope.Aliases,
                    pair.Value.Operation.Identity.Digest,
                    SecretsRequestStatus.Revoked,
                    rule: null));
                _requests[pair.Key] = pair.Value with
                {
                    Response = pair.Value.Response with
                    {
                        Status = SecretsRequestStatus.Revoked,
                        Reservation = null,
                        ExpiresAt = now,
                    },
                    Challenge = null,
                };
            }
        }
    }

    private void MaintainRequests(DateTimeOffset now)
    {
        _reservations.Prune(now);
        foreach (var pair in _requests.ToArray())
        {
            if (pair.Value.Response.ExpiresAt > now) continue;
            if (pair.Value.Response.Status is SecretsRequestStatus.Pending
                or SecretsRequestStatus.AgentDetached
                or SecretsRequestStatus.Allowed)
            {
                _audit.Append(OutcomeRecord(
                    pair.Value.Response.AttemptId,
                    now,
                    pair.Value.Response.RequestId,
                    pair.Value.Scope.Principal.ClientId,
                    pair.Value.Scope.Project.ProjectReference,
                    pair.Value.Scope.Aliases,
                    pair.Value.Operation.Identity.Digest,
                    SecretsRequestStatus.Expired,
                    rule: null));
                _requests[pair.Key] = pair.Value with
                {
                    Response = pair.Value.Response with
                    {
                        Status = SecretsRequestStatus.Expired,
                        ExpiresAt = now,
                    },
                    Challenge = null,
                };
            }
        }

        var removable = _requests
            .Where(pair => pair.Value.Response.Status is not SecretsRequestStatus.Pending
                and not SecretsRequestStatus.AgentDetached
                and not SecretsRequestStatus.Allowed)
            .OrderBy(pair => pair.Value.Response.ExpiresAt)
            .ToArray();
        foreach (var pair in removable.Where(pair =>
                     pair.Value.Response.ExpiresAt <= now - TerminalRequestRetention))
        {
            _requests.Remove(pair.Key);
        }
        foreach (var pair in removable)
        {
            if (_requests.Count <= MaximumTrackedRequests) break;
            _requests.Remove(pair.Key);
        }
    }

    private static SecretsAuditRecord OutcomeRecord(
        Guid attemptId,
        DateTimeOffset now,
        string requestId,
        string clientId,
        string projectReference,
        IEnumerable<SecretsAliasIdentity> aliases,
        string operationDigest,
        SecretsRequestStatus status,
        SecretsPolicyRule? rule) => new(
            attemptId,
            SecretsAuditEventKind.RequestOutcome,
            now,
            requestId,
            clientId,
            projectReference,
            aliases.Select(alias => alias.Alias).ToArray(),
            operationDigest,
            StatusName(status),
            rule?.RuleId,
            rule?.ScopeKind);

    private static bool ValidSubmission(SecretsRequestSubmission submission) =>
        !string.IsNullOrWhiteSpace(submission.RequestId)
        && submission.RequestId.Length <= 128
        && !string.IsNullOrWhiteSpace(submission.ClientId)
        && submission.ClientId.Length <= 64
        && !string.IsNullOrWhiteSpace(submission.ProjectReference)
        && submission.ProjectReference.Length <= 96
        && submission.Operation is not null
        && submission.Reason is not null
        && submission.Reason.Length <= 512;

    private static IReadOnlyList<string> SafeAliases(IEnumerable<string>? aliases) =>
        aliases?.Take(64).Select(alias => SafeIdentifier(alias, "invalid-alias")).ToArray() ?? [];

    private static string SafeIdentifier(string? value, string fallback) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 96
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? value
            : fallback;

    private static string BoundReason(string reason)
    {
        var normalized = new StringBuilder(Math.Min(reason.Length, 240));
        var previousWasSpace = true;
        foreach (var character in reason)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                if (!previousWasSpace && normalized.Length < 240) normalized.Append(' ');
                previousWasSpace = true;
                continue;
            }
            if (normalized.Length >= 240) break;
            normalized.Append(character);
            previousWasSpace = false;
        }
        var value = normalized.ToString().Trim();
        if (value.Length == 0) return "No reason supplied.";
        return value.Length == 240 && reason.Length > 240 ? value[..237] + "..." : value;
    }

    private static string CommandLineForDisplay(
        ResolvedExecOperation operation,
        out bool truncated)
    {
        truncated = false;
        var tokens = new List<string>();
        foreach (var argument in new[] { operation.Executable }.Concat(operation.Arguments))
        {
            var safe = new string(argument.Select(character =>
                char.IsControl(character) ? ' ' : character).ToArray());
            if (safe.Length > 256)
            {
                safe = safe[..253] + "...";
                truncated = true;
            }
            tokens.Add(safe.Length == 0 || safe.Any(char.IsWhiteSpace)
                ? '"' + safe.Replace("\"", "\\\"") + '"'
                : safe);
        }
        var commandLine = string.Join(' ', tokens);
        if (commandLine.Length <= 768) return commandLine;
        truncated = true;
        return commandLine[..765] + "...";
    }

    private static string StatusName(SecretsRequestStatus status) =>
        status.ToString().ToLowerInvariant();

    private static bool FixedEquals(string left, string right) =>
        left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record RequestKey(Guid RegistrationId, long Generation, string RequestId);

    private sealed record RequestState(
        SecretsAuthorizationScope Scope,
        ResolvedExecOperation Operation,
        SecretsRequestResponse Response,
        string? Challenge,
        string Reason,
        Guid? AuthorizingRuleId,
        bool AutoAllowed,
        IReadOnlyList<string> NewAliases,
        SecretsDetachReason? DetachedReason);
}

internal sealed class SecretsProviderUnavailableException(Exception innerException)
    : IOException("The configured secret provider is unavailable.", innerException);
