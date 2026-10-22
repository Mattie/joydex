using Joydex.Secrets;

namespace Joydex.Tests;

public sealed class SecretsDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "joydex-diagnostics", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CorruptDiagnosticEvidenceIsReportedWithoutChangingOtherStorage()
    {
        var journal = new SecretsDiagnostics(_root);
        journal.TryAppend(Record("original"));
        File.AppendAllText(SecretsPaths.GetDiagnosticsPath(_root), "corrupt line\n");
        var snapshot = journal.Read();
        Assert.True(snapshot.Incomplete);
        Assert.Equal("original", Assert.Single(snapshot.Records).RequestId);
        Assert.False(File.Exists(SecretsPaths.GetPolicyPath(_root)));
        Assert.False(File.Exists(SecretsPaths.GetAuditPath(_root)));
    }

    [Fact]
    public void RetentionAndClearAffectOnlySupplementaryEvidence()
    {
        var journal = new SecretsDiagnostics(_root);
        journal.TryAppend(Record("old") with { Timestamp = DateTimeOffset.UtcNow.AddDays(-31) });
        journal.TryAppend(Record("current"));
        Assert.Equal("current", Assert.Single(journal.Read().Records).RequestId);
        var policy = new SecretsPolicyStore(SecretsPaths.GetPolicyPath(_root));
        var before = policy.Read(DateTimeOffset.UtcNow).Epoch;
        journal.Clear();
        Assert.Empty(journal.Read().Records);
        Assert.Equal(before, policy.Read(DateTimeOffset.UtcNow).Epoch);
    }

    [Fact]
    public void UnexpectedExceptionTextNeverBecomesDiagnosticMetadata()
    {
        var exception = new InvalidOperationException("CANARY_SECRET_must_never_be_logged");
        var failure = SecretsFailure.FromException(exception);
        Assert.Equal("internal_error", failure.Code);
        var journal = new SecretsDiagnostics(_root);
        journal.TryAppend(Record("unexpected") with { Failure = failure, ExceptionType = exception.GetType().Name, NativeError = exception.HResult });
        Assert.DoesNotContain("CANARY_SECRET", File.ReadAllText(SecretsPaths.GetDiagnosticsPath(_root)));
    }

    [Fact]
    public async Task BusyDiagnosticJournalDoesNotWaitAtTheAuthorizationBoundary()
    {
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = Task.Run(() =>
        {
            using var lease = SecretsFileLock.Acquire(SecretsPaths.GetDiagnosticsPath(_root));
            acquired.Set();
            release.Wait();
        });
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var append = Task.Run(() => new SecretsDiagnostics(_root).TryAppend(Record("busy")));
            await append.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(File.Exists(SecretsPaths.GetDiagnosticsPath(_root)));
        }
        finally { release.Set(); await owner; }
    }

    private static SecretsDiagnosticRecord Record(string request) => new(DateTimeOffset.UtcNow, "failure", Guid.NewGuid(),
        Environment.ProcessId, "test", SecretsConsentAvailability.Locked, 1, request, Guid.NewGuid(), SecretsFailure.ConsentUnavailable);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
