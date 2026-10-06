using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Joydex.Secrets;

/// <summary>The secret delivery modes that a local requester may use.</summary>
public enum SecretDeliveryMode
{
    ExecInject,
    RawRead,
}

/// <summary>The decisions available on a local secret-use request.</summary>
public enum SecretsConsentChoice
{
    YesAlways,
    Yes24Hours,
    Yes,
    No,
    Never,
}

/// <summary>The portion of an authorization scope retained by a remembered decision.</summary>
public enum RememberedGrantScopeKind
{
    ExactOperation,
    Client,
}

/// <summary>The result of matching a request against durable policy.</summary>
public enum SecretsPolicyDisposition
{
    ConsentRequired,
    Allowed,
    Denied,
}

/// <summary>Authenticated identity resolved from a protected same-user requester credential.</summary>
public sealed record SecretsClientPrincipal(
    Guid RegistrationId,
    string ClientId,
    long Generation,
    string DisplayLabel);

/// <summary>Canonical project and worktree identity captured on the requester's first use.</summary>
public sealed record SecretsProjectIdentity(
    string ProjectReference,
    string ProjectId,
    string CanonicalProjectRoot,
    string WorktreeId,
    string CanonicalWorktreeRoot,
    long Generation,
    string? CanonicalProjectRootIdentity = null,
    string? CanonicalWorktreeRootIdentity = null,
    string? WorktreeReference = null);

