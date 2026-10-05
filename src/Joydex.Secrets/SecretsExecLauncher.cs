using System.Diagnostics;

namespace Joydex.Secrets;

/// <summary>Sanitized child-process result returned after an approved exec-inject operation.</summary>
public sealed record SecretsExecResult(int ExitCode, string StandardOutput, string StandardError,
    SecretsTaskReceipt? Task = null);

/// <summary>Starts a validated process with the caller environment and an already fetched value lease.</summary>
public sealed class SecretsExecLauncher
{
    private const int MaximumCapturedCharacters = 16 * 1024;
    private readonly SecretsAuditJournal _journal;
    private readonly Func<Process, bool>? _terminateProcessTree;

    public SecretsExecLauncher(SecretsAuditJournal journal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    internal SecretsExecLauncher(
        SecretsAuditJournal journal,
        Func<Process, bool> terminateProcessTree)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _terminateProcessTree = terminateProcessTree
            ?? throw new ArgumentNullException(nameof(terminateProcessTree));
    }

    /// <summary>
    /// Flushes launch intent before process creation and records enough identity to recover uncertainty.
    /// </summary>
    public async Task<SecretsExecResult> RunAsync(
        Guid attemptId,
        string requestId,
        string clientReference,
        string projectReference,
        IReadOnlyList<string> aliases,
        ResolvedExecOperation operation,
        SecretValueLease values,
        IReadOnlyDictionary<string, string> callerEnvironment,
        CancellationToken cancellationToken,
        Guid? clientRegistrationId = null,
        long? clientGeneration = null,
        Action<SecretsAuditRecord>? authorizeAndCommit = null,
        SecretsStandardStreams? streams = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(callerEnvironment);
        cancellationToken.ThrowIfCancellationRequested();
        using var operationFiles = ExecOperationCanonicalizer.AcquireCurrentFiles(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if ((operation.OutputDisclosure == SecretOutputDisclosure.Passthrough) != (streams is not null))
            throw new InvalidOperationException("The approved output mode does not match the standard streams.");
        var startInfo = CreateStartInfo(operation, values, callerEnvironment);
        startInfo.RedirectStandardInput = streams is not null;
        var audit = new SecretsAuditRecord(
            attemptId,
            SecretsAuditEventKind.LaunchCommitted,
            DateTimeOffset.UtcNow,
            requestId,
            clientReference,
            projectReference,
            aliases,
            operation.Identity.Digest,
            ClientRegistrationId: clientRegistrationId,
            ClientGeneration: clientGeneration);
        if (operation.Lifetime == SecretsExecutionLifetime.Detached)
        {
            var environment = startInfo.Environment.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
            try
            {
                var receipt = await SecretsDetachedTask.LaunchAsync(_journal.FilePath, operation, environment, audit,
                    () => { if (authorizeAndCommit is null) _journal.Append(audit); else authorizeAndCommit(audit); }, cancellationToken).ConfigureAwait(false);
                return new(receipt.State == "running" ? 0 : 2, string.Empty, string.Empty, receipt);
            }
            finally { environment.Clear(); startInfo.Environment.Clear(); }
        }
        if (authorizeAndCommit is null)
        {
            _journal.Append(audit);
        }
        else
        {
            authorizeAndCommit(audit);
        }

        SecretsOwnedProcess process;
        var started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process = SecretsOwnedProcess.Start(startInfo);
            started = true;
        }
        catch
        {
            if (!started)
            {
                _journal.Append(audit with
                {
                    Kind = SecretsAuditEventKind.FailedBeforeLaunch,
                    Timestamp = DateTimeOffset.UtcNow,
                    Outcome = cancellationToken.IsCancellationRequested
                        ? "cancelled-before-start"
                        : "process-start-failed",
                });
            }
            throw;
        }

        using var ownedProcess = process;
        var startedAt = process.StartTime.ToUniversalTime();
        _journal.Append(audit with
        {
            Kind = SecretsAuditEventKind.LaunchStarted,
            Timestamp = DateTimeOffset.UtcNow,
            ProcessId = process.Id,
            ProcessStartedAt = startedAt,
        });

        if (streams is not null)
            return await RelayAsync(process, streams, audit, startedAt, cancellationToken).ConfigureAwait(false);

        var outputTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        var errorTask = ReadBoundedAsync(process.StandardError, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var terminated = Stop(process);
            await ObserveCaptureAsync(outputTask).ConfigureAwait(false);
            await ObserveCaptureAsync(errorTask).ConfigureAwait(false);
            _journal.Append(audit with
            {
                Kind = terminated
                    ? SecretsAuditEventKind.LaunchTerminated
                    : SecretsAuditEventKind.LaunchUnconfirmed,
                Timestamp = DateTimeOffset.UtcNow,
                Outcome = terminated ? "cancelled-after-start" : "cancellation-kill-unconfirmed",
                ProcessId = process.Id,
                ProcessStartedAt = startedAt,
            });
            throw new SecretsExecutionCanceledException(
                launchUnconfirmed: !terminated,
                cancellationToken);
        }
        var output = Redact(await outputTask.ConfigureAwait(false), operation, values);
        var error = Redact(await errorTask.ConfigureAwait(false), operation, values);
        _journal.Append(audit with
        {
            Kind = SecretsAuditEventKind.LaunchCompleted,
            Timestamp = DateTimeOffset.UtcNow,
            Outcome = process.ExitCode == 0 ? "exit-success" : "exit-failure",
            ProcessId = process.Id,
            ProcessStartedAt = startedAt,
        });
        return new SecretsExecResult(process.ExitCode, output, error);
    }

