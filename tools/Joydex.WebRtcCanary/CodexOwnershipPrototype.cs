using System.Text.Json;

namespace Joydex.WebRtcCanary;

/// <summary>
/// Throwaway logic prototype for one question: does a Joydex-owned App Server retain a durable
/// task's writer until process exit, reject a rival process, and permit a clean handoff afterward?
/// </summary>
internal sealed class CodexOwnershipPrototype
{
    private readonly string _codexPath;
    private readonly string? _configuredThreadId;
    private readonly string _configuredThreadTitle;
    private readonly string _cwd;
    private OwnershipPrototypeState _state = OwnershipPrototypeState.Initial;
    private bool _interactive;
    private bool _ownerProofPassed;
    private ConfiguredTargetProbeResult? _targetProbeResult;

    public CodexOwnershipPrototype(
        string codexPath,
        string? configuredThreadId,
        string configuredThreadTitle,
        string cwd)
    {
        _codexPath = codexPath;
        _configuredThreadId = configuredThreadId;
        _configuredThreadTitle = configuredThreadTitle;
        _cwd = cwd;
    }

    public async Task<int> RunAutomatedAsync(CancellationToken cancellationToken)
    {
        _interactive = false;
        Render();
        _ownerProofPassed = await RunOwnerProofAsync(cancellationToken).ConfigureAwait(false);
        _targetProbeResult = await ProbeConfiguredTargetAsync(cancellationToken).ConfigureAwait(false);
        return GetExitCode();
    }

    public async Task<int> RunInteractiveAsync(CancellationToken cancellationToken)
    {
        _interactive = true;
        Render();

        while (!cancellationToken.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true).Key;
            switch (key)
            {
                case ConsoleKey.O:
                    _ownerProofPassed = await RunOwnerProofAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case ConsoleKey.P:
                    _targetProbeResult = await ProbeConfiguredTargetAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case ConsoleKey.Q:
                    return GetExitCode();
            }
        }

