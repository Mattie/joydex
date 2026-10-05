using System.Security.Cryptography;
using System.Text;

namespace Joydex.Secrets;

/// <summary>Maps a public secret alias to one exact key in one explicitly selected dotenv file.</summary>
public sealed record EnvSecretAlias(
    string Alias,
    string Key,
    string StableSecretId,
    long MappingGeneration);

/// <summary>One exact dotenv file and the public aliases it supplies to a combined provider.</summary>
public sealed record ExactEnvSecretSource(
    string Path,
    IReadOnlyList<EnvSecretAlias> Aliases);

/// <summary>Metadata about a configured alias that contains no secret value.</summary>
public sealed record SecretAliasMetadata(
    string Alias,
    string StableSecretId,
    long MappingGeneration,
    bool Available);

/// <summary>A short-lived group of fetched values that drops all references when disposed.</summary>
public sealed class SecretValueLease : IDisposable
{
    private Dictionary<string, string>? _values;

    internal SecretValueLease(Dictionary<string, string> values, string providerGeneration)
    {
        _values = values;
        ProviderGeneration = providerGeneration;
    }

    /// <summary>Gets the exact content generation used for this fetch.</summary>
    public string ProviderGeneration { get; }

    /// <summary>Gets a value by public alias while the lease is active.</summary>
    public string GetValue(string alias)
    {
        ObjectDisposedException.ThrowIf(_values is null, this);
        return _values.TryGetValue(alias, out var value)
            ? value
            : throw new KeyNotFoundException($"Secret alias '{alias}' was not fetched.");
    }

    /// <summary>Drops broker references to immutable parsed strings after delivery.</summary>
    public void Dispose()
    {
        _values?.Clear();
        _values = null;
    }
}

/// <summary>
/// Parses one or more exact dotenv files without changing the broker environment or searching parent paths.
/// </summary>
public sealed class ExactEnvSecretProvider
{
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IReadOnlyList<SourceMap> _sources;
    private readonly IReadOnlyDictionary<string, AliasBinding> _aliases;

    /// <summary>Creates a provider for an exact absolute file and fixed alias map.</summary>
    public ExactEnvSecretProvider(string path, IEnumerable<EnvSecretAlias> aliases)
        : this([new ExactEnvSecretSource(
            path,
            aliases?.ToArray() ?? throw new ArgumentNullException(nameof(aliases)))])
    {
    }

