using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Secrets;

public enum SecretsAuditEventKind
{
    RequestReceived,
    RequestOutcome,
    ConsentDecision,
    LaunchCommitted,
    LaunchStarted,
    LaunchCompleted,
    LaunchTerminated,
    LaunchUnconfirmed,
    FailedBeforeLaunch,
    RequestDetached,
}

/// <summary>Sanitized audit metadata. It never contains values, reasons, commands or paths.</summary>
public sealed record SecretsAuditRecord(
    Guid AttemptId,
    SecretsAuditEventKind Kind,
    DateTimeOffset Timestamp,
    string RequestId,
    string ClientReference,
    string ProjectReference,
    IReadOnlyList<string> Aliases,
    string? OperationDigest = null,
    string? Outcome = null,
    Guid? RuleId = null,
    RememberedGrantScopeKind? GrantScope = null,
    int? ProcessId = null,
    DateTimeOffset? ProcessStartedAt = null,
    Guid? ClientRegistrationId = null,
    long? ClientGeneration = null,
    string? ProjectIdentityDigest = null);

/// <summary>A durable request identity whose launch boundary was already crossed.</summary>
public enum SecretsCommittedOutcome
{
    LaunchUnconfirmed,
    Completed,
    Terminated,
    FailedBeforeLaunch,
}

public sealed record SecretsCommittedRequest(
    Guid AttemptId,
    Guid ClientRegistrationId,
    long ClientGeneration,
    string RequestId,
    string OperationDigest,
    SecretsCommittedOutcome Outcome)
{
    public bool OutcomeUnconfirmed => Outcome == SecretsCommittedOutcome.LaunchUnconfirmed;
}

/// <summary>Append-only, write-through audit storage for request and launch boundaries.</summary>
public sealed class SecretsAuditJournal
{
    internal string FilePath => _path;
    private const int CurrentOperationSchemaVersion = 1;
    private const int MaximumActivityRecords = 10_000;
    private const int MaximumDurableOperations = 10_000;
    private const int ActivityMaintenanceInterval = 256;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _operationPath;
    private readonly SecretsMetricsStore _metrics;
    private int _appendsSinceMaintenance;