        return 0;
    }

    private async Task<bool> RunOwnerProofAsync(CancellationToken cancellationToken)
    {
        Dispatch(new ProofReset("Starting disposable owner-mechanism proof."));
        CodexAppServerClient? owner = null;
        CodexAppServerClient? rival = null;
        CodexAppServerClient? handoff = null;
        string? threadId = null;
        var deleted = false;

        try
        {
            owner = CreateClient();
            await owner.StartAsync(cancellationToken).ConfigureAwait(false);
            Dispatch(new OwnerStarted(owner.ProcessId ?? throw new InvalidOperationException("Owner PID unavailable.")));

            var startResult = await owner.RequestAsync(
                "thread/start",
                new
                {
                    ephemeral = false,
                    cwd = _cwd,
                    approvalPolicy = "never",
                    sandbox = "read-only",
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            threadId = ReadThreadId(startResult, "thread/start");

            var canaryName = $"Joydex Ownership Prototype {DateTimeOffset.Now:yyyy-MM-dd HHmmss}";
            await owner.RequestAsync(
                "thread/name/set",
                new { threadId, name = canaryName },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            Dispatch(new TaskAcquired(threadId));

            rival = CreateClient();
            await rival.StartAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await rival.RequestAsync(
                    "thread/resume",
                    new { threadId, excludeTurns = true },
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The rival App Server unexpectedly resumed a task owned by another process.");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("active writer", StringComparison.OrdinalIgnoreCase))
            {
                Dispatch(new RivalBlocked(exception.Message));
            }

            await rival.DisposeAsync().ConfigureAwait(false);
            rival = null;
            await owner.DisposeAsync().ConfigureAwait(false);
            owner = null;
            Dispatch(new OwnerStopped());

            handoff = CreateClient();
            await handoff.StartAsync(cancellationToken).ConfigureAwait(false);
            await handoff.RequestAsync(
                "thread/resume",
                new { threadId, excludeTurns = true },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            Dispatch(new HandoffSucceeded());

            await handoff.RequestAsync(
                "thread/delete",
                new { threadId },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);
            deleted = true;
            Dispatch(new CanaryTaskDeleted());
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Dispatch(new PrototypeFailed(exception.Message));
            return false;
        }
        finally
        {
            if (handoff is not null)
            {
                await handoff.DisposeAsync().ConfigureAwait(false);
            }

            if (rival is not null)
            {
                await rival.DisposeAsync().ConfigureAwait(false);
            }

            if (owner is not null)
            {
                await owner.DisposeAsync().ConfigureAwait(false);
            }

            if (threadId is not null && !deleted)
            {
                await TryDeleteCanaryAsync(threadId).ConfigureAwait(false);
            }
        }
    }

    private async Task<ConfiguredTargetProbeResult> ProbeConfiguredTargetAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var probe = CreateClient();
            await probe.StartAsync(cancellationToken).ConfigureAwait(false);
            var threadId = await ResolveConfiguredThreadIdAsync(probe, cancellationToken).ConfigureAwait(false);

            try
            {
                await probe.RequestAsync(
                    "thread/resume",
                    new { threadId, excludeTurns = true },
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);
                Dispatch(new ConfiguredTargetProbed(
                    $"AVAILABLE: '{_configuredThreadTitle}' ({threadId}) was acquired without starting a turn; probe exit releases it."));
                return ConfiguredTargetProbeResult.Available;
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("active writer", StringComparison.OrdinalIgnoreCase))
            {
                Dispatch(new ConfiguredTargetProbed(
                    $"DESKTOP-OWNED: '{_configuredThreadTitle}' ({threadId}) rejected the probe: {exception.Message}"));
                return ConfiguredTargetProbeResult.OwnedByAnotherAppServer;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Dispatch(new PrototypeFailed($"Configured task probe failed: {exception.Message}"));
            return ConfiguredTargetProbeResult.Failed;
        }
    }

    private async Task<string> ResolveConfiguredThreadIdAsync(
        CodexAppServerClient client,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_configuredThreadId))
        {
            return _configuredThreadId;
        }

        var matches = new List<(string Id, string? Name)>();
        string? cursor = null;
        do
        {
            var result = await client.RequestAsync(
                "thread/list",
                new
                {
                    searchTerm = _configuredThreadTitle,
                    archived = false,
                    limit = 100,
                    cursor,
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);

            if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("thread/list returned no task data.");
            }

            matches.AddRange(data.EnumerateArray()
                .Select(thread => new
                {
                    Id = thread.TryGetProperty("id", out var id) ? id.GetString() : null,
                    Name = thread.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null,
                })
                .Where(thread =>
                    string.Equals(thread.Name, _configuredThreadTitle, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(thread.Id))
                .Select(thread => (thread.Id!, thread.Name)));

            cursor = result.TryGetProperty("nextCursor", out var nextCursor) &&
                     nextCursor.ValueKind == JsonValueKind.String
                ? nextCursor.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return matches.Count switch
        {
            0 => throw new InvalidOperationException(
                $"Could not find an active task named '{_configuredThreadTitle}'."),
            1 => matches[0].Id,
            _ => throw new InvalidOperationException(
                $"Found {matches.Count} active tasks named '{_configuredThreadTitle}'. Pass --thread-id to select one explicitly."),
        };
    }

    private async Task TryDeleteCanaryAsync(string threadId)
    {
        try
        {
            await using var cleanup = CreateClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await cleanup.StartAsync(timeout.Token).ConfigureAwait(false);
            await cleanup.RequestAsync(
                "thread/resume",
                new { threadId, excludeTurns = true },
                TimeSpan.FromSeconds(20),
                timeout.Token).ConfigureAwait(false);
            await cleanup.RequestAsync(
                "thread/delete",
                new { threadId },
                TimeSpan.FromSeconds(15),
                timeout.Token).ConfigureAwait(false);
            Console.Error.WriteLine($"Cleanup deleted disposable canary task {threadId}.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"WARNING: disposable canary task {threadId} could not be deleted: {exception.Message}");
        }
    }

    private CodexAppServerClient CreateClient() => new(_codexPath, requestAttestation: false);

    private int GetExitCode()
    {
        if (!_ownerProofPassed || _targetProbeResult is null or ConfiguredTargetProbeResult.Failed)
        {
            return 1;
        }

        return _targetProbeResult == ConfiguredTargetProbeResult.OwnedByAnotherAppServer ? 3 : 0;
    }

    private void Dispatch(OwnershipPrototypeEvent prototypeEvent)
    {
        _state = _state.Apply(prototypeEvent);
        Render();
    }

    private void Render()
    {
        if (_interactive && !Console.IsOutputRedirected)
        {
            Console.Clear();
        }

        Console.WriteLine("JOYDEXOWNER — throwaway App Server writer-lifetime prototype");
        Console.WriteLine();
        Console.WriteLine($"Stage:             {_state.Stage}");
        Console.WriteLine($"Last action:       {_state.LastAction}");
        Console.WriteLine($"Owner PID:         {_state.OwnerProcessId?.ToString() ?? "—"}");
        Console.WriteLine($"Canary task:       {_state.CanaryThreadId ?? "—"}");
        Console.WriteLine($"Rival result:      {_state.RivalResult ?? "—"}");
        Console.WriteLine($"Configured target: {_state.TargetResult ?? $"not probed ({_configuredThreadTitle})"}");
        Console.WriteLine();
        Console.WriteLine("[O] run disposable ownership + handoff proof  [P] probe configured task  [Q] quit");
        Console.WriteLine();
    }

    private static string ReadThreadId(JsonElement result, string method)
    {
        if (result.TryGetProperty("thread", out var thread) &&
            thread.TryGetProperty("id", out var idElement) &&
            idElement.GetString() is { Length: > 0 } id)
        {
            return id;
        }

        throw new InvalidOperationException($"{method} returned no task id.");
    }
}

internal enum ConfiguredTargetProbeResult
{
    Available,
    OwnedByAnotherAppServer,
    Failed,
}
