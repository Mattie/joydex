using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.App;

internal sealed record PebbleIndexReceiverStatus(
    bool Running,
    string Message,
    PebbleIndexDelivery? Latest = null,
    int OutstandingCount = 0);

internal sealed class PebbleIndexReceiverRuntime : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumRequestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly PebbleIndexPreferences _preferences;
    private readonly byte[] _secret;
    private readonly PebbleIndexDeliveryStore _store;
    private readonly IDesktopTaskBridgeClient _bridge;
    private readonly Action<PebbleIndexReceiverStatus> _status;
    private readonly Action<string> _log;
    private readonly Func<Task>? _beforeClientRelease;
    private readonly Channel<PebbleIndexDelivery> _deliveries = Channel.CreateBounded<PebbleIndexDelivery>(
        new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connections = new(8, 8);
    private readonly ConcurrentDictionary<Task, byte> _clients = new();
    private readonly TcpListener _listener;
    private Task? _acceptLoop;
    private Task? _deliveryLoop;

    private PebbleIndexReceiverRuntime(
        PebbleIndexPreferences preferences, string secret, PebbleIndexDeliveryStore store,
        IDesktopTaskBridgeClient bridge, Action<PebbleIndexReceiverStatus> status, Action<string> log,
        Func<Task>? beforeClientRelease)
    {
        _preferences = preferences;
        _secret = Encoding.UTF8.GetBytes(secret);
        _store = store;
        _bridge = bridge;
        _status = IgnoreCallbackFailures(status);
        _log = IgnoreCallbackFailures(log);
        _beforeClientRelease = beforeClientRelease;
        _listener = new TcpListener(IPAddress.Loopback, preferences.Port);
    }

    public static Task<PebbleIndexReceiverRuntime> StartAsync(
        PebbleIndexPreferences preferences, string secretPath, string inboxDirectory,
        string desktopTaskBridgePipeName,
        Action<PebbleIndexReceiverStatus> status, Action<string> log,
        CancellationToken cancellationToken = default) =>
        StartAsync(preferences, PebbleIndexSecretStore.LoadOrCreate(secretPath), inboxDirectory,
            new DesktopTaskBridgeClient(desktopTaskBridgePipeName), status, log, cancellationToken);

    internal static Task<PebbleIndexReceiverRuntime> StartAsync(
        PebbleIndexPreferences preferences, string secret, string inboxDirectory,
        IDesktopTaskBridgeClient bridge, Action<PebbleIndexReceiverStatus> status, Action<string> log,
        CancellationToken cancellationToken = default,
        Func<Task>? beforeClientRelease = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = preferences.Normalize();
        var errors = normalized.Validate();
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        var runtime = new PebbleIndexReceiverRuntime(
            normalized, secret, new PebbleIndexDeliveryStore(inboxDirectory), bridge, status, log,
            beforeClientRelease);
        var message = $"Listening on http://127.0.0.1:{normalized.Port}/pebble-index";
        var startedStatus = runtime.BuildStatus(true, message);
        runtime._listener.Start(backlog: 16);
        runtime._acceptLoop = runtime.AcceptAsync(runtime._lifetime.Token);
        runtime._deliveryLoop = runtime.DeliverAsync(runtime._lifetime.Token);
        runtime._status(startedStatus);
        runtime._log("Pebble Index receiver started on loopback.");
        return Task.FromResult(runtime);
    }

    internal static PebbleIndexReceiverStatus ReadStoredStatus(
        bool running,
        string message,
        string inboxDirectory) =>
        BuildStatus(running, message, new PebbleIndexDeliveryStore(inboxDirectory));

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                if (!_connections.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                var handling = HandleOwnedClientAsync(client, cancellationToken);
                _clients.TryAdd(handling, 0);
                _ = handling.ContinueWith(
                    completed => _clients.TryRemove(completed, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandleOwnedClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await HandleClientAsync(client, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { _log("Pebble Index request timed out."); }
        catch (Exception exception) { _log("Pebble Index request failed: " + exception.Message); }
        finally
        {
            try
            {
                if (_beforeClientRelease is not null)
                    await _beforeClientRelease().ConfigureAwait(false);
            }
            finally { _connections.Release(); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request.Method == "GET" && request.Path == "/health")
                {
                    await WriteResponseAsync(stream, 200, new { status = "ready" }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (request.Method != "POST" || request.Path != "/pebble-index")
                {
                    await WriteResponseAsync(stream, 404, new { error = "not found" }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (!Authorized(request.Headers.GetValueOrDefault("Authorization")))
                {
                    await WriteResponseAsync(stream, 401, new { error = "unauthorized" }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (!TryReadBoundary(request.Headers.GetValueOrDefault("Content-Type"), out var boundary))
                {
                    await WriteResponseAsync(stream, 400, new { error = "multipart/form-data is required" }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                var form = ParseMultipart(request.Body, boundary, out var hasFiles);
                if (hasFiles || form.ContainsKey("audio"))
                {
                    await WriteResponseAsync(stream, 400, new { error = "audio and file uploads are disabled" }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (IsTruthy(form.GetValueOrDefault("test")) || IsTruthy(request.Headers.GetValueOrDefault("X-Index-Test")))
                {
                    await WriteResponseAsync(stream, 200, new { status = "test-received" }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                PebbleIndexAcceptResult accepted;
                try
                {
                    accepted = _store.Accept(
                        form.GetValueOrDefault("transcription") ?? string.Empty,
                        form.GetValueOrDefault("recordedAt") ?? string.Empty,
                        form.GetValueOrDefault("client") ?? string.Empty,
                        request.Headers.GetValueOrDefault("X-Index-Trigger") ?? string.Empty,
                        request.Headers.GetValueOrDefault("X-Index-Delivery-Id"), _preferences);
                }
                catch (InvalidDataException exception)
                {
                    await WriteResponseAsync(stream, 400, new { error = exception.Message }, cancellationToken).ConfigureAwait(false);
                    return;
                }
                _status(BuildStatus(true,
                    accepted.IsDuplicate ? "Duplicate received; no second delivery was attempted." : "Transcription received.",
                    accepted.Delivery));
                if (!accepted.IsDuplicate && !_deliveries.Writer.TryWrite(accepted.Delivery))
                {
                    var held = _store.Update(
                        accepted.Delivery.Id,
                        PebbleIndexDeliveryState.Received,
                        "Held before send: the live delivery queue was unavailable.");
                    _status(BuildStatus(true, held.Detail, held));
                    _log($"Pebble Index delivery {accepted.Delivery.Id} was stored but the live queue was unavailable.");
                }
                await WriteResponseAsync(stream, 202, new
                {
                    status = accepted.IsDuplicate ? accepted.Delivery.State.ToString().ToLowerInvariant() : "received",
                    id = accepted.Delivery.Id,
                    duplicate = accepted.IsDuplicate,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException)
            {
                _log("Pebble Index request was rejected: " + exception.Message);
            }
        }
    }

    private async Task DeliverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var delivery in _deliveries.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await DeliverOneAsync(delivery, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _log($"Pebble Index delivery {delivery.Id} could not finish processing: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task DeliverOneAsync(PebbleIndexDelivery delivery, CancellationToken cancellationToken)
    {
        DesktopTaskSummary target;
        try
        {
            var catalog = await _bridge.ListTasksAsync(
                delivery.TargetTaskId, cancellationToken: cancellationToken).ConfigureAwait(false);
            target = catalog.Tasks.FirstOrDefault(candidate =>
                candidate.Id.Equals(delivery.TargetTaskId, StringComparison.OrdinalIgnoreCase)
                && candidate.HostId.Equals(delivery.TargetHostId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The selected Desktop task is unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _store.Update(
                delivery.Id,
                PebbleIndexDeliveryState.Received,
                "Held before send: the receiver stopped.");
            throw;
        }
        catch (Exception exception)
        {
            var held = _store.Update(delivery.Id, PebbleIndexDeliveryState.Received,
                "Held before send: " + exception.Message);
            _status(BuildStatus(true, held.Detail, held));
            _log($"Pebble Index delivery {delivery.Id} was held before send: {exception.Message}");
            return;
        }
        try
        {
            var attempting = _store.Update(
                delivery.Id,
                PebbleIndexDeliveryState.DeliveryUncertain,
                "Desktop delivery started; confirmation is pending.");
            _status(BuildStatus(true, attempting.Detail, attempting));
        }
        catch (Exception exception)
        {
            _log($"Pebble Index delivery {delivery.Id} was held because its send-attempt state could not be stored: {exception.Message}");
            return;
        }
        try
        {
            var result = await _bridge.SendMessageAsync(
                delivery.TargetTaskId, target, delivery.Transcription, cancellationToken).ConfigureAwait(false);
            var sent = _store.Update(delivery.Id, PebbleIndexDeliveryState.Sent,
                result.Queued ? "Queued to the running task." : "Delivered.");
            _status(BuildStatus(true, sent.Detail, sent));
            _log($"Pebble Index delivery {delivery.Id} was confirmed by Desktop.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _store.Update(
                delivery.Id,
                PebbleIndexDeliveryState.DeliveryUncertain,
                "Desktop delivery may have started before the receiver stopped.");
            throw;
        }
        catch (Exception exception)
        {
            var uncertain = _store.Update(delivery.Id, PebbleIndexDeliveryState.DeliveryUncertain,
                "Desktop delivery was not confirmed: " + exception.Message);
            _status(BuildStatus(true, uncertain.Detail, uncertain));
            _log($"Pebble Index delivery {delivery.Id} is uncertain: {exception.Message}");
        }
    }

    private bool Authorized(string? supplied)
    {
        var value = supplied?.Trim() ?? string.Empty;
        while (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            value = value[7..].TrimStart();
        var actual = Encoding.UTF8.GetBytes(value);
        return actual.Length == _secret.Length && CryptographicOperations.FixedTimeEquals(actual, _secret);
    }

    private PebbleIndexReceiverStatus BuildStatus(
        bool running,
        string message,
        PebbleIndexDelivery? latest = null) =>
        BuildStatus(running, message, _store, latest);

    private static PebbleIndexReceiverStatus BuildStatus(
        bool running,
        string message,
        PebbleIndexDeliveryStore store,
        PebbleIndexDelivery? latest = null)
    {
        var recovery = store.GetRecoverySummary();
        var displayMessage = recovery.OutstandingCount switch
        {
            0 => message,
            1 => message + " 1 stored delivery needs manual review.",
            _ => message + $" {recovery.OutstandingCount} stored deliveries need manual review.",
        };
        return new PebbleIndexReceiverStatus(
            running,
            displayMessage,
            recovery.LatestOutstanding ?? latest ?? store.Recent(1).FirstOrDefault(),
            recovery.OutstandingCount);
    }

    private static async Task<HttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>(1024);
        var state = 0;
        while (header.Count < MaximumHeaderBytes)
        {
            var one = new byte[1];
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
                throw new InvalidDataException("The HTTP request ended before its headers.");
            header.Add(one[0]);
            state = (state, one[0]) switch
            {
                (0, 13) => 1, (1, 10) => 2, (2, 13) => 3, (3, 10) => 4, (_, 13) => 1, _ => 0,
            };
            if (state == 4) break;
        }
        if (state != 4) throw new InvalidDataException("The HTTP headers were too large.");
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', 3);
        if (requestLine.Length != 3) throw new InvalidDataException("The HTTP request line was invalid.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) throw new InvalidDataException("An HTTP header was invalid.");
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        var length = 0;
        if (headers.TryGetValue("Content-Length", out var value)
            && (!int.TryParse(value, out length) || length < 0 || length > MaximumRequestBytes))
            throw new InvalidDataException("The HTTP request body was too large.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return new HttpRequest(requestLine[0].ToUpperInvariant(), requestLine[1], headers, body);
    }

    private static bool TryReadBoundary(string? contentType, out string boundary)
    {
        boundary = string.Empty;
        if (contentType is null || !contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var part in contentType.Split(';').Skip(1))
        {
            var pair = part.Trim().Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals("boundary", StringComparison.OrdinalIgnoreCase))
            {
                boundary = pair[1].Trim().Trim('"');
                return boundary.Length is > 0 and <= 200;
            }
        }
        return false;
    }

    private static Dictionary<string, string> ParseMultipart(byte[] body, string boundary, out bool hasFiles)
    {
        hasFiles = false;
        var form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = Encoding.Latin1.GetString(body);
        foreach (var part in text.Split("--" + boundary, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (separator < 0) continue;
            var headers = part[..separator];
            if (headers.Contains("filename=", StringComparison.OrdinalIgnoreCase)
                || headers.Contains("filename*=", StringComparison.OrdinalIgnoreCase))
            {
                hasFiles = true;
                continue;
            }
            const string nameMarker = "name=";
            var nameStart = headers.IndexOf(nameMarker, StringComparison.OrdinalIgnoreCase);
            if (nameStart < 0) continue;
            nameStart += nameMarker.Length;
            var quoted = nameStart < headers.Length && headers[nameStart] == '"';
            if (quoted) nameStart++;
            var nameEnd = quoted
                ? headers.IndexOf('"', nameStart)
                : headers.IndexOfAny([';', '\r', '\n'], nameStart);
            if (nameEnd < 0) nameEnd = headers.Length;
            var raw = part[(separator + 4)..];
            if (raw.EndsWith("\r\n", StringComparison.Ordinal)) raw = raw[..^2];
            form[headers[nameStart..nameEnd].Trim()] = StrictUtf8.GetString(Encoding.Latin1.GetBytes(raw));
        }
        return form;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, object value, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var reason = statusCode switch { 200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", _ => "Not Found" };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTruthy(string? value) => value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true || value == "1";

    private static Action<T> IgnoreCallbackFailures<T>(Action<T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return value =>
        {
            try { callback(value); }
            catch { }
        };
    }

    internal int ActiveClientCount => _clients.Count;

    public async ValueTask DisposeAsync()
    {
        _deliveries.Writer.TryComplete();
        _lifetime.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null) await _acceptLoop.ConfigureAwait(false);
        var clients = _clients.Keys.ToArray();
        if (clients.Length > 0) await Task.WhenAll(clients).ConfigureAwait(false);
        if (_deliveryLoop is not null) await _deliveryLoop.ConfigureAwait(false);
        while (_deliveries.Reader.TryRead(out var pending))
        {
            try
            {
                _store.Update(
                    pending.Id,
                    PebbleIndexDeliveryState.Received,
                    "Held before send: the receiver stopped.");
            }
            catch (Exception exception)
            {
                _log($"Could not mark Pebble Index delivery {pending.Id} as held: {exception.Message}");
            }
        }
        _connections.Dispose();
        _lifetime.Dispose();
        _status(BuildStatus(false, "Receiver stopped."));
    }

    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);
}