/// <summary>Captures and rechecks the directory identities used by local requesters.</summary>
public static class SecretsProjectIdentityFactory
{
    public static SecretsProjectIdentity Create(
        string projectReference,
        string root,
        long generation = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!IsIdentifier(projectReference, 96))
        {
            throw new ArgumentException(
                "The project name may contain letters, numbers, hyphens, underscores, and periods.",
                nameof(projectReference));
        }
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var rootIdentity = CaptureDirectoryIdentity(fullRoot);
        var worktreeReference = Path.GetFileName(fullRoot);
        return new(
            projectReference,
            Digest("project:" + rootIdentity),
            fullRoot,
            Digest("worktree:" + rootIdentity),
            fullRoot,
            generation,
            rootIdentity,
            rootIdentity,
            string.IsNullOrWhiteSpace(worktreeReference)
                ? projectReference
                : worktreeReference[..Math.Min(worktreeReference.Length, 96)]);
    }

    public static bool IsCurrent(SecretsProjectIdentity project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.CanonicalProjectRootIdentity is null
            && project.CanonicalWorktreeRootIdentity is null)
        {
            return true;
        }
        if (!IsDigest(project.CanonicalProjectRootIdentity)
            || !IsDigest(project.CanonicalWorktreeRootIdentity))
        {
            return false;
        }
        try
        {
            return string.Equals(
                    CaptureDirectoryIdentity(project.CanonicalProjectRoot),
                    project.CanonicalProjectRootIdentity,
                    StringComparison.Ordinal)
                && string.Equals(
                    CaptureDirectoryIdentity(project.CanonicalWorktreeRoot),
                    project.CanonicalWorktreeRootIdentity,
                    StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string CaptureDirectoryIdentity(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
        {
            throw new ArgumentException("The project folder does not exist.", nameof(path));
        }
        ExecOperationCanonicalizer.RejectReparsePoints(fullPath, "project", isDirectory: true);
        return Digest(WindowsDirectoryHandle.GetIdentity(fullPath));
    }

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsIdentifier(string value, int maximum) =>
        value.Length <= maximum
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsDigest(string? value) => value is not null
        && value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>A public alias resolved to a stable provider secret without containing its value.</summary>
public sealed record SecretsAliasIdentity(
    string Alias,
    string ProviderId,
    string SecretId,
    long MappingGeneration);

/// <summary>A broker-resolved operation whose digest changes whenever executable behavior changes.</summary>
public sealed record SecretsOperationIdentity(
    SecretDeliveryMode DeliveryMode,
    string Digest,
    long Generation,
    SecretsExecutionLifetime Lifetime = SecretsExecutionLifetime.Attached);

/// <summary>
/// Complete authorization identity. Exact and client digests are computed by the broker and
/// exclude caller prose.
/// </summary>
public sealed record SecretsAuthorizationScope(
    SecretsClientPrincipal Principal,
    SecretsProjectIdentity Project,
    IReadOnlyList<SecretsAliasIdentity> Aliases,
    SecretsOperationIdentity Operation,
    string ExactScopeDigest,
    string ClientScopeDigest);

/// <summary>Builds deterministic authorization identities from already authenticated metadata.</summary>
public static class SecretsScopeFactory
{
    /// <summary>Creates a validated, deterministic scope for policy matching and deduplication.</summary>
    public static SecretsAuthorizationScope Create(
        SecretsClientPrincipal principal,
        SecretsProjectIdentity project,
        IEnumerable<SecretsAliasIdentity> aliases,
        SecretsOperationIdentity operation)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(operation);
        ValidatePrincipal(principal);
        ValidateProject(project);
        ValidateOperation(operation);

        var canonicalAliases = aliases.OrderBy(alias => alias.Alias, StringComparer.Ordinal).ToArray();
        if (canonicalAliases.Length is < 1 or > 64)
        {
            throw new ArgumentException("A secrets request must contain 1 to 64 aliases.", nameof(aliases));
        }

        for (var index = 0; index < canonicalAliases.Length; index++)
        {
            ValidateAlias(canonicalAliases[index]);
            if (index > 0 && string.Equals(
                    canonicalAliases[index - 1].Alias,
                    canonicalAliases[index].Alias,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException("A secrets request cannot repeat an alias.", nameof(aliases));
            }
        }

        var clientDigest = ComputeDigest(principal, project, canonicalAliases, operation.DeliveryMode, operation.Lifetime);
        var exactDigest = ComputeDigest(principal, project, canonicalAliases, operation.DeliveryMode, operation.Lifetime, operation);
        return new SecretsAuthorizationScope(
            principal,
            project,
            Array.AsReadOnly(canonicalAliases),
            operation,
            exactDigest,
            clientDigest);
    }

    private static void ValidatePrincipal(SecretsClientPrincipal principal)
    {
        if (principal.RegistrationId == Guid.Empty
            || !IsIdentifier(principal.ClientId, 64)
            || principal.Generation < 1
            || string.IsNullOrWhiteSpace(principal.DisplayLabel)
            || principal.DisplayLabel.Length > 96)
        {
            throw new ArgumentException("The authenticated client principal is invalid.", nameof(principal));
        }
    }

    internal static void ValidateProject(SecretsProjectIdentity project)
    {
        if (!IsIdentifier(project.ProjectReference, 96)
            || string.IsNullOrWhiteSpace(project.ProjectId)
            || project.ProjectId.Length > 128
            || string.IsNullOrWhiteSpace(project.CanonicalProjectRoot)
            || !Path.IsPathFullyQualified(project.CanonicalProjectRoot)
            || !IsIdentifier(project.WorktreeId, 128)
            || string.IsNullOrWhiteSpace(project.CanonicalWorktreeRoot)
            || !Path.IsPathFullyQualified(project.CanonicalWorktreeRoot)
            || project.Generation < 1
            || (project.WorktreeReference is not null
                && (string.IsNullOrWhiteSpace(project.WorktreeReference)
                    || project.WorktreeReference.Length > 96
                    || project.WorktreeReference.Any(char.IsControl)))
            || (project.CanonicalProjectRootIdentity is not null
                && !IsLowerHexDigest(project.CanonicalProjectRootIdentity))
            || (project.CanonicalWorktreeRootIdentity is not null
                && !IsLowerHexDigest(project.CanonicalWorktreeRootIdentity)))
        {
            throw new ArgumentException("The canonical project identity is invalid.", nameof(project));
        }
    }

    private static void ValidateAlias(SecretsAliasIdentity alias)
    {
        if (!IsIdentifier(alias.Alias, 96)
            || !IsIdentifier(alias.ProviderId, 96)
            || !IsIdentifier(alias.SecretId, 160)
            || alias.MappingGeneration < 1)
        {
            throw new ArgumentException("A resolved secret alias is invalid.", nameof(alias));
        }
    }

    private static void ValidateOperation(SecretsOperationIdentity operation)
    {
        if (!Enum.IsDefined(operation.DeliveryMode)
            || !Enum.IsDefined(operation.Lifetime)
            || !IsLowerHexDigest(operation.Digest)
            || operation.Generation < 1)
        {
            throw new ArgumentException("The resolved operation identity is invalid.", nameof(operation));
        }
    }

    private static string ComputeDigest(
        SecretsClientPrincipal principal,
        SecretsProjectIdentity project,
        IReadOnlyList<SecretsAliasIdentity> aliases,
        SecretDeliveryMode delivery,
        SecretsExecutionLifetime lifetime,
        SecretsOperationIdentity? operation = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("registrationId", principal.RegistrationId);
            writer.WriteNumber("clientGeneration", principal.Generation);
            writer.WriteString("projectId", project.ProjectId);
            writer.WriteString("projectRoot", NormalizePath(project.CanonicalProjectRoot));
            writer.WriteString("worktreeId", project.WorktreeId);
            writer.WriteString("worktreeRoot", NormalizePath(project.CanonicalWorktreeRoot));
            writer.WriteNumber("projectGeneration", project.Generation);
            if (project.CanonicalProjectRootIdentity is not null)
            {
                writer.WriteString("projectRootIdentity", project.CanonicalProjectRootIdentity);
            }
            if (project.CanonicalWorktreeRootIdentity is not null)
            {
                writer.WriteString("worktreeRootIdentity", project.CanonicalWorktreeRootIdentity);
            }
            writer.WriteString("deliveryMode", delivery.ToString());
            if (lifetime == SecretsExecutionLifetime.Detached) writer.WriteString("executionLifetime", "detached-v1");
            writer.WriteStartArray("aliases");
            foreach (var alias in aliases)
            {
                writer.WriteStartObject();
                writer.WriteString("alias", alias.Alias);
                writer.WriteString("providerId", alias.ProviderId);
                writer.WriteString("secretId", alias.SecretId);
                writer.WriteNumber("mappingGeneration", alias.MappingGeneration);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (operation is not null)
            {
                writer.WriteString("operationDigest", operation.Digest);
                writer.WriteNumber("operationGeneration", operation.Generation);
            }
            writer.WriteEndObject();
        }
        var digest = SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();

    private static bool IsLowerHexDigest(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsIdentifier(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

/// <summary>A persistent authorization rule containing no secret values or caller prose.</summary>
public sealed record SecretsPolicyRule(
    Guid RuleId,
    bool Allow,
    RememberedGrantScopeKind ScopeKind,
    string ScopeDigest,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt,
    string Source,
    string? ClientReference = null,
    string? ClientLabel = null,
    string? ProjectReference = null,
    IReadOnlyList<string>? Aliases = null)
{
    /// <summary>Creates the durable rule implied by a remembered consent choice.</summary>
    public static SecretsPolicyRule Create(
        SecretsConsentChoice choice,
        RememberedGrantScopeKind requestedScope,
        SecretsAuthorizationScope scope,
        DateTimeOffset now,
        string source)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return choice switch
        {
            SecretsConsentChoice.YesAlways => NewAllow(requestedScope, scope, now, null, source),
            SecretsConsentChoice.Yes24Hours => NewAllow(requestedScope, scope, now, now.AddHours(24), source),
            SecretsConsentChoice.Never => new(
                Guid.NewGuid(),
                false,
                requestedScope,
                requestedScope == RememberedGrantScopeKind.ExactOperation
                    ? scope.ExactScopeDigest
                    : scope.ClientScopeDigest,
                now,
                null,
                source,
                scope.Principal.ClientId,
                scope.Principal.DisplayLabel,
                scope.Project.ProjectReference,
                scope.Aliases.Select(alias => alias.Alias).ToArray()),
            _ => throw new ArgumentException("The consent choice does not create a persistent rule.", nameof(choice)),
        };
    }

    private static SecretsPolicyRule NewAllow(
        RememberedGrantScopeKind scopeKind,
        SecretsAuthorizationScope scope,
        DateTimeOffset issuedAt,
        DateTimeOffset? expiresAt,
        string source) => new(
            Guid.NewGuid(),
            true,
            scopeKind,
            scopeKind == RememberedGrantScopeKind.ExactOperation
                ? scope.ExactScopeDigest
                : scope.ClientScopeDigest,
            issuedAt,
            expiresAt,
            source,
            scope.Principal.ClientId,
            scope.Principal.DisplayLabel,
            scope.Project.ProjectReference,
            scope.Aliases.Select(alias => alias.Alias).ToArray());
}

/// <summary>Policy match with the durable rule that produced the result, when present.</summary>
public sealed record SecretsPolicyEvaluation(
    SecretsPolicyDisposition Disposition,
    SecretsPolicyRule? MatchedRule);

/// <summary>Matches remembered denies before operation-bound or client-scoped allows.</summary>
public static class SecretsPolicyEvaluator
{
    /// <summary>Evaluates a request against non-expired rules at a fixed point in time.</summary>
    public static SecretsPolicyEvaluation Evaluate(
        SecretsAuthorizationScope scope,
        IEnumerable<SecretsPolicyRule> rules,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(rules);
        var active = rules.Where(rule => rule.ExpiresAt is null || rule.ExpiresAt > now).ToArray();

        var deny = active.FirstOrDefault(rule => !rule.Allow && Matches(rule, scope));
        if (deny is not null)
        {
            return new(SecretsPolicyDisposition.Denied, deny);
        }

        var allow = active.FirstOrDefault(rule => rule.Allow && Matches(rule, scope));
        return allow is null
            ? new(SecretsPolicyDisposition.ConsentRequired, null)
            : new(SecretsPolicyDisposition.Allowed, allow);
    }

    private static bool Matches(SecretsPolicyRule rule, SecretsAuthorizationScope scope)
    {
        var expected = rule.ScopeKind == RememberedGrantScopeKind.ExactOperation
            ? scope.ExactScopeDigest
            : scope.ClientScopeDigest;
        if (rule.ScopeDigest.Length != expected.Length)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(rule.ScopeDigest),
            Encoding.ASCII.GetBytes(expected));
    }
}

/// <summary>Result of registering a caller request ID for authenticated deduplication.</summary>
public enum SecretsRequestRegistrationResult
{
    Added,
    Existing,
    Conflict,
}

/// <summary>Deduplicates request IDs inside an authenticated client generation.</summary>
public sealed class SecretsRequestIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<RequestKey, string> _digests = [];

    /// <summary>
    /// Adds a request, joins an identical retry, or rejects reuse with changed authority fields.
    /// </summary>
    public SecretsRequestRegistrationResult Register(
        SecretsClientPrincipal principal,
        string requestId,
        SecretsAuthorizationScope scope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(scope);
        if (requestId.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId));
        }

        var key = new RequestKey(principal.RegistrationId, principal.Generation, requestId);
        lock (_gate)
        {
            if (!_digests.TryGetValue(key, out var existing))
            {
                _digests.Add(key, scope.ExactScopeDigest);
                return SecretsRequestRegistrationResult.Added;
            }
            return string.Equals(existing, scope.ExactScopeDigest, StringComparison.Ordinal)
                ? SecretsRequestRegistrationResult.Existing
                : SecretsRequestRegistrationResult.Conflict;
        }
    }

    private sealed record RequestKey(Guid RegistrationId, long Generation, string RequestId);
}
