using System.Text.Json;

namespace Joydex.Secrets;

/// <summary>Supplementary metadata; never contains commands, environments, credentials or raw exception messages.</summary>
public sealed record SecretsDiagnosticRecord(
    DateTimeOffset Timestamp,
    string Kind,
    Guid BrokerInstance,
    int ProcessId,
    string BrokerVersion,
    SecretsConsentAvailability ConsentAvailability,
    int SessionId,
    string? RequestId = null,
    Guid? AttemptId = null,
    SecretsFailure? Failure = null,
    string? ExceptionType = null,
    int? NativeError = null,
    int SchemaVersion = 1);

public sealed record SecretsDiagnosticSnapshot(IReadOnlyList<SecretsDiagnosticRecord> Records, bool Incomplete);

/// <summary>A bounded, best-effort journal independent of policy and the authoritative launch ledger.</summary>
public sealed class SecretsDiagnostics
{
    private const int MaximumRecords = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _path;

    public SecretsDiagnostics(string dataRoot) => _path = SecretsPaths.GetDiagnosticsPath(dataRoot);

    /// <summary>Reads a snapshot without creating files, connecting to a broker, or enrolling a requester.</summary>
    public SecretsDiagnosticSnapshot Read()
    {
        var records = new Queue<SecretsDiagnosticRecord>();
        var incomplete = false;
        try
        {
            if (!File.Exists(_path)) return new([], false);
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    if (line.Length > 16_384) { incomplete = true; continue; }
                    var record = JsonSerializer.Deserialize<SecretsDiagnosticRecord>(line, JsonOptions);
                    if (record is null || record.SchemaVersion != 1) { incomplete = true; continue; }
                    records.Enqueue(record);
                    if (records.Count > MaximumRecords) records.Dequeue();
                }
                catch (JsonException) { incomplete = true; }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            incomplete = true;
        }
        return new(records.ToArray(), incomplete);
    }

    /// <summary>Attempts to save evidence. Failure must never change whether an approved command can run.</summary>
    public void TryAppend(SecretsDiagnosticRecord record)
    {
        try
        {
            using var lease = SecretsFileLock.Acquire(_path, TimeSpan.Zero);
            var retained = Read().Records.Where(item => item.Timestamp >= record.Timestamp.AddDays(-30))
                .TakeLast(MaximumRecords - 1).Append(record);
            Write(retained);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException) { }
    }

    public void Clear()
    {
        using var lease = SecretsFileLock.Acquire(_path);
        Write([]);
    }

    private void Write(IEnumerable<SecretsDiagnosticRecord> records)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".diagnostics.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (var record in records)
                {
                    JsonSerializer.Serialize(stream, record, JsonOptions);
                    stream.WriteByte((byte)'\n');
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
