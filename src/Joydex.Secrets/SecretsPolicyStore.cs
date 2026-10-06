using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Secrets;

/// <summary>Atomic broker-owned storage for remembered decisions and its revocation epoch.</summary>
public sealed class SecretsPolicyStore
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumPolicyRules = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
    private readonly object _gate = new();
    private readonly string _path;
    private PolicyDocument _document;

    internal TResult WithLock<TResult>(Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate) return SecretsFileLock.WithLock(_path, action);
    }

    /// <summary>Loads policy from an explicit path, failing closed if existing data is corrupt.</summary>
    public SecretsPolicyStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _document = SecretsFileLock.WithLock(_path, () => Load(_path));
    }

    /// <summary>Gets the current epoch and active rules at a fixed point in time.</summary>
    public SecretsPolicySnapshot Read(DateTimeOffset now)
    {
        lock (_gate)
        {
            return SecretsFileLock.WithLock(_path, () =>
            {
                _document = Load(_path);
                var active = _document.Rules
                    .Where(rule => rule.ExpiresAt is null || rule.ExpiresAt > now)
                    .ToList();
                if (active.Count != _document.Rules.Count)
                {
                    _document = _document with
                    {
                        Epoch = checked(_document.Epoch + 1),
                        Rules = active,
                    };
                    Save(_document);
                }
                return new SecretsPolicySnapshot(
                    _document.Epoch,
                    _document.Rules.ToArray());
            });
        }
    }

    /// <summary>Adds a durable rule and advances the epoch before returning.</summary>
    public long Add(SecretsPolicyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ValidateRule(rule);
        lock (_gate)
        {
            return SecretsFileLock.WithLock(_path, () =>
            {
                _document = Load(_path);
                var retained = _document.Rules
                    .Where(candidate => candidate.ExpiresAt is null
                        || candidate.ExpiresAt > rule.IssuedAt)
                    .Where(candidate => candidate.Allow != rule.Allow
                        || candidate.ScopeKind != rule.ScopeKind
                        || !string.Equals(candidate.ScopeDigest, rule.ScopeDigest, StringComparison.Ordinal))
                    .ToList();
                if (retained.Count >= MaximumPolicyRules)
                {
                    throw new InvalidOperationException("The Secrets remembered-decision limit has been reached.");
                }
                var next = _document with
                {
                    Epoch = checked(_document.Epoch + 1),
                    Rules = retained.Append(rule).ToList(),
                };
                Save(next);
                _document = next;
                return next.Epoch;
            });
        }
    }

    /// <summary>Revokes one rule and advances the epoch if it existed.</summary>
    public bool Revoke(Guid ruleId)
    {
        if (ruleId == Guid.Empty) return false;
        lock (_gate)
        {
            return SecretsFileLock.WithLock(_path, () =>
            {
                _document = Load(_path);
                var rules = _document.Rules.Where(rule => rule.RuleId != ruleId).ToList();
                if (rules.Count == _document.Rules.Count) return false;
                var next = _document with { Epoch = checked(_document.Epoch + 1), Rules = rules };
                Save(next);
                _document = next;
                return true;
            });
        }
    }

    /// <summary>Removes expired rules and advances the epoch only when maintenance changed policy.</summary>
    public int PruneExpired(DateTimeOffset now)
    {
        lock (_gate)
        {
            return SecretsFileLock.WithLock(_path, () =>
            {
                _document = Load(_path);
                var rules = _document.Rules
                    .Where(rule => rule.ExpiresAt is null || rule.ExpiresAt > now)
                    .ToList();
                var removed = _document.Rules.Count - rules.Count;
                if (removed == 0) return 0;
                var next = _document with { Epoch = checked(_document.Epoch + 1), Rules = rules };
                Save(next);
                _document = next;
                return removed;
            });
        }
    }

    private void Save(PolicyDocument document) => SecretsAtomicFile.WriteJson(_path, document, JsonOptions);

    private static PolicyDocument Load(string path)
    {
        if (!File.Exists(path)) return new(CurrentSchemaVersion, 1, []);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<PolicyDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("The secrets policy is empty.");
            if (document.SchemaVersion != CurrentSchemaVersion
                || document.Epoch < 1
                || document.Rules is null
                || document.Rules.Count > MaximumPolicyRules)
            {
                throw new InvalidDataException("The secrets policy schema is unsupported.");
            }
            foreach (var rule in document.Rules) ValidateRule(rule);
            if (document.Rules.Select(rule => rule.RuleId).Distinct().Count() != document.Rules.Count)
            {
                throw new InvalidDataException("The secrets policy repeats a rule ID.");
            }
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The secrets policy is malformed.", exception);
        }
    }

    private static void ValidateRule(SecretsPolicyRule rule)
    {
        var legacyMetadata = rule.ClientReference is null
            && rule.ClientLabel is null
            && rule.ProjectReference is null
            && rule.Aliases is null;
        var completeMetadata = IsIdentifier(rule.ClientReference, 64)
            && !string.IsNullOrWhiteSpace(rule.ClientLabel)
            && rule.ClientLabel.Length <= 96
            && IsIdentifier(rule.ProjectReference, 96)
            && rule.Aliases is { Count: >= 1 and <= 64 }
            && rule.Aliases.All(alias => IsIdentifier(alias, 96))
            && rule.Aliases.Distinct(StringComparer.Ordinal).Count() == rule.Aliases.Count;
        if (rule.RuleId == Guid.Empty
            || !Enum.IsDefined(rule.ScopeKind)
            || rule.ScopeDigest.Length != 64
            || rule.ScopeDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || rule.ExpiresAt <= rule.IssuedAt
            || string.IsNullOrWhiteSpace(rule.Source)
            || rule.Source.Length > 64
            || !legacyMetadata && !completeMetadata)
        {
            throw new InvalidDataException("The secrets policy contains an invalid rule.");
        }
    }

    private static bool IsIdentifier(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private sealed record PolicyDocument(
        int SchemaVersion,
        long Epoch,
        List<SecretsPolicyRule> Rules);
}

/// <summary>A consistent view used to evaluate and later recheck one delivery.</summary>
public sealed record SecretsPolicySnapshot(long Epoch, IReadOnlyList<SecretsPolicyRule> Rules);
