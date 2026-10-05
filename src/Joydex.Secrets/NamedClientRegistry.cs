using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Secrets;

/// <summary>A local requester identity and the projects and delivery modes it may request.</summary>
public sealed record NamedClientRegistration(
    SecretsClientPrincipal Principal,
    IReadOnlyList<SecretsProjectIdentity> Projects,
    IReadOnlyList<SecretDeliveryMode> AllowedDeliveryModes,
    byte[] CredentialHash,
    bool Revoked = false);

/// <summary>Successful authentication resolved entirely from broker-owned requester metadata.</summary>
public sealed record NamedClientAuthentication(
    SecretsClientPrincipal Principal,
    SecretsProjectIdentity Project);

/// <summary>Persistent local-requester registry. It stores a one-way credential verifier.</summary>
public sealed class NamedClientRegistry
{
    public const int CurrentSchemaVersion = 1;
    private const int CredentialBytes = 32;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
    private readonly object _gate = new();
    private readonly string _path;
    private List<NamedClientRegistration> _registrations;

    internal TResult WithLock<TResult>(Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            using var fileLock = SecretsFileLock.Acquire(_path);
            return action();
        }
    }

    /// <summary>Loads a registry from an explicit path, failing closed if existing data is corrupt.</summary>
    public NamedClientRegistry(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        using var fileLock = SecretsFileLock.Acquire(_path);
        _registrations = LoadDocument(_path);
    }

    /// <summary>Creates a random credential and persists an explicit requester registration.</summary>
    public NamedClientEnrollment Enroll(
        string clientId,
        string displayLabel,
        IEnumerable<SecretsProjectIdentity> projects,
        IEnumerable<SecretDeliveryMode> allowedDeliveryModes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayLabel);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(allowedDeliveryModes);
        var projectArray = projects.ToArray();
        var modeArray = allowedDeliveryModes.Distinct().ToArray();
        ValidateEnrollment(clientId, displayLabel, projectArray, modeArray);

        var credential = RandomNumberGenerator.GetBytes(CredentialBytes);
        try
        {
            lock (_gate)
            {
                using var fileLock = SecretsFileLock.Acquire(_path);
                _registrations = LoadDocument(_path);
                var existingIndex = _registrations.FindIndex(candidate => string.Equals(
                    candidate.Principal.ClientId,
                    clientId,
                    StringComparison.Ordinal));
                var existing = existingIndex < 0 ? null : _registrations[existingIndex];
                if (existing is { Revoked: false })
                {
                    throw new InvalidOperationException($"Local requester '{clientId}' is already registered.");
                }

                var principal = new SecretsClientPrincipal(
                    Guid.NewGuid(),
                    clientId,
                    existing is null ? 1 : checked(existing.Principal.Generation + 1),
                    displayLabel);
                var registration = new NamedClientRegistration(
                    principal,
                    projectArray,
                    modeArray,
                    ComputeCredentialHash(principal.RegistrationId, credential));
                var updated = _registrations.ToList();
                if (existingIndex < 0) updated.Add(registration);
                else updated[existingIndex] = registration;
                SaveDocument(_path, updated);
                _registrations = updated;
                return new NamedClientEnrollment(principal, credential.ToArray());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    /// <summary>
    /// Creates or refreshes the hidden local identity used by the helper on first request.
    /// Existing projects and credential generations remain stable when nothing changed.
    /// </summary>
    public NamedClientRegistration EnsureLocalRequester(
        string clientId,
        string displayLabel,
        SecretsProjectIdentity project,
        SecretDeliveryMode deliveryMode,
        ReadOnlySpan<byte> credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayLabel);
        ArgumentNullException.ThrowIfNull(project);
        if (credential.Length != CredentialBytes)
        {
            throw new ArgumentException("A local-requester credential must contain 32 bytes.", nameof(credential));
        }
        ValidateEnrollment(clientId, displayLabel, [project], [deliveryMode]);

        lock (_gate)
        {
            using var fileLock = SecretsFileLock.Acquire(_path);
            _registrations = LoadDocument(_path);
            var existingIndex = _registrations.FindIndex(candidate => string.Equals(
                candidate.Principal.ClientId,
                clientId,
                StringComparison.Ordinal));
            var existing = existingIndex < 0 ? null : _registrations[existingIndex];
            var credentialMatches = existing is not null && CredentialMatches(existing, credential);
            var principal = existing is { Revoked: false } && credentialMatches
                ? existing.Principal with { DisplayLabel = displayLabel }
                : new SecretsClientPrincipal(
                    Guid.NewGuid(),
                    clientId,
                    existing is null ? 1 : checked(existing.Principal.Generation + 1),
                    displayLabel);
            var projects = UpsertProject(existing?.Projects ?? [], project);
            var modes = (existing?.AllowedDeliveryModes ?? [])
                .Append(deliveryMode)
                .Distinct()
                .ToArray();
            var registration = new NamedClientRegistration(
                principal,
                projects,
                modes,
                ComputeCredentialHash(principal.RegistrationId, credential));
            ValidateEnrollment(clientId, displayLabel, projects.ToArray(), modes);
            var updated = _registrations.ToList();
            if (existingIndex < 0) updated.Add(registration);
            else updated[existingIndex] = registration;
            SaveDocument(_path, updated);
            _registrations = updated;
            return registration;
        }
    }

    /// <summary>Authenticates one requester generation and resolves its project reference.</summary>
    public NamedClientAuthentication? Authenticate(
        string clientId,
        ReadOnlySpan<byte> credential,
        string projectReference,
        SecretDeliveryMode deliveryMode)
    {
        if (string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(projectReference)
            || credential.Length != CredentialBytes)
        {
            return null;
        }

        lock (_gate)
        {
            using var fileLock = SecretsFileLock.Acquire(_path);
            _registrations = LoadDocument(_path);
            var registration = _registrations.SingleOrDefault(candidate => string.Equals(
                candidate.Principal.ClientId,
                clientId,
                StringComparison.Ordinal));
            if (registration is null
                || registration.Revoked
                || !registration.AllowedDeliveryModes.Contains(deliveryMode))
            {
                return null;
            }

            var actual = ComputeCredentialHash(registration.Principal.RegistrationId, credential);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(actual, registration.CredentialHash))
                {
                    return null;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
            }

            var project = registration.Projects.SingleOrDefault(candidate => string.Equals(
                candidate.ProjectReference,
                projectReference,
                StringComparison.Ordinal));
            return project is null || !SecretsProjectIdentityFactory.IsCurrent(project)
                ? null
                : new NamedClientAuthentication(registration.Principal, project);
        }
    }

    /// <summary>Revokes a requester registration and advances its generation atomically.</summary>
    public void Revoke(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        lock (_gate)
        {
            using var fileLock = SecretsFileLock.Acquire(_path);
            _registrations = LoadDocument(_path);
            var index = _registrations.FindIndex(candidate => string.Equals(
                candidate.Principal.ClientId,
                clientId,
                StringComparison.Ordinal));
            if (index < 0) throw new KeyNotFoundException($"Local requester '{clientId}' is not registered.");
            var current = _registrations[index];
            if (current.Revoked) return;
            var principal = current.Principal with { Generation = checked(current.Principal.Generation + 1) };
            var updated = _registrations.ToList();
            updated[index] = current with { Principal = principal, Revoked = true };
            SaveDocument(_path, updated);
            _registrations = updated;
        }
    }

    /// <summary>Returns requester metadata without credential verifiers.</summary>
    public IReadOnlyList<NamedClientMetadata> List()
    {
        lock (_gate)
        {
            using var fileLock = SecretsFileLock.Acquire(_path);
            _registrations = LoadDocument(_path);
            return _registrations.Select(registration => new NamedClientMetadata(
                registration.Principal.ClientId,
                registration.Principal.DisplayLabel,
                registration.Principal.Generation,
                registration.Revoked,
                registration.Projects.Select(project => project.ProjectReference).ToArray(),
                registration.AllowedDeliveryModes.ToArray())).ToArray();
        }
    }

    /// <summary>Lists canonical projects from active requester records for advanced recipe tooling.</summary>
    public IReadOnlyList<SecretsProjectIdentity> ListProjects()
    {
        lock (_gate)
        {
            using var fileLock = SecretsFileLock.Acquire(_path);
            _registrations = LoadDocument(_path);
            return _registrations
                .Where(registration => !registration.Revoked)
                .SelectMany(registration => registration.Projects)
                .Where(SecretsProjectIdentityFactory.IsCurrent)
                .GroupBy(project => project.ProjectReference, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(project => project.ProjectReference, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private static byte[] ComputeCredentialHash(Guid registrationId, ReadOnlySpan<byte> credential)
    {
        Span<byte> registrationBytes = stackalloc byte[16];
        registrationId.TryWriteBytes(registrationBytes);
        Span<byte> material = stackalloc byte[16 + CredentialBytes];
        registrationBytes.CopyTo(material);
        credential.CopyTo(material[16..]);
        var hash = SHA256.HashData(material);
        CryptographicOperations.ZeroMemory(material);
        return hash;
    }

    private static bool CredentialMatches(
        NamedClientRegistration registration,
        ReadOnlySpan<byte> credential)
    {
        var actual = ComputeCredentialHash(registration.Principal.RegistrationId, credential);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actual, registration.CredentialHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static IReadOnlyList<SecretsProjectIdentity> UpsertProject(
        IReadOnlyList<SecretsProjectIdentity> existing,
        SecretsProjectIdentity requested)
    {
        var projects = existing.ToList();
        var index = projects.FindIndex(candidate => string.Equals(
            candidate.ProjectReference,
            requested.ProjectReference,
            StringComparison.Ordinal));
        if (index < 0)
        {
            if (projects.Count >= 32)
            {
                throw new InvalidOperationException("This local requester already has the maximum number of projects.");
            }
            projects.Add(requested);
            return projects;
        }

        var current = projects[index];
        var sameIdentity = string.Equals(current.ProjectId, requested.ProjectId, StringComparison.Ordinal)
            && string.Equals(current.WorktreeId, requested.WorktreeId, StringComparison.Ordinal)
            && string.Equals(
                current.CanonicalProjectRootIdentity,
                requested.CanonicalProjectRootIdentity,
                StringComparison.Ordinal)
            && string.Equals(
                current.CanonicalWorktreeRootIdentity,
                requested.CanonicalWorktreeRootIdentity,
                StringComparison.Ordinal);
        projects[index] = sameIdentity
            ? current
            : requested with { Generation = checked(current.Generation + 1) };
        return projects;
    }

    private static void ValidateEnrollment(
        string clientId,
        string displayLabel,
        SecretsProjectIdentity[] projects,
        SecretDeliveryMode[] modes)
    {
        foreach (var project in projects) SecretsScopeFactory.ValidateProject(project);
        if (clientId.Length > 64
            || !clientId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            || displayLabel.Length > 96
            || projects.Length is < 1 or > 32
            || projects.Select(project => project.ProjectReference).Distinct(StringComparer.Ordinal).Count()
                != projects.Length
            || modes.Length is < 1 or > 2
            || modes.Any(mode => !Enum.IsDefined(mode)))
        {
            throw new ArgumentException("The local requester registration is invalid.");
        }
    }

    private static List<NamedClientRegistration> LoadDocument(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<RegistryDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("The named-client registry is empty.");
            if (document.SchemaVersion != CurrentSchemaVersion || document.Registrations is null)
            {
                throw new InvalidDataException("The named-client registry schema is unsupported.");
            }
            foreach (var registration in document.Registrations)
            {
                ValidateEnrollment(
                    registration.Principal.ClientId,
                    registration.Principal.DisplayLabel,
                    registration.Projects.ToArray(),
                    registration.AllowedDeliveryModes.Distinct().ToArray());
                if (registration.Principal.RegistrationId == Guid.Empty
                    || registration.Principal.Generation < 1
                    || registration.CredentialHash.Length != 32
                    || registration.Projects.Count is < 1 or > 32
                    || registration.AllowedDeliveryModes.Count is < 1 or > 2)
                {
                    throw new InvalidDataException("The named-client registry contains an invalid registration.");
                }
            }
            if (document.Registrations.Select(registration => registration.Principal.ClientId)
                    .Distinct(StringComparer.Ordinal).Count() != document.Registrations.Count
                || document.Registrations.Select(registration => registration.Principal.RegistrationId)
                    .Distinct().Count() != document.Registrations.Count)
            {
                throw new InvalidDataException("The named-client registry repeats a client identity.");
            }
            return document.Registrations;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The named-client registry is malformed.", exception);
        }
    }

    private static void SaveDocument(string path, IReadOnlyList<NamedClientRegistration> registrations) =>
        SecretsAtomicFile.WriteJson(path, new RegistryDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Registrations = registrations.ToList(),
        }, JsonOptions);

    private sealed class RegistryDocument
    {
        public int SchemaVersion { get; init; }
        public List<NamedClientRegistration>? Registrations { get; init; }
    }
}

/// <summary>A one-time explicit registration result. Dispose it after saving the helper credential.</summary>
public sealed class NamedClientEnrollment : IDisposable
{
    private byte[]? _credential;

    internal NamedClientEnrollment(SecretsClientPrincipal principal, byte[] credential)
    {
        Principal = principal;
        _credential = credential;
    }

    public SecretsClientPrincipal Principal { get; }

    /// <summary>Copies the random credential so a protected helper store can persist it.</summary>
    public byte[] CopyCredential()
    {
        ObjectDisposedException.ThrowIf(_credential is null, this);
        return _credential.ToArray();
    }

    public void Dispose()
    {
        if (_credential is not null) CryptographicOperations.ZeroMemory(_credential);
        _credential = null;
    }
}

/// <summary>Requester registration information without credential verifier material.</summary>
public sealed record NamedClientMetadata(
    string ClientId,
    string DisplayLabel,
    long Generation,
    bool Revoked,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<SecretDeliveryMode> AllowedDeliveryModes);
