using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Joydex.Secrets;

/// <summary>One exact dotenv source and its public, value-free alias mappings.</summary>
public sealed record SecretsEnvSource(
    Guid SourceId,
    string DisplayName,
    string FilePath,
    long Generation,
    IReadOnlyList<EnvSecretAlias> Aliases,
    bool Enabled = true);

/// <summary>A locally managed recipe before its generation is assigned by the store.</summary>
public sealed record SecretsRecipeDraft(
    string RecipeId,
    string DisplayName,
    string ProjectReference,
    string Executable,
    IReadOnlyList<string> ArgumentTemplates,
    string WorkingDirectory,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Parameters,
    IReadOnlyDictionary<string, string> SecretEnvironment,
    IReadOnlyList<string> FingerprintInputs,
    SecretOutputDisclosure OutputDisclosure);

/// <summary>A versioned recipe that resolves to an exact exec proposal before authorization.</summary>
public sealed record SecretsRecipe(
    string RecipeId,
    string DisplayName,
    long Generation,
    string ProjectReference,
    string Executable,
    IReadOnlyList<string> ArgumentTemplates,
    string WorkingDirectory,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Parameters,
    IReadOnlyDictionary<string, string> SecretEnvironment,
    IReadOnlyList<string> FingerprintInputs,
    SecretOutputDisclosure OutputDisclosure)
{
    private static readonly Regex Placeholder = new(
        "\\{([A-Za-z][A-Za-z0-9_-]{0,63})\\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Validates supplied parameters and freezes the recipe to one concrete operation.</summary>
    public ExecOperationProposal Resolve(IReadOnlyDictionary<string, string> suppliedParameters)
    {
        ArgumentNullException.ThrowIfNull(suppliedParameters);
        if (suppliedParameters.Count != Parameters.Count
            || Parameters.Keys.Any(name => !suppliedParameters.ContainsKey(name)))
        {
            throw new ArgumentException("The recipe parameters do not match its declared parameters.");
        }

        foreach (var parameter in Parameters)
        {
            var value = suppliedParameters[parameter.Key];
            if (value.Length > 512
                || parameter.Value.Count == 0
                || !parameter.Value.Contains(value, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Recipe parameter '{parameter.Key}' has an unsupported value.");
            }
        }

        var arguments = ArgumentTemplates.Select(template =>
        {
            const string openBrace = "\u001fOPEN\u001f";
            const string closeBrace = "\u001fCLOSE\u001f";
            var escaped = template.Replace("{{", openBrace, StringComparison.Ordinal)
                .Replace("}}", closeBrace, StringComparison.Ordinal);
            var resolved = Placeholder.Replace(escaped, match =>
            {
                var name = match.Groups[1].Value;
                return suppliedParameters.TryGetValue(name, out var value)
                    ? value
                    : throw new ArgumentException($"Recipe parameter '{name}' was not supplied.");
            });
            if (resolved.Contains('{') || resolved.Contains('}'))
            {
                throw new ArgumentException("The recipe contains an invalid or unresolved parameter placeholder.");
            }
            return resolved.Replace(openBrace, "{", StringComparison.Ordinal)
                .Replace(closeBrace, "}", StringComparison.Ordinal);
        }).ToArray();

        return new ExecOperationProposal(
            Executable,
            arguments,
            WorkingDirectory,
            SecretEnvironment,
            FingerprintInputs,
            OutputDisclosure,
            Generation);
    }
}

/// <summary>Value-free Secrets configuration read atomically from local storage.</summary>
public sealed record SecretsConfigurationSnapshot(
    long Epoch,
    IReadOnlyList<SecretsEnvSource> Sources,
    IReadOnlyList<SecretsRecipe> Recipes);

/// <summary>Stores dotenv source metadata and saved recipes without storing secret values.</summary>
public sealed class SecretsConfigurationStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumSources = 32;
    public const int MaximumAliases = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
    private readonly object _gate = new();
    private readonly string _path;

    public SecretsConfigurationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _ = Read();
    }

    public SecretsConfigurationSnapshot Read() => WithLock(() => Snapshot(LoadDocument(_path)));

    internal TResult WithSnapshotLock<TResult>(Func<SecretsConfigurationSnapshot, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return WithLock(() => action(Snapshot(LoadDocument(_path))));
    }

    /// <summary>Creates or replaces one dotenv source and preserves unchanged alias identities.</summary>
    public SecretsConfigurationSnapshot UpsertEnvSource(
        Guid? sourceId,
        string displayName,
        string filePath,
        IReadOnlyDictionary<string, string> aliasKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(aliasKeys);
        if (displayName.Length > 96 || aliasKeys.Count is < 1 or > 256)
        {
            throw new ArgumentException("The dotenv source metadata exceeds its allowed size.");
        }

        var fullPath = Path.GetFullPath(filePath.Trim());
        if (!Path.IsPathFullyQualified(fullPath))
        {
            throw new ArgumentException("The dotenv source path must be absolute.", nameof(filePath));
        }
        foreach (var mapping in aliasKeys)
        {
            if (!ValidAlias(mapping.Key) || !ValidEnvironmentName(mapping.Value))
            {
                throw new ArgumentException("A dotenv alias or key is invalid.", nameof(aliasKeys));
            }
        }

        return WithLock(() =>
        {
            var document = LoadDocument(_path);
            var current = sourceId is null
                ? null
                : document.Sources.SingleOrDefault(source => source.SourceId == sourceId.Value)
                    ?? throw new KeyNotFoundException("The dotenv source no longer exists.");
            var pathChanged = current is not null
                && !string.Equals(current.FilePath, fullPath, StringComparison.OrdinalIgnoreCase);
            var previous = current?.Aliases.ToDictionary(alias => alias.Alias, StringComparer.Ordinal)
                ?? new Dictionary<string, EnvSecretAlias>(StringComparer.Ordinal);
            var aliases = aliasKeys.OrderBy(mapping => mapping.Key, StringComparer.Ordinal)
                .Select(mapping =>
                {
                    previous.TryGetValue(mapping.Key, out var existing);
                    return existing is not null
                        && !pathChanged
                        && string.Equals(existing.Key, mapping.Value, StringComparison.Ordinal)
                            ? existing
                            : new EnvSecretAlias(
                                mapping.Key,
                                mapping.Value,
                                Guid.NewGuid().ToString("N"),
                                existing is null ? 1 : checked(existing.MappingGeneration + 1));
                })
                .ToArray();
            var source = new SecretsEnvSource(
                current?.SourceId ?? Guid.NewGuid(),
                displayName.Trim(),
                fullPath,
                current is null ? 1 : checked(current.Generation + 1),
                aliases);
            var sourceIndex = current is null
                ? -1
                : document.Sources.FindIndex(candidate => candidate.SourceId == current.SourceId);
            if (sourceIndex < 0) document.Sources.Add(source);
            else document.Sources[sourceIndex] = source;
            ValidateSourceSet(document.Sources, invalidData: false);
            document.Epoch = checked(document.Epoch + 1);
            SaveDocument(_path, document);
            return Snapshot(document);
        });
    }

    public SecretsConfigurationSnapshot RemoveEnvSource(Guid sourceId) => WithLock(() =>
    {
        var document = LoadDocument(_path);
        if (document.Sources.RemoveAll(source => source.SourceId == sourceId) == 0)
        {
            throw new KeyNotFoundException("The dotenv source no longer exists.");
        }
        document.Epoch = checked(document.Epoch + 1);
        SaveDocument(_path, document);
        return Snapshot(document);
    });

    public SecretsConfigurationSnapshot UpsertRecipe(SecretsRecipeDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);
        return WithLock(() =>
        {
            var document = LoadDocument(_path);
            var index = document.Recipes.FindIndex(recipe => string.Equals(
                recipe.RecipeId,
                draft.RecipeId,
                StringComparison.Ordinal));
            var generation = index < 0 ? 1 : checked(document.Recipes[index].Generation + 1);
            var recipe = new SecretsRecipe(
                draft.RecipeId,
                draft.DisplayName.Trim(),
                generation,
                draft.ProjectReference,
                Path.GetFullPath(draft.Executable),
                draft.ArgumentTemplates.ToArray(),
                Path.GetFullPath(draft.WorkingDirectory),
                draft.Parameters.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                    StringComparer.Ordinal),
                new Dictionary<string, string>(draft.SecretEnvironment, StringComparer.Ordinal),
                draft.FingerprintInputs.Select(Path.GetFullPath).ToArray(),
                draft.OutputDisclosure);
            if (index < 0) document.Recipes.Add(recipe);
            else document.Recipes[index] = recipe;
            document.Epoch = checked(document.Epoch + 1);
            SaveDocument(_path, document);
            return Snapshot(document);
        });
    }

    public SecretsConfigurationSnapshot RemoveRecipe(string recipeId) => WithLock(() =>
    {
        var document = LoadDocument(_path);
        if (document.Recipes.RemoveAll(recipe => string.Equals(
                recipe.RecipeId,
                recipeId,
                StringComparison.Ordinal)) == 0)
        {
            throw new KeyNotFoundException($"Recipe '{recipeId}' no longer exists.");
        }
        document.Epoch = checked(document.Epoch + 1);
        SaveDocument(_path, document);
        return Snapshot(document);
    });

    private TResult WithLock<TResult>(Func<TResult> action)
    {
        lock (_gate)
        {
            using var fileLock = SecretsFileLock.Acquire(_path);
            return action();
        }
    }

    private static ConfigurationDocument LoadDocument(string path)
    {
        if (!File.Exists(path)) return new ConfigurationDocument { Epoch = 1 };
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<ConfigurationDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("The Secrets configuration is empty.");
            ValidateDocument(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Secrets configuration is malformed.", exception);
        }
    }

    private static void SaveDocument(string path, ConfigurationDocument document) =>
        SecretsAtomicFile.WriteJson(path, document, JsonOptions);

    private static SecretsConfigurationSnapshot Snapshot(ConfigurationDocument document) => new(
        document.Epoch,
        document.Sources.ToArray(),
        document.Recipes.ToArray());

    private static void ValidateDocument(ConfigurationDocument document)
    {
        if (document.SchemaVersion != CurrentSchemaVersion
            || document.Epoch < 1
            || document.Sources.Count > MaximumSources
            || document.Recipes.Count > 128
            || document.Recipes.Select(recipe => recipe.RecipeId)
                .Distinct(StringComparer.Ordinal).Count() != document.Recipes.Count)
        {
            throw new InvalidDataException("The Secrets configuration schema or bounds are invalid.");
        }
        foreach (var source in document.Sources)
        {
            if (source.SourceId == Guid.Empty
                || source.Generation < 1
                || source.DisplayName.Length is < 1 or > 96
                || !Path.IsPathFullyQualified(source.FilePath)
                || source.Aliases.Count is < 1 or > 256
                || source.Aliases.Select(alias => alias.Alias)
                    .Distinct(StringComparer.Ordinal).Count() != source.Aliases.Count
                || source.Aliases.Any(alias => !ValidAlias(alias.Alias)
                    || !ValidEnvironmentName(alias.Key)
                    || string.IsNullOrWhiteSpace(alias.StableSecretId)
                    || alias.StableSecretId.Length > 96
                    || alias.MappingGeneration < 1))
            {
                throw new InvalidDataException("The Secrets configuration contains an invalid source.");
            }
        }
        ValidateSourceSet(document.Sources, invalidData: true);
        foreach (var recipe in document.Recipes)
        {
            ValidateDraft(new SecretsRecipeDraft(
                recipe.RecipeId,
                recipe.DisplayName,
                recipe.ProjectReference,
                recipe.Executable,
                recipe.ArgumentTemplates,
                recipe.WorkingDirectory,
                recipe.Parameters,
                recipe.SecretEnvironment,
                recipe.FingerprintInputs,
                recipe.OutputDisclosure));
            if (recipe.Generation < 1)
            {
                throw new InvalidDataException("The Secrets configuration contains an invalid recipe generation.");
            }
        }
    }

    private static void ValidateDraft(SecretsRecipeDraft draft)
    {
        if (!ValidId(draft.RecipeId, 64)
            || string.IsNullOrWhiteSpace(draft.DisplayName)
            || draft.DisplayName.Length > 96
            || !ValidId(draft.ProjectReference, 96)
            || !Path.IsPathFullyQualified(draft.Executable)
            || !Path.IsPathFullyQualified(draft.WorkingDirectory)
            || draft.ArgumentTemplates.Count > 128
            || draft.ArgumentTemplates.Any(argument => argument.Length > 4096 || argument.Contains('\0'))
            || draft.Parameters.Count > 32
            || draft.Parameters.Any(parameter => !ValidId(parameter.Key, 64)
                || parameter.Value.Count is < 1 or > 64
                || parameter.Value.Any(value => value.Length > 512 || value.Contains('\0')))
            || draft.SecretEnvironment.Count is < 1 or > 64
            || draft.SecretEnvironment.Any(mapping =>
                !ValidEnvironmentName(mapping.Key) || !ValidAlias(mapping.Value))
            || draft.SecretEnvironment.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != draft.SecretEnvironment.Count
            || draft.FingerprintInputs.Count > 64
            || draft.FingerprintInputs.Any(path => !Path.IsPathFullyQualified(path))
            || !Enum.IsDefined(draft.OutputDisclosure))
        {
            throw new ArgumentException("The recipe metadata is invalid or exceeds its bounds.");
        }
    }

    private static bool ValidAlias(string value) => ValidId(value, 96);

    private static bool ValidId(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool ValidEnvironmentName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && !char.IsAsciiDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static void ValidateSourceSet(
        IReadOnlyList<SecretsEnvSource> sources,
        bool invalidData)
    {
        var duplicateName = sources
            .GroupBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        var aliases = sources.SelectMany(source => source.Aliases.Select(alias => new
        {
            alias.Alias,
            source.DisplayName,
        })).ToArray();
        var duplicateAlias = aliases
            .GroupBy(alias => alias.Alias, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        var duplicateIdentity = sources.SelectMany(source => source.Aliases)
            .GroupBy(alias => alias.StableSecretId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        string? message = null;
        if (sources.Count > MaximumSources)
        {
            message = $"Joydex supports up to {MaximumSources} secret sources.";
        }
        else if (aliases.Length > MaximumAliases)
        {
            message = $"Joydex supports up to {MaximumAliases} variables across all secret sources.";
        }
        else if (duplicateName is not null)
        {
            message = $"A secret source named '{duplicateName}' already exists.";
        }
        else if (duplicateAlias is not null)
        {
            message = $"Variable '{duplicateAlias.Key}' is already provided by another secret source. "
                + "Give one of them a different public name until source selection is available in approval popups.";
        }
        else if (duplicateIdentity is not null)
        {
            message = "The Secrets configuration repeats a stable secret identity.";
        }

        if (message is null) return;
        if (invalidData) throw new InvalidDataException(message);
        throw new InvalidOperationException(message);
    }

    private sealed class ConfigurationDocument
    {
        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

        public long Epoch { get; set; }

        public List<SecretsEnvSource> Sources { get; set; } = [];

        public List<SecretsRecipe> Recipes { get; set; } = [];
    }
}
