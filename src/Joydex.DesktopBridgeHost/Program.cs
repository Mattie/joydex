using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

Console.InputEncoding = new UTF8Encoding(false, true);
Console.OutputEncoding = new UTF8Encoding(false, true);
return await DesktopBridgeProgram.RunAsync(args).ConfigureAwait(false);

internal static class DesktopBridgeProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 3 && string.Equals(args[0], "--install-desktop-bridge", StringComparison.Ordinal))
        {
            var manager = new DesktopBridgeConfigurationManager(args[1]);
            manager.InstallOrRepair(args[2]);
            Console.WriteLine(manager.Inspect(args[2]).Message);
            return 0;
        }

        if (args.Length == 2
            && string.Equals(args[0], "--probe-desktop-app-server", StringComparison.Ordinal)
            && int.TryParse(args[1], out var appServerProcessId))
        {
            await using var probe = new PackagedCodexAppToolsClient(Console.Error.WriteLine);
            if (!probe.TryPrepareEnvironmentFromAppServer(appServerProcessId, out var probeError))
            {
                Console.Error.WriteLine(probeError);
                return 2;
            }
            await probe.EnsureRequiredToolsAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine("Codex Desktop task messaging is available.");
            return 0;
        }

        if (args.Length == 3
            && string.Equals(args[0], "--serve-desktop", StringComparison.Ordinal)
            && int.TryParse(args[1], out var ownerProcessId))
        {
            return await RunDesktopBrokerWorkerAsync(ownerProcessId, args[2]).ConfigureAwait(false);
        }

        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            lifetime.Cancel();
        };

        if (args.Length == 4 && string.Equals(args[0], "--voice-tools", StringComparison.Ordinal))
        {
            return await RunVoiceToolsMcpServerAsync(args[1], args[2], args[3], lifetime.Token).ConfigureAwait(false);
        }

        using var transportLease = DesktopTaskBridgeTransportLease.TryAcquire();
        await using var desktopTools = new PackagedCodexAppToolsClient(Console.Error.WriteLine);
        await using var bridge = new DesktopTaskBridgePipeServer(desktopTools, Console.Error.WriteLine);
        if (transportLease is null)
        {
            Console.Error.WriteLine("Joydex Desktop Task Bridge transport is already owned by another process.");
        }
        else if (desktopTools.TryPrepareEnvironment(out var bridgeUnavailableReason))
        {
            bridge.Start();
        }
        else
        {
            Console.Error.WriteLine($"Joydex Desktop Task Bridge is inactive: {bridgeUnavailableReason}");
        }

        try
        {
            await RunMcpServerAsync(lifetime.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Joydex Desktop Task Bridge stopped: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunDesktopBrokerWorkerAsync(int ownerProcessId, string pipeName)
    {
        Process owner;
        try
        {
            owner = Process.GetProcessById(ownerProcessId);
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine($"Joydex owner process {ownerProcessId} is not running.");
            return 2;
        }

        using (owner)
        using (var transportLease = DesktopTaskBridgeTransportLease.TryAcquire())
        {
            if (transportLease is null)
            {
                Console.Error.WriteLine("Joydex Desktop Task Bridge transport is already owned by another process.");
                return 3;
            }

            Console.WriteLine("Joydex Desktop Task Bridge broker worker started.");
            Console.Out.Flush();
            while (!owner.HasExited)
            {
                if (!DesktopAppToolsEnvironmentResolver.TryFindDesktopAppServerProcessId(
                        out var desktopAppServerProcessId,
                        out var discoveryError))
                {
                    Console.Error.WriteLine(discoveryError);
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    using var desktopAppServer = Process.GetProcessById(desktopAppServerProcessId);
                    await using var desktopTools = new PackagedCodexAppToolsClient(Console.Error.WriteLine);
                    if (!desktopTools.TryPrepareEnvironmentFromAppServer(
                            desktopAppServerProcessId,
                            out var bridgeUnavailableReason))
                    {
                        throw new InvalidOperationException(bridgeUnavailableReason);
                    }

                    using (var preflight = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        await desktopTools.EnsureRequiredToolsAsync(preflight.Token).ConfigureAwait(false);
                    }

                    await using var bridge = new DesktopTaskBridgePipeServer(
                        desktopTools,
                        Console.Error.WriteLine,
                        pipeName);
                    bridge.Start();
                    Console.Error.WriteLine(
                        $"Joydex Desktop Task Bridge is connected to Desktop App Server {desktopAppServerProcessId}.");

                    await Task.WhenAny(
                            owner.WaitForExitAsync(),
                            desktopAppServer.WaitForExitAsync())
                        .ConfigureAwait(false);
                    if (!owner.HasExited)
                    {
                        Console.Error.WriteLine("Codex Desktop restarted; reconnecting the task bridge.");
                    }
                }
                catch (Exception exception) when (exception is ArgumentException
                    or IOException
                    or InvalidDataException
                    or InvalidOperationException
                    or OperationCanceledException
                    or TimeoutException)
                {
                    Console.Error.WriteLine($"Joydex Desktop Task Bridge connection failed: {exception.Message}");
                }

                if (!owner.HasExited)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
        }

        return 0;
    }

    private static async Task<int> RunVoiceToolsMcpServerAsync(
        string preferencesPath,
        string sourceThreadId,
        string pipeName,
        CancellationToken cancellationToken)
    {
        if (!CodexTaskReference.TryParse(sourceThreadId, out sourceThreadId))
        {
            Console.Error.WriteLine("Joydex voice tools require a valid source task ID.");
            return 2;
        }

        var bridge = new DesktopTaskBridgeClient(pipeName);
        var recentDeliveries = new VoiceTaskDeliveryDeduplicator();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return 0;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id))
                {
                    continue;
                }
                var method = root.TryGetProperty("method", out var methodElement)
                    ? methodElement.GetString()
                    : null;
                object response = method switch
                {
                    "initialize" => new
                    {
                        jsonrpc = "2.0",
                        id,
                        result = new
                        {
                            protocolVersion = ReadProtocolVersion(root),
                            capabilities = new { tools = new { listChanged = false } },
                            serverInfo = new
                            {
                                name = "joydex-voice-task-tools",
                                title = "Joydex Voice Task Tools",
                                version = "1.0.0",
                            },
                            instructions = "Send clean prompts to the selected Codex Desktop task. Never retry held messages automatically.",
                        },
                    },
                    "ping" => new { jsonrpc = "2.0", id, result = new { } },
                    "tools/list" => BuildVoiceToolList(id),
                    "tools/call" => await BuildVoiceToolResponseAsync(
                            root,
                            id,
                            preferencesPath,
                            sourceThreadId,
                            bridge,
                            recentDeliveries,
                            cancellationToken)
                        .ConfigureAwait(false),
                    _ => new
                    {
                        jsonrpc = "2.0",
                        id,
                        error = new { code = -32601, message = $"Unsupported method: {method}" },
                    },
                };
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
                await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return 0;
    }

    private static object BuildVoiceToolList(JsonElement id) => new
    {
        jsonrpc = "2.0",
        id,
        result = new
        {
            tools = new[]
            {
                new
                {
                    name = "send_message_to_codex_task",
                    title = "Send Message to Codex Task",
                    description = "Sends a clean follow-up prompt to the saved Voice Target, or to one explicitly named local Codex task. Call it for every send request; prior delivery failures do not predict current availability.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            target = new
                            {
                                type = "string",
                                description = "Optional task title. Omit it to use the saved Voice Target.",
                            },
                            message = new
                            {
                                type = "string",
                                minLength = 1,
                                maxLength = DesktopTaskBridgeClient.MaximumMessageLength,
                                description = "The exact clean prompt to deliver.",
                            },
                        },
                        required = new[] { "message" },
                        additionalProperties = false,
                    },
                },
            },
        },
    };

    private static async Task<object> BuildVoiceToolResponseAsync(
        JsonElement request,
        JsonElement id,
        string preferencesPath,
        string sourceThreadId,
        DesktopTaskBridgeClient bridge,
        VoiceTaskDeliveryDeduplicator recentDeliveries,
        CancellationToken cancellationToken)
    {
        var toolName = request.TryGetProperty("params", out var parameters)
            && parameters.TryGetProperty("name", out var name)
            ? name.GetString()
            : null;
        if (!string.Equals(toolName, "send_message_to_codex_task", StringComparison.Ordinal))
        {
            return VoiceToolError(id, $"Unknown tool: {toolName}");
        }
        if (!parameters.TryGetProperty("arguments", out var arguments)
            || !arguments.TryGetProperty("message", out var messageElement)
            || messageElement.GetString() is not { } message
            || string.IsNullOrWhiteSpace(message)
            || message.Length > DesktopTaskBridgeClient.MaximumMessageLength)
        {
            return VoiceToolError(id, "A non-empty message of 32 KiB or less is required.");
        }
        var spokenTarget = arguments.TryGetProperty("target", out var targetElement)
            ? targetElement.GetString()
            : null;

        VoicePePreferences preferences;
        try
        {
            preferences = VoicePePreferencesStore.LoadOrCreate(preferencesPath);
        }
        catch (Exception exception)
        {
            return VoiceToolResult(id, true, $"held for review: Room Voice settings are unavailable ({exception.Message}).");
        }
        if (!preferences.DesktopTaskMessagingEnabled)
        {
            return VoiceToolResult(id, true, "held for review: Desktop task messaging is disabled in Room Voice settings.");
        }

        DesktopTaskSummary? selected = null;
        try
        {
            var catalog = await bridge.ListTasksAsync(
                    sourceThreadId,
                    preferences.DedicatedTaskId,
                    cancellationToken)
                .ConfigureAwait(false);
            var resolution = VoiceTaskTargetResolution.Resolve(
                catalog.Tasks,
                spokenTarget,
                preferences.VoiceTargetTaskId,
                preferences.VoiceTargetHostId);
            if (resolution.Kind == VoiceTaskTargetResolutionKind.Ambiguous)
            {
                var candidates = string.Join(", ", resolution.Candidates.Take(8).Select(task => task.Title));
                return VoiceToolResult(id, false, $"No message was sent because that task name is ambiguous. Candidates: {candidates}.");
            }
            if (resolution.Kind == VoiceTaskTargetResolutionKind.Missing || resolution.Target is null)
            {
                return VoiceToolResult(id, false, string.IsNullOrWhiteSpace(spokenTarget)
                    ? "No message was sent because the saved Voice Target is unavailable. Choose another target in Room Voice."
                    : $"No message was sent because no accessible local task matched '{spokenTarget.Trim()}'.");
            }
            selected = resolution.Target;
            var sourceSession = ReadActiveSession(preferences.AgentWorkspacePath, sourceThreadId);
            if (recentDeliveries.TryGet(
                    sourceSession,
                    selected,
                    message,
                    DateTimeOffset.UtcNow,
                    out var earlierResult))
            {
                return VoiceToolResult(id, false, earlierResult);
            }
            var delivered = await bridge.SendMessageAsync(sourceThreadId, selected, message, cancellationToken)
                .ConfigureAwait(false);
            var resultText = delivered.Queued ? "queued to the running task" : "delivered";
            recentDeliveries.Record(
                sourceSession,
                selected,
                message,
                resultText,
                DateTimeOffset.UtcNow);
            return VoiceToolResult(
                id,
                false,
                resultText);
        }
        catch (Exception exception) when (exception is IOException
            or TimeoutException
            or InvalidDataException
            or InvalidOperationException)
        {
            selected ??= BuildSavedTarget(preferences, spokenTarget);
            if (selected is null || string.IsNullOrWhiteSpace(preferences.AgentWorkspacePath))
            {
                return VoiceToolResult(id, true, "held for review: the Desktop bridge is unavailable and no exact task could be resolved.");
            }

            try
            {
                var outbox = new VoiceTaskOutbox(preferences.AgentWorkspacePath);
                var draft = outbox.Hold(
                    selected,
                    message,
                    ReadActiveSession(preferences.AgentWorkspacePath, sourceThreadId),
                    exception.Message);
                return VoiceToolResult(id, false, $"held for review (draft {draft.Id})");
            }
            catch (Exception outboxException)
            {
                return VoiceToolResult(id, true, $"Delivery failed and the review draft could not be saved: {outboxException.Message}");
            }
        }
    }

    private static DesktopTaskSummary? BuildSavedTarget(VoicePePreferences preferences, string? spokenTarget)
    {
        if (!string.IsNullOrWhiteSpace(spokenTarget)
            || !CodexTaskReference.TryParse(preferences.VoiceTargetTaskId, out var taskId)
            || string.IsNullOrWhiteSpace(preferences.VoiceTargetHostId))
        {
            return null;
        }
        return new DesktopTaskSummary(
            taskId,
            preferences.VoiceTargetHostId,
            string.IsNullOrWhiteSpace(preferences.VoiceTargetTaskLabel) ? taskId : preferences.VoiceTargetTaskLabel,
            string.Empty,
            null,
            null,
            0,
            false);
    }

    private static string ReadActiveSession(string workspacePath, string fallback)
    {
        try
        {
            var path = Path.Combine(workspacePath, ".joydex", "active-voice-session.txt");
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } value && value.Length <= 256)
            {
                return value;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return fallback;
    }

    private static object VoiceToolError(JsonElement id, string message) => new
    {
        jsonrpc = "2.0",
        id,
        error = new { code = -32602, message },
    };

    private static object VoiceToolResult(JsonElement id, bool isError, string text) => new
    {
        jsonrpc = "2.0",
        id,
        result = new
        {
            content = new[] { new { type = "text", text } },
            isError,
        },
    };

    private static async Task RunMcpServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id))
                {
                    continue;
                }

                var method = root.TryGetProperty("method", out var methodElement)
                    ? methodElement.GetString()
                    : null;
                object response = method switch
                {
                    "initialize" => new
                    {
                        jsonrpc = "2.0",
                        id,
                        result = new
                        {
                            protocolVersion = ReadProtocolVersion(root),
                            capabilities = new { tools = new { listChanged = false } },
                            serverInfo = new
                            {
                                name = "joydex-desktop-task-bridge",
                                title = "Joydex Desktop Task Bridge",
                                version = "1.0.0",
                            },
                        },
                    },
                    "ping" => new { jsonrpc = "2.0", id, result = new { } },
                    "tools/list" => BuildDesktopBridgeToolList(id),
                    "tools/call" => new
                    {
                        jsonrpc = "2.0",
                        id,
                        error = new { code = -32601, message = "The Desktop Task Bridge exposes no model-callable tools." },
                    },
                    _ => new
                    {
                        jsonrpc = "2.0",
                        id,
                        error = new { code = -32601, message = $"Unsupported method: {method}" },
                    },
                };

                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions))
                    .ConfigureAwait(false);
                await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static object BuildDesktopBridgeToolList(JsonElement id) => new
    {
        jsonrpc = "2.0",
        id,
        result = new { tools = Array.Empty<object>() },
    };

    private static string ReadProtocolVersion(JsonElement request)
    {
        if (request.TryGetProperty("params", out var parameters)
            && parameters.TryGetProperty("protocolVersion", out var version)
            && version.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        return "2025-06-18";
    }

}

