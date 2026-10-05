using System.Text.Json;
using System.Text.Json.Serialization;

namespace Joydex.Secrets;

/// <summary>Lifetime and rolling 24-hour counts that remain after visible activity is cleared.</summary>
public sealed record SecretsMetricsSnapshot(
    long RequestsTotal,
    long Requests24Hours,
    long ExecutionsTotal,
    long Executions24Hours,
    long ApprovedTotal,
    long Approved24Hours,
    long DeniedTotal,
    long Denied24Hours);

/// <summary>Maintains bounded minute buckets plus lifetime Secrets counters.</summary>
public sealed class SecretsMetricsStore
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumBuckets = 1_441;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
    private readonly object _gate = new();
    private readonly string _path;

    public SecretsMetricsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public SecretsMetricsSnapshot Read(DateTimeOffset now)
    {
        lock (_gate)
        {
            return SecretsFileLock.WithLock(_path, () =>
            {
                var document = Load(_path);
                if (Prune(document, now)) Save(document);
                return Snapshot(document, now);
            });
        }
    }

    internal void Record(SecretsAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var delta = Delta.From(record);
        if (delta is null) return;
        lock (_gate)
        {
            SecretsFileLock.WithLock(_path, () =>
            {
                var document = Load(_path);
                Prune(document, record.Timestamp);
                var minute = StartOfMinute(record.Timestamp);
                var bucket = document.Buckets.SingleOrDefault(candidate => candidate.Minute == minute);
                if (bucket is null)
                {
                    if (document.Buckets.Count >= MaximumBuckets)
                    {
                        document.Buckets.RemoveAt(0);
                    }
                    bucket = new MetricsBucket { Minute = minute };
                    document.Buckets.Add(bucket);
                    document.Buckets.Sort((left, right) => left.Minute.CompareTo(right.Minute));
                }
                document.RequestsTotal = checked(document.RequestsTotal + delta.Requests);
                document.ExecutionsTotal = checked(document.ExecutionsTotal + delta.Executions);
                document.ApprovedTotal = checked(document.ApprovedTotal + delta.Approved);
                document.DeniedTotal = checked(document.DeniedTotal + delta.Denied);
                bucket.Requests = checked(bucket.Requests + delta.Requests);
                bucket.Executions = checked(bucket.Executions + delta.Executions);
                bucket.Approved = checked(bucket.Approved + delta.Approved);
                bucket.Denied = checked(bucket.Denied + delta.Denied);
                Save(document);
                return true;
            });
        }
    }

    private void Save(MetricsDocument document) => SecretsAtomicFile.WriteJson(_path, document, JsonOptions);

    private static MetricsDocument Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<MetricsDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("The Secrets metrics are empty.");
            if (document.SchemaVersion != CurrentSchemaVersion
                || document.RequestsTotal < 0
                || document.ExecutionsTotal < 0
                || document.ApprovedTotal < 0
                || document.DeniedTotal < 0
                || document.Buckets is null
                || document.Buckets.Count > MaximumBuckets
                || document.Buckets.Select(bucket => bucket.Minute).Distinct().Count() != document.Buckets.Count
                || document.Buckets.Any(bucket => bucket.Minute == default
                    || bucket.Minute.Offset != TimeSpan.Zero
                    || bucket.Minute.Second != 0
                    || bucket.Minute.Millisecond != 0
                    || bucket.Requests < 0
                    || bucket.Executions < 0
                    || bucket.Approved < 0
                    || bucket.Denied < 0))
            {
                throw new InvalidDataException("The Secrets metrics are invalid.");
            }
            document.Buckets.Sort((left, right) => left.Minute.CompareTo(right.Minute));
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Secrets metrics are malformed.", exception);
        }
    }

    private static bool Prune(MetricsDocument document, DateTimeOffset now)
    {
        var cutoff = StartOfMinute(now - TimeSpan.FromHours(24));
        return document.Buckets.RemoveAll(bucket => bucket.Minute < cutoff) > 0;
    }

    private static SecretsMetricsSnapshot Snapshot(MetricsDocument document, DateTimeOffset now)
    {
        var cutoff = StartOfMinute(now - TimeSpan.FromHours(24));
        var recent = document.Buckets.Where(bucket => bucket.Minute >= cutoff).ToArray();
        return new(
            document.RequestsTotal,
            recent.Sum(bucket => bucket.Requests),
            document.ExecutionsTotal,
            recent.Sum(bucket => bucket.Executions),
            document.ApprovedTotal,
            recent.Sum(bucket => bucket.Approved),
            document.DeniedTotal,
            recent.Sum(bucket => bucket.Denied));
    }

    private static DateTimeOffset StartOfMinute(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    private sealed class MetricsDocument
    {
        public int SchemaVersion { get; init; } = CurrentSchemaVersion;
        public long RequestsTotal { get; set; }
        public long ExecutionsTotal { get; set; }
        public long ApprovedTotal { get; set; }
        public long DeniedTotal { get; set; }
        public List<MetricsBucket> Buckets { get; set; } = [];
    }

    private sealed class MetricsBucket
    {
        public DateTimeOffset Minute { get; set; }
        public long Requests { get; set; }
        public long Executions { get; set; }
        public long Approved { get; set; }
        public long Denied { get; set; }
    }

    private sealed record Delta(long Requests, long Executions, long Approved, long Denied)
    {
        public static Delta? From(SecretsAuditRecord record) => record.Kind switch
        {
            SecretsAuditEventKind.RequestReceived => new(1, 0, 0, 0),
            SecretsAuditEventKind.LaunchStarted => new(0, 1, 0, 0),
            SecretsAuditEventKind.RequestOutcome or SecretsAuditEventKind.ConsentDecision
                when string.Equals(record.Outcome, "allowed", StringComparison.Ordinal) => new(0, 0, 1, 0),
            SecretsAuditEventKind.RequestOutcome or SecretsAuditEventKind.ConsentDecision
                when string.Equals(record.Outcome, "denied", StringComparison.Ordinal) => new(0, 0, 0, 1),
            _ => null,
        };
    }
}