    public SecretsAuditJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _operationPath = Path.Combine(
            Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The audit path has no parent directory."),
            "operations.json");
        _metrics = new SecretsMetricsStore(Path.Combine(
            Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The audit path has no parent directory."),
            "metrics.json"));
    }

    /// <summary>Appends and flushes one sanitized record before returning.</summary>
    public void Append(SecretsAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);
        lock (_gate)
        {
            using var fileLock = SecretsFileLock.Acquire(_path);
            UpdateOperationLedger(record);
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The audit path has no parent directory.");
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(
                       _path,
                       FileMode.Append,
                       FileAccess.Write,
                       FileShare.Read,
                       4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, record, JsonOptions);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }

            _appendsSinceMaintenance++;
            if (_appendsSinceMaintenance >= ActivityMaintenanceInterval)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
                var retained = ReadAllUnlocked()
                    .Where(item => item.Timestamp >= cutoff)
                    .TakeLast(MaximumActivityRecords)
                    .ToArray();
                WriteActivityUnlocked(retained);
                _appendsSinceMaintenance = 0;
            }
        }
        try
        {
            _metrics.Record(record);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or OverflowException
            or TimeoutException
            or UnauthorizedAccessException)
        {
            // Metrics must never prevent an audited request or launch from proceeding.
        }
    }

    /// <summary>Reads all complete records, failing closed on malformed or partial data.</summary>
    public IReadOnlyList<SecretsAuditRecord> ReadAll()
    {
        lock (_gate)
        {
            using var fileLock = SecretsFileLock.Acquire(_path);
            return ReadAllUnlocked();
        }
    }

    /// <summary>Removes old visible activity without changing durable launch tombstones.</summary>
    public void PruneActivity(DateTimeOffset cutoff, int maximumRecords = MaximumActivityRecords)
    {
        if (maximumRecords < 1) throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        lock (_gate)
        {
            using var fileLock = SecretsFileLock.Acquire(_path);
            if (!File.Exists(_path)) return;
            var retained = ReadAllUnlocked()
                .Where(record => record.Timestamp >= cutoff)
                .TakeLast(maximumRecords)
                .ToArray();
            WriteActivityUnlocked(retained);
        }
    }

    /// <summary>Clears visible activity without clearing remembered grants or launch tombstones.</summary>
    public void ClearActivity()
    {
        lock (_gate)
        {
            using var fileLock = SecretsFileLock.Acquire(_path);
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    /// <summary>Finds committed launches that have no conclusive terminal record.</summary>
    public IReadOnlyList<Guid> FindUnconfirmedLaunches()
    {
        var durable = FindCommittedRequests()
            .Where(request => request.OutcomeUnconfirmed)
            .Select(request => request.AttemptId);
        var activity = ReadAll()
            .GroupBy(record => record.AttemptId)
            .Where(group => group.Any(record => record.Kind == SecretsAuditEventKind.LaunchCommitted)
                && !group.Any(record => record.Kind is SecretsAuditEventKind.LaunchCompleted
                    or SecretsAuditEventKind.LaunchTerminated
                    or SecretsAuditEventKind.FailedBeforeLaunch))
            .Select(group => group.Key);
        return durable.Concat(activity).Distinct().ToArray();
    }

    /// <summary>Returns every durable request key that crossed the launch-commit boundary.</summary>
    public IReadOnlyList<SecretsCommittedRequest> FindCommittedRequests()
    {
        lock (_gate)
        {
            using var operationLock = SecretsFileLock.Acquire(_operationPath);
            if (File.Exists(_operationPath))
            {
                return LoadOperationDocument()
                    .Operations
                    .Select(item => new SecretsCommittedRequest(
                        item.AttemptId,
                        item.ClientRegistrationId,
                        item.ClientGeneration,
                        item.RequestId,
                        item.OperationDigest,
                        DurableOutcome(item)))
                    .ToArray();
            }
        }
        return FindCommittedRequests(ReadAll());
    }

    private static IReadOnlyList<SecretsCommittedRequest> FindCommittedRequests(
        IReadOnlyList<SecretsAuditRecord> records) => records
            .GroupBy(record => record.AttemptId)
            .Where(group => group.Any(record => record.Kind == SecretsAuditEventKind.LaunchCommitted))
            .Select(group => new
            {
                Group = group,
                Commit = group.Last(record => record.Kind == SecretsAuditEventKind.LaunchCommitted),
            })
            .Where(item => item.Commit.ClientRegistrationId is not null
                && item.Commit.ClientGeneration is not null
                && item.Commit.OperationDigest is not null)
            .Select(item => new SecretsCommittedRequest(
                item.Commit.AttemptId,
                item.Commit.ClientRegistrationId!.Value,
                item.Commit.ClientGeneration!.Value,
                item.Commit.RequestId,
                item.Commit.OperationDigest!,
                item.Group.Any(record => record.Kind == SecretsAuditEventKind.FailedBeforeLaunch)
                    ? SecretsCommittedOutcome.FailedBeforeLaunch
                    : item.Group.Any(record => record.Kind == SecretsAuditEventKind.LaunchTerminated)
                        ? SecretsCommittedOutcome.Terminated
                    : item.Group.Any(record => record.Kind == SecretsAuditEventKind.LaunchCompleted)
                        ? SecretsCommittedOutcome.Completed
                        : SecretsCommittedOutcome.LaunchUnconfirmed))
            .ToArray();

    private IReadOnlyList<SecretsAuditRecord> ReadAllUnlocked()
    {
        if (!File.Exists(_path)) return [];
        var records = new List<SecretsAuditRecord>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(_path))
        {
            lineNumber++;
            try
            {
                var record = JsonSerializer.Deserialize<SecretsAuditRecord>(line, JsonOptions)
                    ?? throw new InvalidDataException($"Audit line {lineNumber} is empty.");
                Validate(record);
                records.Add(record);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Audit line {lineNumber} is malformed.", exception);
            }
        }
        return records;
    }

    private void UpdateOperationLedger(SecretsAuditRecord record)
    {
        if (record.Kind is not SecretsAuditEventKind.LaunchCommitted
            and not SecretsAuditEventKind.LaunchCompleted
            and not SecretsAuditEventKind.LaunchTerminated
            and not SecretsAuditEventKind.LaunchUnconfirmed
            and not SecretsAuditEventKind.FailedBeforeLaunch)
        {
            return;
        }
        using var operationLock = SecretsFileLock.Acquire(_operationPath);
        var creatingLedger = !File.Exists(_operationPath);
        var document = LoadOperationDocument();
        if (creatingLedger)
        {
            foreach (var legacy in FindCommittedRequests(ReadAllUnlocked())
                         .TakeLast(MaximumDurableOperations))
            {
                document.Operations.Add(new(
                    legacy.AttemptId,
                    legacy.ClientRegistrationId,
                    legacy.ClientGeneration,
                    legacy.RequestId,
                    legacy.OperationDigest,
                    legacy.OutcomeUnconfirmed,
                    legacy.Outcome == SecretsCommittedOutcome.FailedBeforeLaunch,
                    legacy.Outcome == SecretsCommittedOutcome.Terminated,
                    DateTimeOffset.UnixEpoch));
            }
        }
        var index = document.Operations.FindIndex(item => item.AttemptId == record.AttemptId);
        if (record.Kind == SecretsAuditEventKind.LaunchCommitted)
        {
            if (record.ClientRegistrationId is null
                || record.ClientGeneration is null
                || record.OperationDigest is null)
            {
                return;
            }
            if (index < 0)
            {
                while (document.Operations.Count >= MaximumDurableOperations)
                {
                    var removable = document.Operations
                        .Where(item => !item.OutcomeUnconfirmed)
                        .OrderBy(item => item.UpdatedAt)
                        .FirstOrDefault();
                    if (removable is null)
                    {
                        throw new InvalidOperationException(
                            "The durable Secrets operation journal is full of unconfirmed launches.");
                    }
                    document.Operations.Remove(removable);
                }
                document.Operations.Add(new(
                    record.AttemptId,
                    record.ClientRegistrationId.Value,
                    record.ClientGeneration.Value,
                    record.RequestId,
                    record.OperationDigest,
                    true,
                    false,
                    false,
                    record.Timestamp));
            }
        }
        else if (index >= 0)
        {
            var current = document.Operations[index];
            document.Operations[index] = current with
            {
                OutcomeUnconfirmed = record.Kind == SecretsAuditEventKind.LaunchUnconfirmed,
                FailedBeforeLaunch = record.Kind == SecretsAuditEventKind.FailedBeforeLaunch,
                TerminatedAfterLaunch = record.Kind == SecretsAuditEventKind.LaunchTerminated,
                UpdatedAt = record.Timestamp,
            };
        }
        SecretsAtomicFile.WriteJson(_operationPath, document, JsonOptions);
    }

    private OperationDocument LoadOperationDocument()
    {
        if (!File.Exists(_operationPath)) return new();
        try
        {
            using var stream = new FileStream(_operationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<OperationDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("The durable Secrets operation journal is empty.");
            if (document.SchemaVersion != CurrentOperationSchemaVersion
                || document.Operations.Count > MaximumDurableOperations
                || document.Operations.Any(item => item.AttemptId == Guid.Empty
                    || item.ClientRegistrationId == Guid.Empty
                    || item.ClientGeneration < 1
                    || string.IsNullOrWhiteSpace(item.RequestId)
                    || item.RequestId.Length > 128
                    || item.OperationDigest.Length != 64
                    || (item.OutcomeUnconfirmed ? 1 : 0)
                        + (item.FailedBeforeLaunch ? 1 : 0)
                        + (item.TerminatedAfterLaunch ? 1 : 0) > 1)
                || document.Operations.Select(item => item.AttemptId).Distinct().Count()
                    != document.Operations.Count)
            {
                throw new InvalidDataException("The durable Secrets operation journal is invalid.");
            }
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The durable Secrets operation journal is malformed.",
                exception);
        }
    }

    private void WriteActivityUnlocked(IReadOnlyList<SecretsAuditRecord> records)
    {
        if (records.Count == 0)
        {
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The audit path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                foreach (var record in records)
                {
                    JsonSerializer.Serialize(stream, record, JsonOptions);
                    stream.WriteByte((byte)'\n');
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class OperationDocument
    {
        public int SchemaVersion { get; init; } = CurrentOperationSchemaVersion;

        public List<DurableOperation> Operations { get; init; } = [];
    }

    private sealed record DurableOperation(
        Guid AttemptId,
        Guid ClientRegistrationId,
        long ClientGeneration,
        string RequestId,
        string OperationDigest,
        bool OutcomeUnconfirmed,
        bool FailedBeforeLaunch,
        bool TerminatedAfterLaunch,
        DateTimeOffset UpdatedAt);

    private static SecretsCommittedOutcome DurableOutcome(DurableOperation operation) =>
        operation.FailedBeforeLaunch
            ? SecretsCommittedOutcome.FailedBeforeLaunch
            : operation.TerminatedAfterLaunch
                ? SecretsCommittedOutcome.Terminated
            : operation.OutcomeUnconfirmed
                ? SecretsCommittedOutcome.LaunchUnconfirmed
                : SecretsCommittedOutcome.Completed;

    private static void Validate(SecretsAuditRecord record)
    {
        if (record.AttemptId == Guid.Empty
            || string.IsNullOrWhiteSpace(record.RequestId)
            || record.RequestId.Length > 128
            || string.IsNullOrWhiteSpace(record.ClientReference)
            || record.ClientReference.Length > 96
            || string.IsNullOrWhiteSpace(record.ProjectReference)
            || record.ProjectReference.Length > 96
            || record.Aliases.Count > 64
            || record.Aliases.Any(alias => string.IsNullOrWhiteSpace(alias) || alias.Length > 96)
            || (record.OperationDigest is not null && record.OperationDigest.Length != 64)
            || (record.ProjectIdentityDigest is not null && (record.ProjectIdentityDigest.Length != 64
                || record.ProjectIdentityDigest.Any(character => !char.IsAsciiHexDigit(character))))
            || (record.Outcome is not null && record.Outcome.Length > 64)
            || (record.ClientRegistrationId is null) != (record.ClientGeneration is null)
            || record.ClientGeneration < 1)
        {
            throw new ArgumentException("The audit record contains invalid or unbounded metadata.", nameof(record));
        }
    }
}