internal sealed class VoiceTaskDeliveryDeduplicator(TimeSpan? window = null)
{
    private const int MaximumEntries = 64;
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(15);
    private readonly Dictionary<DeliveryKey, DeliveryRecord> _deliveries = [];

    public bool TryGet(
        string sourceSession,
        DesktopTaskSummary target,
        string message,
        DateTimeOffset now,
        out string result)
    {
        Prune(now);
        var key = BuildKey(sourceSession, target, message);
        if (_deliveries.TryGetValue(key, out var delivery))
        {
            result = delivery.Result;
            return true;
        }

        result = string.Empty;
        return false;
    }

    public void Record(
        string sourceSession,
        DesktopTaskSummary target,
        string message,
        string result,
        DateTimeOffset now)
    {
        Prune(now);
        if (_deliveries.Count >= MaximumEntries)
        {
            var oldest = _deliveries.MinBy(pair => pair.Value.At).Key;
            _deliveries.Remove(oldest);
        }
        _deliveries[BuildKey(sourceSession, target, message)] = new DeliveryRecord(now, result);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _deliveries
                     .Where(pair => now - pair.Value.At > _window)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _deliveries.Remove(key);
        }
    }

    private static DeliveryKey BuildKey(
        string sourceSession,
        DesktopTaskSummary target,
        string message) =>
        new(sourceSession, target.Id, target.HostId, message);

    private readonly record struct DeliveryKey(
        string SourceSession,
        string TargetTaskId,
        string TargetHostId,
        string Message);

    private readonly record struct DeliveryRecord(DateTimeOffset At, string Result);
}

