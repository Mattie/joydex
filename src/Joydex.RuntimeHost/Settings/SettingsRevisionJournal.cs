using System.Text.Json;
using System.Text.Json.Serialization;
using Joydex.Contracts;

namespace Joydex.RuntimeHost.Settings;

internal enum SettingsCommitPhase
{
    WritingDocuments,
    DesiredCommitted,
}

internal sealed record JournalFileFingerprint(bool Exists, string ContentHash)
{
    public static JournalFileFingerprint From(SettingsFileFingerprint value) =>
        new(value.Exists, value.ContentHash);

    public SettingsFileFingerprint ToRuntime() => new(Exists, ContentHash);
}

internal sealed record SettingsDocumentBackup(
    SettingsAggregateId Aggregate,
    bool Existed,
    string? PreviousContentBase64,
    string PreviousHash,
    string TargetHash);

internal sealed record PendingSettingsCommit(
    SettingsCommitPhase Phase,
    Guid OperationId,
    string TokenHash,
    string PayloadHash,
    long PreviousRevision,
    long TargetRevision,
    SettingsBundle Candidate,
    SettingsAggregateId[] ChangedAggregates,
    SettingsEffect[] Effects,
    SettingsDocumentBackup[] Documents);

// Operation replay preserves the immutable outcome, settings revision, and completion order. The
// snapshot is intentionally materialized from current authority state so bounded replay does not
// copy every historical configuration into the control journal.
internal sealed record CompletedSettingsOperation(
    Guid OperationId,
    string TokenHash,
    string PayloadHash,
    SettingsApplyStatus Status,
    bool DesiredStateCommitted,
    bool CanCloseSettings,
    SettingsAggregateApplyResult[] Aggregates,
    string[] Errors,
    long CompletionRevision,
    long CompletionSequence)
{
    public static CompletedSettingsOperation From(
        string tokenHash,
        string payloadHash,
        ApplySettingsResult result,
        long completionSequence) => new(
            result.OperationId,
            tokenHash,
            payloadHash,
            result.Status,
            result.DesiredStateCommitted,
            result.CanCloseSettings,
            result.Aggregates,
            result.Errors,
            result.Snapshot.Revision,
            completionSequence);

    public ApplySettingsResult Materialize(SettingsSnapshot currentSnapshot) => new(
        OperationId,
        Status,
        DesiredStateCommitted,
        CanCloseSettings,
        Aggregates,
        Errors,
        currentSnapshot);
}

internal sealed record SettingsRevisionJournal(
    int SchemaVersion,
    string? SelectionIdentity,
    long CurrentRevision,
    SettingsBundle Desired,
    SettingsBundle Active,
    SettingsAggregateState[] Aggregates,
    Dictionary<SettingsAggregateId, JournalFileFingerprint> Fingerprints,
    PendingSettingsCommit? PendingCommit,
    CompletedSettingsOperation[] CompletedOperations,
    long LastCompletionSequence = 0)
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumCompletedOperations = 64;
}

internal sealed class SettingsRevisionJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly string _path;
    private readonly ISettingsDocumentIo _io;

    public SettingsRevisionJournalStore(string path, ISettingsDocumentIo io)
    {
        _path = Path.GetFullPath(path);
        _io = io;
    }

    public SettingsRevisionJournal? Load()
    {
        if (!_io.Exists(_path))
        {
            return null;
        }

        try
        {
            var journal = _io.Read(
                _path,
                stream => JsonSerializer.Deserialize<SettingsRevisionJournal>(stream, JsonOptions)
                          ?? throw new JsonException("The settings revision journal was empty."));
            if (journal.SchemaVersion == 1)
            {
                journal = UpgradeVersionOne(journal);
            }
            if (journal.SchemaVersion != SettingsRevisionJournal.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported settings revision journal schema {journal.SchemaVersion}.");
            }

            ValidateCompletionOrder(journal);

            return journal;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The settings revision journal is not valid JSON.", exception);
        }
    }

    public void Save(SettingsRevisionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        _io.Replace(_path, stream => JsonSerializer.Serialize(stream, journal, JsonOptions));
    }

    private static SettingsRevisionJournal UpgradeVersionOne(SettingsRevisionJournal journal)
    {
        var completionSequence = 0L;
        var operations = journal.CompletedOperations
            .OrderBy(item => item.CompletionRevision)
            .ThenBy(item => item.OperationId)
            .Select(item => item with { CompletionSequence = checked(++completionSequence) })
            .ToArray();
        return journal with
        {
            SchemaVersion = SettingsRevisionJournal.CurrentSchemaVersion,
            CompletedOperations = operations,
            LastCompletionSequence = completionSequence,
        };
    }

    private static void ValidateCompletionOrder(SettingsRevisionJournal journal)
    {
        if (journal.LastCompletionSequence < 0
            || journal.CompletedOperations.Any(item =>
                item.CompletionSequence <= 0
                || item.CompletionSequence > journal.LastCompletionSequence)
            || journal.CompletedOperations
                .Select(item => item.CompletionSequence)
                .Distinct()
                .Count() != journal.CompletedOperations.Length)
        {
            throw new InvalidDataException(
                "The settings revision journal contains an invalid completion order.");
        }
    }
}