    /// <summary>Creates one provider over fixed alias maps from several exact files.</summary>
    public ExactEnvSecretProvider(IEnumerable<ExactEnvSecretSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var sourceMaps = new List<SourceMap>();
        var map = new Dictionary<string, AliasBinding>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(source.Aliases);
            var path = NormalizePath(source.Path);
            var sourceAliases = source.Aliases.ToArray();
            if (sourceAliases.Length is < 1 or > 256)
            {
                throw new ArgumentException(
                    "Each dotenv source requires 1 to 256 aliases.",
                    nameof(sources));
            }
            var sourceMap = new SourceMap(path, sourceAliases);
            foreach (var alias in sourceAliases)
            {
                if (string.IsNullOrWhiteSpace(alias.Alias)
                    || string.IsNullOrWhiteSpace(alias.Key)
                    || string.IsNullOrWhiteSpace(alias.StableSecretId)
                    || alias.MappingGeneration < 1
                    || !map.TryAdd(alias.Alias, new(sourceMap, alias)))
                {
                    throw new ArgumentException(
                        "The dotenv alias maps are invalid or contain duplicate public names.",
                        nameof(sources));
                }
            }
            sourceMaps.Add(sourceMap);
        }
        if (sourceMaps.Count is < 1 or > SecretsConfigurationStore.MaximumSources
            || map.Count > SecretsConfigurationStore.MaximumAliases)
        {
            throw new ArgumentException(
                "The dotenv provider source or alias limit was exceeded.",
                nameof(sources));
        }
        _sources = sourceMaps;
        _aliases = map;
    }

    /// <summary>Reads and validates one dotenv source, returning variable names without their values.</summary>
    public static IReadOnlyList<string> DiscoverKeys(string path)
    {
        var snapshot = ParseSnapshot(NormalizePath(path));
        try
        {
            return snapshot.Values.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            snapshot.Values.Clear();
        }
    }

    /// <summary>Lists configured aliases and current availability without returning values.</summary>
    public IReadOnlyList<SecretAliasMetadata> ListAliases()
    {
        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in _sources)
        {
            Dictionary<string, string>? parsed = null;
            try
            {
                parsed = ParseSnapshot(source.Path).Values;
                foreach (var alias in source.Aliases)
                {
                    if (parsed.ContainsKey(alias.Key)) available.Add(alias.Alias);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
            }
            finally
            {
                parsed?.Clear();
            }
        }

        return _aliases.Values
            .Select(binding => binding.Alias)
            .OrderBy(alias => alias.Alias, StringComparer.Ordinal)
            .Select(alias => new SecretAliasMetadata(
                alias.Alias,
                alias.StableSecretId,
                alias.MappingGeneration,
                available.Contains(alias.Alias)))
            .ToArray();
    }

    /// <summary>Fetches only the requested aliases from one stable file snapshot.</summary>
    public SecretValueLease Fetch(IEnumerable<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        var requested = aliases.Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length == 0)
        {
            throw new ArgumentException("At least one secret alias is required.", nameof(aliases));
        }

        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var bindings = new List<AliasBinding>();
            foreach (var aliasName in requested)
            {
                if (!_aliases.TryGetValue(aliasName, out var binding))
                {
                    throw new KeyNotFoundException($"Secret alias '{aliasName}' is not configured.");
                }
                bindings.Add(binding);
            }

            var generations = new List<(string Path, string Generation)>();
            foreach (var group in bindings.GroupBy(binding => binding.Source))
            {
                var snapshot = ParseSnapshot(group.Key.Path);
                try
                {
                    foreach (var binding in group)
                    {
                        if (!snapshot.Values.TryGetValue(binding.Alias.Key, out var value))
                        {
                            throw new InvalidDataException(
                                $"Secret alias '{binding.Alias.Alias}' is unavailable.");
                        }
                        selected.Add(binding.Alias.Alias, value);
                    }
                    generations.Add((group.Key.Path, snapshot.Generation));
                }
                finally
                {
                    snapshot.Values.Clear();
                }
            }
            return new SecretValueLease(selected, CombinedGeneration(generations));
        }
        catch
        {
            selected.Clear();
            throw;
        }
    }

    private static string CombinedGeneration(IReadOnlyList<(string Path, string Generation)> generations)
    {
        if (generations.Count == 1) return generations[0].Generation;
        var canonical = string.Join(
            '\n',
            generations
                .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Path.ToUpperInvariant() + "=" + item.Generation));
        return Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException("The dotenv provider requires an absolute path.", nameof(path));
        }
        return fullPath;
    }

    private static ParsedSnapshot ParseSnapshot(string path)
    {
        byte[] bytes;
        DateTime beforeWrite;
        long beforeLength;
        try
        {
            var before = new FileInfo(path);
            before.Refresh();
            if (!before.Exists || before.Length is <= 0 or > MaximumFileBytes)
            {
                throw new InvalidDataException(
                    $"The dotenv source must contain 1 to {MaximumFileBytes} bytes.");
            }
            beforeWrite = before.LastWriteTimeUtc;
            beforeLength = before.Length;
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidDataException("The dotenv source cannot be read.", exception);
        }

        try
        {
            var after = new FileInfo(path);
            after.Refresh();
            if (!after.Exists || after.Length != beforeLength || after.LastWriteTimeUtc != beforeWrite)
            {
                throw new InvalidDataException("The dotenv source changed while it was being read.");
            }

            string contents;
            try
            {
                contents = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("The dotenv source is not valid UTF-8.", exception);
            }
            if (ContainsInterpolation(contents))
            {
                throw new InvalidDataException(
                    "Dotenv interpolation is disabled so values cannot depend on the broker environment.");
            }

            try
            {
                var values = StrictDotEnvParser.Parse(contents);
                var generation = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                return new ParsedSnapshot(values, generation);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new InvalidDataException("The dotenv source could not be parsed.", exception);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool ContainsInterpolation(string contents)
    {
        var quote = '\0';
        var escaped = false;
        foreach (var character in contents)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (character == '\\' && quote == '"')
            {
                escaped = true;
                continue;
            }
            if (character is '\'' or '"')
            {
                if (quote == '\0') quote = character;
                else if (quote == character) quote = '\0';
                continue;
            }
            if (character == '$' && quote != '\'')
            {
                return true;
            }
        }
        return false;
    }

    private sealed record ParsedSnapshot(Dictionary<string, string> Values, string Generation);

    private sealed record SourceMap(string Path, IReadOnlyList<EnvSecretAlias> Aliases);

    private sealed record AliasBinding(SourceMap Source, EnvSecretAlias Alias);
}