internal sealed class DesktopTaskBridgeTransportLease : IDisposable
{
    private const string SemaphoreName = @"Local\Joydex.DesktopTasks.Transport.v1";
    private readonly Semaphore _semaphore;
    private int _disposed;

    private DesktopTaskBridgeTransportLease(Semaphore semaphore) => _semaphore = semaphore;

    public static DesktopTaskBridgeTransportLease? TryAcquire()
        => TryAcquire(SemaphoreName);

    internal static DesktopTaskBridgeTransportLease? TryAcquire(string semaphoreName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semaphoreName);
        var semaphore = new Semaphore(initialCount: 1, maximumCount: 1, semaphoreName);
        if (!semaphore.WaitOne(0))
        {
            semaphore.Dispose();
            return null;
        }
        return new DesktopTaskBridgeTransportLease(semaphore);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _semaphore.Release();
        _semaphore.Dispose();
    }
}

internal sealed class DesktopTaskBridgePipeServer(
    PackagedCodexAppToolsClient nativeClient,
    Action<string>? log = null,
    string pipeName = DesktopTaskBridgeProtocol.PipeName) : IAsyncDisposable
{
    private readonly string _pipeName = string.IsNullOrWhiteSpace(pipeName)
        ? throw new ArgumentException("A Desktop task bridge pipe name is required.", nameof(pipeName))
        : pipeName.Trim();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private Task? _acceptTask;

    public void Start() => _acceptTask ??= AcceptLoopAsync(_lifetime.Token);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: 4096,
                    outBufferSize: 4096);
                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    var connection = HandleConnectionAsync(server, cancellationToken);
                    _connections.TryAdd(connection, 0);
                    _ = connection.ContinueWith(
                        completed => _connections.TryRemove(completed, out _),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    server = null!;
                }
                finally
                {
                    server?.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            log?.Invoke($"Joydex Desktop Task Bridge receiver stopped: {exception.Message}");
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        await using (server.ConfigureAwait(false))
        {
            DesktopTaskBridgeResponse response;
            var responseId = string.Empty;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var payload = await DesktopTaskBridgeFraming.ReadAsync(
                        server,
                        DesktopTaskBridgeProtocol.MaximumFrameBytes,
                        deadline.Token)
                    .ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<DesktopTaskBridgeRequest>(
                    payload,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidDataException("Desktop task bridge request was empty.");
                if (!string.IsNullOrWhiteSpace(request.Id) && request.Id.Length <= 128)
                {
                    responseId = request.Id;
                }
                response = await HandleRequestAsync(request, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                or JsonException
                or InvalidDataException
                or InvalidOperationException
                or TimeoutException
                or OperationCanceledException)
            {
                response = new DesktopTaskBridgeResponse(
                    DesktopTaskBridgeProtocol.Version,
                    responseId,
                    Success: false,
                    JsonSerializer.SerializeToElement(new { }),
                    exception.Message);
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                response,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await DesktopTaskBridgeFraming.WriteAsync(
                    server,
                    bytes,
                    DesktopTaskBridgeProtocol.MaximumFrameBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<DesktopTaskBridgeResponse> HandleRequestAsync(
        DesktopTaskBridgeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Version != DesktopTaskBridgeProtocol.Version)
        {
            throw new InvalidDataException($"Unsupported Desktop task bridge version {request.Version}.");
        }
        if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128)
        {
            throw new InvalidDataException("Desktop task bridge request ID was invalid.");
        }
        if (request.Method != DesktopTaskBridgeProtocol.StatusMethod
            && !Guid.TryParse(request.SourceThreadId, out _))
        {
            throw new InvalidDataException("Desktop task bridge source task ID was invalid.");
        }

        JsonElement result;
        if (request.Method == DesktopTaskBridgeProtocol.StatusMethod)
        {
            await nativeClient.EnsureRequiredToolsAsync(cancellationToken).ConfigureAwait(false);
            result = JsonSerializer.SerializeToElement(new { available = true });
        }
        else
        {
            var (tool, arguments) = MapTool(request);
            var content = await nativeClient.CallToolAsync(
                    tool,
                    request.SourceThreadId,
                    request.Id,
                    arguments,
                    cancellationToken)
                .ConfigureAwait(false);
            result = JsonSerializer.SerializeToElement(new { content });
        }

        return new DesktopTaskBridgeResponse(
            DesktopTaskBridgeProtocol.Version,
            request.Id,
            Success: true,
            result);
    }

    private static (string Tool, JsonElement Arguments) MapTool(DesktopTaskBridgeRequest request)
    {
        var tool = request.Method switch
        {
            DesktopTaskBridgeProtocol.ListTasksMethod => "list_threads",
            DesktopTaskBridgeProtocol.ReadTaskMethod => "read_thread",
            DesktopTaskBridgeProtocol.SendMessageMethod => "send_message_to_thread",
            _ => throw new InvalidDataException($"Unsupported Desktop task bridge method: {request.Method}"),
        };
        return (tool, request.Arguments);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_acceptTask is not null)
        {
            await _acceptTask.ConfigureAwait(false);
        }
        await Task.WhenAll(_connections.Keys).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}

internal sealed class PackagedCodexAppToolsClient(Action<string>? log = null) : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HashSet<string>? _tools;
    private Process? _process;
    private Task? _stderrTask;
    private int _requestId;
    private bool _environmentPrepared;

    public bool TryPrepareEnvironment(out string error)
    {
        if (_environmentPrepared)
        {
            error = string.Empty;
            return true;
        }
        _environmentPrepared = DesktopAppToolsEnvironmentResolver.TryPrepareForCurrentProcess(log, out error);
        return _environmentPrepared;
    }

    public bool TryPrepareEnvironmentFromAppServer(int processId, out string error)
    {
        _environmentPrepared = DesktopAppToolsEnvironmentResolver.TryPrepareFromAppServerProcess(
            processId,
            log,
            out error);
        return _environmentPrepared;
    }

    public async Task EnsureRequiredToolsAsync(CancellationToken cancellationToken)
    {
        var tools = await GetToolsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var required in new[] { "list_threads", "read_thread", "send_message_to_thread" })
        {
            if (!tools.Contains(required))
            {
                throw new InvalidOperationException($"Codex Desktop does not expose {required}.");
            }
        }
    }

    public async Task<string> CallToolAsync(
        string tool,
        string sourceThreadId,
        string callId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var tools = await GetToolsAsync(cancellationToken).ConfigureAwait(false);
        if (!tools.Contains(tool))
        {
            throw new InvalidOperationException($"Codex Desktop does not expose {tool}.");
        }

        var result = await RequestAsync(
                "tools/call",
                new
                {
                    name = tool,
                    arguments,
                    _meta = new Dictionary<string, object>
                    {
                        ["openai/threadId"] = sourceThreadId,
                        ["openai/toolCallId"] = $"joydex-{callId}",
                        ["openai/turnId"] = $"joydex-{callId}",
                    },
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            throw new InvalidOperationException(ReadContent(result, "Codex Desktop rejected the task operation."));
        }
        return ReadContent(result, string.Empty);
    }

    private async Task<HashSet<string>> GetToolsAsync(CancellationToken cancellationToken)
    {
        if (_tools is not null)
        {
            return _tools;
        }

        var result = await RequestAsync(
                "tools/list",
                new { threadStartKind = "all" },
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.TryGetProperty("tools", out var toolsElement)
            || toolsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Codex Desktop returned no task tool catalog.");
        }

        _tools = toolsElement.EnumerateArray()
            .Where(tool => tool.TryGetProperty("name", out _))
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        return _tools;
    }

    private async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var process = _process ?? throw new InvalidOperationException("Codex App Tools adapter is not running.");
            var requestId = Interlocked.Increment(ref _requestId);
            var request = JsonSerializer.Serialize(
                new
                {
                    jsonrpc = "2.0",
                    id = requestId,
                    method,
                    @params = parameters,
                },
                JsonOptions);
            log?.Invoke($"Codex App Tools adapter request started: {method}.");
            await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Codex App Tools adapter closed its output.");
            log?.Invoke($"Codex App Tools adapter request completed: {method}.");
            using var response = JsonDocument.Parse(line);
            var root = response.RootElement;
            if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var responseId) || responseId != requestId)
            {
                throw new InvalidDataException("Codex App Tools adapter returned a mismatched response.");
            }
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                throw new InvalidOperationException(message ?? "Codex Desktop task broker returned an error.");
            }
            if (!root.TryGetProperty("result", out var result))
            {
                throw new InvalidDataException("Codex Desktop task broker returned no result.");
            }
            return result.Clone();
        }
        catch
        {
            ResetAdapter();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ResetAdapter()
    {
        _tools = null;
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        process.Dispose();
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        if (!TryPrepareEnvironment(out var brokerUnavailableReason))
        {
            throw new InvalidOperationException(brokerUnavailableReason);
        }
        var root = FindAdapterRoot();
        var serverPath = Path.Combine(root, "server.mjs");
        var nodePath = Environment.GetEnvironmentVariable("CODEX_MCP_NODE_PATH")?.Trim();
        ProcessStartInfo startInfo;
        if (!string.IsNullOrWhiteSpace(nodePath) && File.Exists(nodePath))
        {
            startInfo = CreateAdapterStartInfo(nodePath, root);
            startInfo.ArgumentList.Add(serverPath);
        }
        else
        {
            var launcher = Path.Combine(root, "scripts", "launch_codex_app_tools_mcp.cmd");
            if (!File.Exists(launcher))
            {
                throw new FileNotFoundException("The packaged Codex App Tools launcher is missing.", launcher);
            }
            startInfo = CreateAdapterStartInfo("cmd.exe", root);
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("call");
            startInfo.ArgumentList.Add(launcher);
            startInfo.ArgumentList.Add(serverPath);
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The packaged Codex App Tools adapter did not start.");
        log?.Invoke("Packaged Codex App Tools adapter started.");
        _stderrTask = DrainStderrAsync(_process, cancellationToken);
        var initialized = await ExchangeStartupAsync(
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "joydex-desktop-task-bridge", version = "1.0.0" },
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!initialized.TryGetProperty("protocolVersion", out _))
        {
            throw new InvalidDataException("The packaged Codex App Tools adapter did not initialize.");
        }
        log?.Invoke("Packaged Codex App Tools adapter initialized.");
        await _process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}".AsMemory(),
                cancellationToken)
            .ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> ExchangeStartupAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Codex App Tools adapter is not running.");
        var requestId = Interlocked.Increment(ref _requestId);
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = requestId,
            method,
            @params = parameters,
        }, JsonOptions);
        await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Codex App Tools adapter closed during initialization.");
        using var response = JsonDocument.Parse(line);
        if (response.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(error.GetProperty("message").GetString());
        }
        return response.RootElement.GetProperty("result").Clone();
    }

    private static ProcessStartInfo CreateAdapterStartInfo(string executablePath, string workingDirectory) => new()
    {
        FileName = executablePath,
        WorkingDirectory = workingDirectory,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardInputEncoding = Utf8,
        StandardOutputEncoding = Utf8,
        StandardErrorEncoding = Utf8,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static string FindAdapterRoot()
    {
        var configured = Environment.GetEnvironmentVariable("JOYDEX_CODEX_APP_TOOLS_ROOT")?.Trim();
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "server.mjs")))
        {
            return Path.GetFullPath(configured);
        }
        var resources = Environment.GetEnvironmentVariable("CODEX_ELECTRON_RESOURCES_PATH")?.Trim();
        if (!string.IsNullOrWhiteSpace(resources))
        {
            var candidate = Path.Combine(
                resources,
                "plugins",
                "openai-bundled",
                "plugins",
                "codex-app-tools");
            if (File.Exists(Path.Combine(candidate, "server.mjs")))
            {
                return Path.GetFullPath(candidate);
            }
        }
        throw new InvalidOperationException(
            "The packaged Codex App Tools adapter is unavailable. Repair the Desktop Task Bridge after updating Codex Desktop.");
    }

    private static async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string ReadContent(JsonElement result, string fallback)
    {
        if (!result.TryGetProperty("content", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        var text = new StringBuilder();
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type)
                && type.GetString() == "text"
                && item.TryGetProperty("text", out var content))
            {
                text.Append(content.GetString());
            }
        }
        return text.Length > 0 ? text.ToString() : fallback;
    }

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Close();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
        }
        if (_stderrTask is not null)
        {
            try
            {
                await _stderrTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        _gate.Dispose();
    }
}
