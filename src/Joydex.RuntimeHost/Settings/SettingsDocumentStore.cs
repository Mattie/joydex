using System.Security.Cryptography;
using System.Text.Json;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;

namespace Joydex.RuntimeHost.Settings;

internal sealed class SettingsDocumentStore
{
    private readonly RuntimeSettingsPaths _paths;
    private readonly ISettingsDocumentIo _io;

    public SettingsDocumentStore(RuntimeSettingsPaths paths, ISettingsDocumentIo io)
    {
        _paths = paths.Normalize();
        _io = io;
    }

    public SettingsBundle LoadOrCreate() => SettingsCanonicalizer.Normalize(new SettingsBundle(
        ConfigStore.LoadOrCreate(_paths.Companion),
        VoicePePreferencesStore.LoadOrCreate(_paths.Voice),
        PebbleIndexPreferencesStore.LoadOrCreate(_paths.PebbleIndex),
        TaskAlertPreferencesStore.LoadOrCreate(_paths.TaskAlerts)));

    public ExternalSettingsRead ReadExternal(
        SettingsBundle basis,
        IEnumerable<SettingsAggregateId> aggregates)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(aggregates);
        var loaded = basis;
        var fingerprints = new Dictionary<SettingsAggregateId, SettingsFileFingerprint>();
        foreach (var aggregate in aggregates.Distinct())
        {
            var path = _paths.For(aggregate);
            var content = ReadRequired(path);
            fingerprints.Add(aggregate, Fingerprint(content));
            loaded = aggregate switch
            {
                SettingsAggregateId.Companion => loaded with
                {
                    Companion = ConfigStore.ParseExisting(content, path),
                },
                SettingsAggregateId.Voice => loaded with
                {
                    Voice = VoicePePreferencesStore.ParseExisting(content),
                },
                SettingsAggregateId.PebbleIndex => loaded with
                {
                    PebbleIndex = PebbleIndexPreferencesStore.ParseExisting(content),
                },
                SettingsAggregateId.TaskAlerts => loaded with
                {
                    TaskAlerts = TaskAlertPreferencesStore.ParseExisting(content),
                },
                _ => throw new ArgumentOutOfRangeException(nameof(aggregates)),
            };
        }
        return new ExternalSettingsRead(SettingsCanonicalizer.Normalize(loaded), fingerprints);
    }

    public ExternalSettingsInspection InspectExternal(SettingsAggregateId aggregate)
    {
        var path = _paths.For(aggregate);
        byte[] content;
        try
        {
            content = ReadRequired(path);
        }
        catch (Exception exception) when (IsDocumentReadFailure(exception))
        {
            return new ExternalSettingsInspection(
                new SettingsFileFingerprint(false, "MISSING"),
                false,
                exception.Message);
        }

        var fingerprint = Fingerprint(content);
        try
        {
            object parsed = aggregate switch
            {
                SettingsAggregateId.Companion => ConfigStore.ParseExisting(content, path),
                SettingsAggregateId.Voice => VoicePePreferencesStore.ParseExisting(content),
                SettingsAggregateId.PebbleIndex => PebbleIndexPreferencesStore.ParseExisting(content),
                SettingsAggregateId.TaskAlerts => TaskAlertPreferencesStore.ParseExisting(content),
                _ => throw new ArgumentOutOfRangeException(nameof(aggregate)),
            };
            _ = parsed;
            return new ExternalSettingsInspection(fingerprint, true, null);
        }
        catch (Exception exception) when (IsDocumentReadFailure(exception))
        {
            return new ExternalSettingsInspection(fingerprint, false, exception.Message);
        }
    }

    public IReadOnlyDictionary<SettingsAggregateId, SettingsFileFingerprint> Fingerprints() =>
        Enum.GetValues<SettingsAggregateId>().ToDictionary(
            aggregate => aggregate,
            aggregate => SettingsFileFingerprint.Read(_io, _paths.For(aggregate)));

    public IReadOnlyDictionary<SettingsAggregateId, byte[]> Serialize(
        SettingsBundle bundle,
        IEnumerable<SettingsAggregateId> aggregates)
    {
        var journalDirectory = Path.GetDirectoryName(_paths.Journal)
            ?? throw new InvalidOperationException("The settings journal path has no parent directory.");
        Directory.CreateDirectory(journalDirectory);
        var stageDirectory = Path.Combine(journalDirectory, $".settings-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDirectory);
        try
        {
            var documents = new Dictionary<SettingsAggregateId, byte[]>();
            foreach (var aggregate in aggregates.Distinct())
            {
                var path = Path.Combine(stageDirectory, aggregate + ".json");
                switch (aggregate)
                {
                    case SettingsAggregateId.Companion:
                        ConfigStore.Save(path, bundle.Companion);
                        break;
                    case SettingsAggregateId.Voice:
                        VoicePePreferencesStore.Save(path, bundle.Voice);
                        break;
                    case SettingsAggregateId.PebbleIndex:
                        PebbleIndexPreferencesStore.Save(path, bundle.PebbleIndex);
                        break;
                    case SettingsAggregateId.TaskAlerts:
                        TaskAlertPreferencesStore.Save(path, bundle.TaskAlerts);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(aggregate));
                }

                documents.Add(aggregate, File.ReadAllBytes(path));
            }

            return documents;
        }
        finally
        {
            if (Directory.Exists(stageDirectory))
            {
                Directory.Delete(stageDirectory, recursive: true);
            }
        }
    }

    public byte[]? ReadExact(SettingsAggregateId aggregate)
    {
        try
        {
            return _io.Read(_paths.For(aggregate));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void WriteExact(SettingsAggregateId aggregate, ReadOnlySpan<byte> content) =>
        _io.Replace(_paths.For(aggregate), content);

    public void RestoreExact(SettingsAggregateId aggregate, byte[]? content)
    {
        if (content is null)
        {
            _io.Delete(_paths.For(aggregate));
        }
        else
        {
            _io.Replace(_paths.For(aggregate), content);
        }
    }

    private byte[] ReadRequired(string path)
    {
        try
        {
            return _io.Read(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundException("The settings file is missing.", path, exception);
        }
    }

    private static SettingsFileFingerprint Fingerprint(ReadOnlySpan<byte> content) =>
        new(true, Convert.ToHexString(SHA256.HashData(content)));

    private static bool IsDocumentReadFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidDataException
            or JsonException;
}

internal sealed record ExternalSettingsRead(
    SettingsBundle Bundle,
    IReadOnlyDictionary<SettingsAggregateId, SettingsFileFingerprint> Fingerprints);

internal sealed record ExternalSettingsInspection(
    SettingsFileFingerprint Fingerprint,
    bool IsValid,
    string? Detail);