    private async Task<SecretsExecResult> RelayAsync(SecretsOwnedProcess process, SecretsStandardStreams streams,
        SecretsAuditRecord audit, DateTime startedAt, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task FeedInput()
        {
            try { await streams.Input.CopyToAsync(process.StandardInput.BaseStream, lifetime.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { try { process.StandardInput.Close(); } catch (InvalidOperationException) { } }
        }
        var input = FeedInput();
        try
        {
            async Task Pump(Stream source, Stream destination)
            {
                try { await source.CopyToAsync(destination, lifetime.Token).ConfigureAwait(false); }
                catch { lifetime.Cancel(); throw; }
            }
            await Task.WhenAll(
                Pump(process.StandardOutput.BaseStream, streams.Output),
                Pump(process.StandardError.BaseStream, streams.Error),
                process.WaitForExitAsync(lifetime.Token)).ConfigureAwait(false);
            _journal.Append(audit with { Kind = SecretsAuditEventKind.LaunchCompleted,
                Timestamp = DateTimeOffset.UtcNow, ProcessId = process.Id, ProcessStartedAt = startedAt,
                Outcome = process.ExitCode == 0 ? "exit-success" : "exit-failure" });
            return new(process.ExitCode, string.Empty, string.Empty);
        }
        catch
        {
            var stopped = Stop(process);
            _journal.Append(audit with { Kind = stopped ? SecretsAuditEventKind.LaunchTerminated : SecretsAuditEventKind.LaunchUnconfirmed,
                Timestamp = DateTimeOffset.UtcNow, ProcessId = process.Id, ProcessStartedAt = startedAt });
            throw new SecretsExecutionCanceledException(!stopped, token);
        }
        finally { lifetime.Cancel(); await input.ConfigureAwait(false); }
    }

    private bool Stop(SecretsOwnedProcess process)
    {
        var reported = _terminateProcessTree?.Invoke(process.Process) ?? true;
        return process.TerminateAndConfirm() && reported;
    }

    /// <summary>Last-resort shutdown of executions owned by this process.</summary>
    public static void TerminateOwnedExecutions() => SecretsOwnedProcess.TerminateAll();

    private static async Task ObserveCaptureAsync(Task<CapturedOutput> capture)
    {
        try
        {
            _ = await capture.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException
            or TimeoutException
            or IOException
            or ObjectDisposedException)
        {
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        ResolvedExecOperation operation,
        SecretValueLease values,
        IReadOnlyDictionary<string, string> callerEnvironment)
    {
        var info = new ProcessStartInfo
        {
            FileName = operation.Executable,
            WorkingDirectory = operation.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in operation.Arguments) info.ArgumentList.Add(argument);
        info.Environment.Clear();
        foreach (var variable in callerEnvironment)
            info.Environment[variable.Key] = variable.Value;
        foreach (var mapping in operation.SecretEnvironment)
        {
            info.Environment[mapping.Key] = values.GetValue(mapping.Value);
        }
        return info;
    }

    private static async Task<CapturedOutput> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var result = new System.Text.StringBuilder();
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            var remaining = MaximumCapturedCharacters - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        return new(result.ToString(), truncated);
    }

    private static string Redact(
        CapturedOutput capture,
        ResolvedExecOperation operation,
        SecretValueLease values)
    {
        if (operation.OutputDisclosure == SecretOutputDisclosure.None) return string.Empty;
        if (capture.Truncated) return "[OUTPUT TRUNCATED]";
        var output = capture.Text;
        var secrets = operation.SecretEnvironment.Values
            .Distinct(StringComparer.Ordinal)
            .Select(values.GetValue)
            .Where(secret => secret.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret => secret.Length);
        foreach (var secret in secrets)
        {
            output = output.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }
        return output;
    }

    private sealed record CapturedOutput(string Text, bool Truncated);
}

internal sealed class SecretsExecutionCanceledException(
    bool launchUnconfirmed,
    CancellationToken cancellationToken)
    : OperationCanceledException(
        "The approved command was cancelled after launch.",
        innerException: null,
        cancellationToken)
{
    public bool LaunchUnconfirmed { get; } = launchUnconfirmed;
}
