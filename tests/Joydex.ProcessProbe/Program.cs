using System.Text.Json;
using System.Text.Json.Serialization;
using Joydex.Contracts;
using Joydex.Ipc;
using Joydex.RuntimeHost;
using Joydex.RuntimeHost.Settings;

namespace Joydex.ProcessProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length < 2)
            {
                throw new ArgumentException("Expected a command and settings root.");
            }

            var command = args[0];
            var paths = PathsFor(args[1]);
            return command switch
            {
                "crash-apply" => await CrashApplyAsync(paths, args),
                "recover" => await RecoverAsync(paths, args),
                "recover-replay" => await RecoverReplayAsync(paths, args),
                "apply-lost-reply" => await ApplyLostReplyAsync(paths, args),
                "external-conflict" => await ExternalConflictAsync(paths, args),
                "commit-pending-voice" => await CommitPendingVoiceAsync(paths, args),
                "apply-task-alerts" => await ApplyTaskAlertsAsync(paths, args),
                "runtime-apply-crash" => await RuntimeApplyCrashAsync(args),
                "runtime-capture-crash" => await RuntimeCaptureCrashAsync(args),
                "runtime-inspect" => await RuntimeInspectAsync(args),
                "runtime-operation" => await RuntimeOperationAsync(args),
                "runtime-reconnect" => await RuntimeReconnectAsync(args),
                "runtime-apply-client-loss" => await RuntimeApplyClientLossAsync(args),
                _ => throw new ArgumentException($"Unknown command '{command}'."),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<int> RuntimeApplyCrashAsync(string[] args)
    {
        var callbacks = new CapturingCallbacks();
        await using var connection = await ConnectRuntimeAsync(args, callbacks);
        var before = connection.AttachResult.Snapshot;
        var bank = int.Parse(args[5]);
        var prepared = await connection.PrepareSettingsAsync(
            new PrepareSettingsRequest(before.Settings.Revision,
                new SettingsPatch(TaskAlerts: before.Settings.Desired.TaskAlerts with { Bank = bank })),
            CancellationToken.None);
        var operationId = Guid.NewGuid();
        var applied = await connection.ApplySettingsAsync(
            new ApplySettingsRequest(operationId, prepared.PreparationToken!), CancellationToken.None);
        Write(new RuntimeApplied(before, applied));
        Environment.Exit(137);
        return 137;
    }

    private static async Task<int> RuntimeCaptureCrashAsync(string[] args)
    {
        var callbacks = new CapturingCallbacks();
        await using var connection = await ConnectRuntimeAsync(args, callbacks);
        var snapshot = connection.AttachResult.Snapshot;
        var source = AssertSingle(snapshot.Input.Sources);
        var capture = await connection.BeginInputCaptureAsync(
            new RuntimeCaptureRequest(source.SourceId, "process-validation", source.Generation),
            CancellationToken.None);
        Write(new RuntimeCapture(snapshot, capture));
        Environment.Exit(137);
        return 137;
    }

    private static async Task<int> RuntimeInspectAsync(string[] args)
    {
        var callbacks = new CapturingCallbacks();
        await using var connection = await ConnectRuntimeAsync(args, callbacks);
        var capture = await connection.GetInputCaptureAsync(Guid.Parse(args[5]), CancellationToken.None);
        Write(new RuntimeInspection(connection.AttachResult, capture));
        return 0;
    }

    private static async Task<int> RuntimeOperationAsync(string[] args)
    {
        var callbacks = new CapturingCallbacks();
        await using var connection = await ConnectRuntimeAsync(args, callbacks);
        var operation = await connection.GetSettingsOperationAsync(Guid.Parse(args[5]), CancellationToken.None);
        Write(new RuntimeOperation(connection.AttachResult, operation));
        return 0;
    }

    private static async Task<int> RuntimeReconnectAsync(string[] args)
    {
        var callbacks = new CapturingCallbacks();
        var previousEpoch = Guid.Parse(args[5]);
        var afterSequence = long.Parse(args[6]);
        var staleRevision = long.Parse(args[7]);
        await using var connection = await ConnectRuntimeAsync(args, callbacks, previousEpoch, afterSequence);
        await callbacks.WaitThroughAsync(connection.AttachResult.Snapshot.EventCursor, TimeSpan.FromSeconds(5));
        var current = connection.AttachResult.Snapshot.Settings.Desired.TaskAlerts;
        var stale = await connection.PrepareSettingsAsync(
            new PrepareSettingsRequest(staleRevision,
                new SettingsPatch(TaskAlerts: current with { Bank = current.Bank == 5 ? 4 : 5 })),
            CancellationToken.None);
        Write(new RuntimeReconnect(connection.AttachResult, callbacks.SnapshotEvents(), stale));
        return 0;
    }

    private static async Task<int> RuntimeApplyClientLossAsync(string[] args)
    {
        var callbacks = new CapturingCallbacks();
        await using var connection = await ConnectRuntimeAsync(args, callbacks);
        var snapshot = connection.AttachResult.Snapshot;
        var operationId = Guid.Parse(args[5]);
        var bank = int.Parse(args[6]);
        var prepared = await connection.PrepareSettingsAsync(
            new PrepareSettingsRequest(snapshot.Settings.Revision,
                new SettingsPatch(TaskAlerts: snapshot.Settings.Desired.TaskAlerts with { Bank = bank })),
            CancellationToken.None);
        Write(new RuntimePrepared(snapshot, operationId, prepared.PreparationToken!));
        _ = connection.ApplySettingsAsync(
            new ApplySettingsRequest(operationId, prepared.PreparationToken!), CancellationToken.None);
        var taskAlertsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "task-alerts.json");
        await WaitUntilAsync(() => File.Exists(taskAlertsPath)
            && Joydex.Core.TaskAlerts.TaskAlertPreferencesStore.LoadOrCreate(taskAlertsPath).Bank == bank,
            TimeSpan.FromSeconds(10));
        Environment.Exit(137);
        return 137;
    }

    private static async Task<RuntimeIpcClientConnection> ConnectRuntimeAsync(
        string[] args,
        IRuntimeRpcClient callbacks,
        Guid? previousEpoch = null,
        long? afterSequence = null)
    {
        var configurationPath = Path.GetFullPath(args[1]);
        var endpoint = RuntimeIpcEndpoint.CreateSynthetic(
            Path.GetDirectoryName(configurationPath)!, configurationPath, args[2]);
        if (!string.Equals(endpoint.DataRootId, args[3], StringComparison.Ordinal))
        {
            throw new InvalidDataException("The supplied data-root identity does not match the selected configuration.");
        }
        return await RuntimeIpcClient.ConnectAsync(endpoint,
            new RuntimeAttachRequest(RuntimeProtocol.MajorVersion, RuntimeProtocol.MinorVersion,
                RuntimeClientKind.HeadlessTest, RuntimeInstanceKind.Synthetic, endpoint.DataRootId,
                args[4], previousEpoch, afterSequence), callbacks,
            cancellationToken: CancellationToken.None);
    }

    private static T AssertSingle<T>(T[] values) => values.Length == 1
        ? values[0]
        : throw new InvalidDataException($"Expected one value, found {values.Length}.");

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for committed settings.");
            await Task.Delay(20);
        }
    }

    private static async Task<int> CrashApplyAsync(RuntimeSettingsPaths paths, string[] args)
    {
        var checkpoint = Enum.Parse<SettingsTransactionCheckpoint>(args[2], ignoreCase: true);
        var operationId = Guid.Parse(args[3]);
        var bank = int.Parse(args[4]);
        await using var coordinator = CreateCoordinator(paths, observer: new ExitingObserver(checkpoint));
        using var client = await AttachAsync(coordinator);
        var prepared = await PrepareCrashChangeAsync(coordinator, client, bank);
        Write(new Prepared(prepared.PreparationToken!, client.State.Snapshot.Revision));
        await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken!),
            CancellationToken.None);
        throw new InvalidOperationException("The crash checkpoint was not reached.");
    }

    private static async Task<int> RecoverAsync(RuntimeSettingsPaths paths, string[] args)
    {
        var operationId = Guid.Parse(args[2]);
        await using var coordinator = CreateCoordinator(paths);
        using var client = await AttachAsync(coordinator);
        var operation = await coordinator.GetOperationAsync(client.ConnectionId, operationId, CancellationToken.None);
        Write(new Recovery(operation, client.State.Snapshot));
        return 0;
    }

    private static async Task<int> RecoverReplayAsync(RuntimeSettingsPaths paths, string[] args)
    {
        var operationId = Guid.Parse(args[2]);
        var token = args[3];
        await using var coordinator = CreateCoordinator(paths);
        using var client = await AttachAsync(coordinator);
        var operation = await coordinator.GetOperationAsync(client.ConnectionId, operationId, CancellationToken.None);
        var replay = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(operationId, token),
            CancellationToken.None);
        Write(new Replay(operation, replay));
        return 0;
    }

    private static async Task<int> ApplyLostReplyAsync(RuntimeSettingsPaths paths, string[] args)
    {
        var operationId = Guid.Parse(args[2]);
        var bank = int.Parse(args[3]);
        await using var coordinator = CreateCoordinator(paths);
        using var client = await AttachAsync(coordinator);
        var prepared = await PrepareBankAsync(coordinator, client, bank);
        Write(new Prepared(prepared.PreparationToken!, client.State.Snapshot.Revision));
        _ = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken!),
            CancellationToken.None);
        Environment.Exit(0);
        return 0;
    }

    private static async Task<int> ExternalConflictAsync(RuntimeSettingsPaths paths, string[] args)
    {
        var operationId = Guid.Parse(args[2]);
        var bank = int.Parse(args[3]);
        await using var coordinator = CreateCoordinator(paths);
        using var client = await AttachAsync(coordinator);
        var prepared = await PrepareBankAsync(coordinator, client, bank);
        Write(new Prepared(prepared.PreparationToken!, client.State.Snapshot.Revision));
        var signal = await Console.In.ReadLineAsync();
        if (!string.Equals(signal, "apply", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Expected the apply signal.");
        }
        var result = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken!),
            CancellationToken.None);
        Write(result);
        return 0;
    }

    private static async Task<int> CommitPendingVoiceAsync(RuntimeSettingsPaths paths, string[] args)
    {
        var operationId = Guid.Parse(args[2]);
        var label = args[3];
        await using var coordinator = CreateCoordinator(paths, planner: new VoicePendingPlanner());
        using var client = await AttachAsync(coordinator);
        var prepared = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                client.State.Snapshot.Revision,
                new SettingsPatch(Voice: client.State.Snapshot.Desired.Voice with { PinnedTaskLabel = label })),
            CancellationToken.None);
        var result = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken!),
            CancellationToken.None);
        Write(result);
        return 0;
    }

    private static async Task<int> ApplyTaskAlertsAsync(RuntimeSettingsPaths paths, string[] args)
    {
        var operationId = Guid.Parse(args[2]);
        var bank = int.Parse(args[3]);
        var activator = new CapturingActivator();
        await using var coordinator = CreateCoordinator(paths, activator: activator);
        using var client = await AttachAsync(coordinator);
        var prepared = await PrepareBankAsync(coordinator, client, bank);
        var result = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken!),
            CancellationToken.None);
        Write(new ActivationCapture(result, activator.Candidates.ToArray()));
        return 0;
    }

    private static RuntimeSettingsCoordinator CreateCoordinator(
        RuntimeSettingsPaths paths,
        ISettingsImpactPlanner? planner = null,
        ISettingsActivator? activator = null,
        ISettingsTransactionObserver? observer = null) => RuntimeSettingsCoordinator.Create(
            paths,
            planner ?? new DefaultSettingsImpactPlanner(),
            activator ?? new ImmediateSettingsActivator(),
            TimeProvider.System,
            CancellationToken.None,
            new SettingsDocumentIo(),
            observer ?? SettingsTransactionObserver.Instance,
            new RuntimeEventHub(Guid.NewGuid()));

    private static async Task<PrepareSettingsResult> PrepareBankAsync(
        RuntimeSettingsCoordinator coordinator,
        AttachedClient client,
        int bank)
    {
        var result = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                client.State.Snapshot.Revision,
                new SettingsPatch(TaskAlerts: client.State.Snapshot.Desired.TaskAlerts with { Bank = bank })),
            CancellationToken.None);
        if (result.Status != SettingsPrepareStatus.Prepared)
        {
            throw new InvalidOperationException($"Prepare failed with {result.Status}: {string.Join(' ', result.Errors)}");
        }
        return result;
    }

    private static async Task<PrepareSettingsResult> PrepareCrashChangeAsync(
        RuntimeSettingsCoordinator coordinator,
        AttachedClient client,
        int bank)
    {
        var result = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                client.State.Snapshot.Revision,
                new SettingsPatch(
                    Voice: client.State.Snapshot.Desired.Voice with { PinnedTaskLabel = "crash candidate" },
                    TaskAlerts: client.State.Snapshot.Desired.TaskAlerts with { Bank = bank })),
            CancellationToken.None);
        if (result.Status != SettingsPrepareStatus.Prepared)
        {
            throw new InvalidOperationException($"Prepare failed with {result.Status}: {string.Join(' ', result.Errors)}");
        }
        return result;
    }

    private static async Task<AttachedClient> AttachAsync(RuntimeSettingsCoordinator coordinator)
    {
        var connectionId = $"probe-{Guid.NewGuid():N}";
        var state = await coordinator.AttachAsync(
            connectionId,
            previousEpoch: null,
            afterSequence: null,
            _ => { },
            CancellationToken.None);
        return new AttachedClient(connectionId, state);
    }

    private static RuntimeSettingsPaths PathsFor(string root)
    {
        root = Path.GetFullPath(root);
        return new RuntimeSettingsPaths(
            Path.Combine(root, "companion.json"),
            Path.Combine(root, "voice.json"),
            Path.Combine(root, "pebble-index.json"),
            Path.Combine(root, "task-alerts.json"),
            Path.Combine(root, "runtime-settings-journal.json"));
    }

    private static void Write<T>(T message)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(message, JsonOptions));
        Console.Out.Flush();
    }

    private sealed class ExitingObserver(SettingsTransactionCheckpoint checkpoint) : ISettingsTransactionObserver
    {
        public void OnCheckpoint(SettingsTransactionCheckpoint current, SettingsAggregateId? aggregate = null)
        {
            if (current != checkpoint)
            {
                return;
            }
            Console.Out.WriteLine(JsonSerializer.Serialize(new Checkpoint(current, aggregate), JsonOptions));
            Console.Out.Flush();
            Environment.Exit(137);
        }
    }

    private sealed class VoicePendingPlanner : ISettingsImpactPlanner
    {
        public SettingsEffect[] Plan(
            SettingsBundle active,
            SettingsBundle candidate,
            IReadOnlyList<SettingsAggregateId> changedAggregates) => changedAggregates
            .Select(aggregate => new SettingsEffect(
                aggregate,
                aggregate == SettingsAggregateId.Voice
                    ? SettingsEffectKind.PendingIdle
                    : SettingsEffectKind.ApplyLive,
                "Synthetic process validation."))
            .ToArray();
    }

    private sealed class CapturingActivator : ISettingsActivator
    {
        public List<ActivationCandidate> Candidates { get; } = [];

        public Task<SettingsActivationResult> ActivateAsync(
            SettingsAggregateId aggregate,
            SettingsBundle activationCandidate,
            long desiredRevision,
            CancellationToken runtimeCancellationToken)
        {
            runtimeCancellationToken.ThrowIfCancellationRequested();
            Candidates.Add(new ActivationCandidate(
                aggregate,
                activationCandidate.Voice.PinnedTaskLabel,
                activationCandidate.TaskAlerts.Bank));
            return Task.FromResult(new SettingsActivationResult(SettingsActivationState.Applied));
        }
    }

    private sealed class AttachedClient(string connectionId, SettingsAttachState state) : IDisposable
    {
        public string ConnectionId { get; } = connectionId;
        public SettingsAttachState State { get; } = state;
        public void Dispose() => State.Subscription.Dispose();
    }

    private sealed record Prepared(string PreparationToken, long Revision);
    private sealed record Checkpoint(SettingsTransactionCheckpoint Value, SettingsAggregateId? Aggregate);
    private sealed record Recovery(SettingsOperationResult Operation, SettingsSnapshot Snapshot);
    private sealed record Replay(SettingsOperationResult BeforeReplay, ApplySettingsResult Replayed);
    private sealed record ActivationCandidate(SettingsAggregateId Aggregate, string VoiceLabel, int TaskAlertBank);
    private sealed record ActivationCapture(ApplySettingsResult Result, ActivationCandidate[] Candidates);
    private sealed record RuntimeApplied(RuntimeSnapshot Before, ApplySettingsResult Applied);
    private sealed record RuntimeCapture(RuntimeSnapshot Snapshot, RuntimeCaptureStartResult Capture);
    private sealed record RuntimeInspection(RuntimeAttachResult Attach, RuntimeCaptureLookupResult Capture);
    private sealed record RuntimeOperation(RuntimeAttachResult Attach, SettingsOperationResult Operation);
    private sealed record RuntimeReconnect(RuntimeAttachResult Attach, RuntimeEvent[] Events, PrepareSettingsResult Stale);
    private sealed record RuntimePrepared(RuntimeSnapshot Snapshot, Guid OperationId, string PreparationToken);

    private sealed class CapturingCallbacks : IRuntimeRpcClient
    {
        public List<RuntimeEvent> Events { get; } = [];
        public RuntimeEvent[] SnapshotEvents()
        {
            lock (Events) return Events.ToArray();
        }
        public Task RuntimeEventAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken)
        {
            lock (Events) Events.Add(runtimeEvent);
            return Task.CompletedTask;
        }
        public Task RuntimeInputEventAsync(RuntimeConnectionInputEvent inputEvent, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RuntimeCommandCompletedAsync(RuntimeCommandResult result, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task WaitThroughAsync(long sequence, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                lock (Events) if (Events.Any(item => item.Sequence >= sequence)) return;
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return;
                await Task.Delay(TimeSpan.FromMilliseconds(20)).WaitAsync(remaining);
            }
        }
    }
}
