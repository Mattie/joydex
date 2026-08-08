using System.Net;
using System.Net.Http.Headers;

namespace Joydex.Windows.WirelessPanel;

/// <summary>
/// Defines the small panel I/O surface consumed by Joydex's higher-level task/action controller.
/// </summary>
public interface IEspHomePanelTransport : IAsyncDisposable
{
    /// <inheritdoc cref="EspHomePanelTransport.RunAsync"/>
    Task RunAsync(
        Func<EspHomePanelButton, CancellationToken, ValueTask> onPressed,
        Func<CancellationToken, ValueTask>? onConnected = null,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="EspHomePanelTransport.SetTaskStatesAsync"/>
    Task SetTaskStatesAsync(
        EspHomeTaskState task1,
        EspHomeTaskState task2,
        EspHomeTaskState task3,
        EspHomeTaskState task4,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="EspHomePanelTransport.SetTaskStateUpdatesAsync"/>
    Task SetTaskStateUpdatesAsync(
        IReadOnlyList<EspHomeTaskStateUpdate> updates,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="EspHomePanelTransport.SetTaskWorkspaceLabelsAsync"/>
    Task SetTaskWorkspaceLabelsAsync(
        string task1,
        string task2,
        string task3,
        string task4,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="EspHomePanelTransport.SetTaskWorkspaceLabelUpdatesAsync"/>
    Task SetTaskWorkspaceLabelUpdatesAsync(
        IReadOnlyList<EspHomeTaskWorkspaceLabelUpdate> updates,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Maintains the outbound ESPHome Web Server connection used for panel touch events and state.
/// The transport owns no Joydex task identity or action policy.
/// </summary>
public sealed class EspHomePanelTransport : IEspHomePanelTransport
{
    private const int WorkspaceLabelSupportUnknown = 0;
    private const int WorkspaceLabelSupportAvailable = 1;
    private const int WorkspaceLabelSupportUnavailable = 2;

    private static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultPostTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultSseIdleTimeout = TimeSpan.FromSeconds(45);

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly bool _ownsHttpClient;
    private readonly Action<string>? _log;
    private readonly TimeSpan _reconnectDelay;
    private readonly TimeSpan _postTimeout;
    private readonly TimeSpan _sseIdleTimeout;
    private readonly SemaphoreSlim _postGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _lifecycleGate = new();
    private Task? _runTask;
    private int _workspaceLabelSupport;
    private bool _disposed;

    /// <summary>
    /// Creates a transport whose HTTP client responds to ESPHome Digest authentication challenges.
    /// </summary>
    public EspHomePanelTransport(
        Uri baseUri,
        string username,
        string password,
        Action<string>? log = null)
        : this(
            CreateDigestClient(username, password),
            baseUri,
            ownsHttpClient: true,
            log,
            DefaultReconnectDelay,
            DefaultPostTimeout,
            DefaultSseIdleTimeout)
    {
    }

    internal EspHomePanelTransport(
        HttpClient httpClient,
        Uri baseUri,
        Action<string>? log = null,
        TimeSpan? reconnectDelay = null,
        TimeSpan? postTimeout = null,
        TimeSpan? sseIdleTimeout = null)
        : this(
            httpClient,
            baseUri,
            ownsHttpClient: false,
            log,
            reconnectDelay ?? DefaultReconnectDelay,
            postTimeout ?? DefaultPostTimeout,
            sseIdleTimeout ?? DefaultSseIdleTimeout)
    {
    }

    private EspHomePanelTransport(
        HttpClient httpClient,
        Uri baseUri,
        bool ownsHttpClient,
        Action<string>? log,
        TimeSpan reconnectDelay,
        TimeSpan postTimeout,
        TimeSpan sseIdleTimeout)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUri = NormalizeBaseUri(baseUri);
        _ownsHttpClient = ownsHttpClient;
        _log = log;
        _reconnectDelay = reconnectDelay >= TimeSpan.Zero
            ? reconnectDelay
            : throw new ArgumentOutOfRangeException(nameof(reconnectDelay));
        _postTimeout = postTimeout > TimeSpan.Zero
            ? postTimeout
            : throw new ArgumentOutOfRangeException(nameof(postTimeout));
        _sseIdleTimeout = sseIdleTimeout > TimeSpan.Zero
            ? sseIdleTimeout
            : throw new ArgumentOutOfRangeException(nameof(sseIdleTimeout));
    }

    /// <summary>
    /// Connects to <c>/events</c>, reports live allowlisted press edges, and reconnects after
    /// network failure. Cancellation ends the loop normally.
    /// </summary>
    /// <param name="onPressed">
    /// Called once for each live OFF-to-ON transition. A callback that wants to stop the owner
    /// should cancel, return, and let the owner dispose after <see cref="RunAsync"/> completes.
    /// </param>
    /// <param name="onConnected">
    /// Called after each successful SSE connection, before the initial state catch-up is read.
    /// This is the appropriate place to push a complete current task-state replacement. As with
    /// <paramref name="onPressed"/>, cancel and return before the owner disposes the transport.
    /// </param>
    /// <param name="cancellationToken">Stops the stream and any reconnect delay.</param>
    public Task RunAsync(
        Func<EspHomePanelButton, CancellationToken, ValueTask> onPressed,
        Func<CancellationToken, ValueTask>? onConnected = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onPressed);
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("The ESPHome panel event loop is already running.");
            }

            _runTask = RunCoreAsync(onPressed, onConnected, cancellationToken);
            return _runTask;
        }
    }

    /// <summary>
    /// Replaces all four task-card states as one serialized group of ESPHome select requests. HTTP
    /// has no transaction, so the caller should retry its latest complete snapshot after a failure.
    /// </summary>
    public Task SetTaskStatesAsync(
        EspHomeTaskState task1,
        EspHomeTaskState task2,
        EspHomeTaskState task3,
        EspHomeTaskState task4,
        CancellationToken cancellationToken = default) =>
        PostEntityUpdatesAsync(
            [
                EntityUpdate.Select("Task 1 State", ToOption(task1)),
                EntityUpdate.Select("Task 2 State", ToOption(task2)),
                EntityUpdate.Select("Task 3 State", ToOption(task3)),
                EntityUpdate.Select("Task 4 State", ToOption(task4)),
            ],
            cancellationToken);

    /// <summary>
    /// Updates only the task-card selects whose projected state changed. Keeping ordinary
    /// publications narrow avoids unnecessary full-card redraws on the single-framebuffer panel.
    /// </summary>
    public Task SetTaskStateUpdatesAsync(
        IReadOnlyList<EspHomeTaskStateUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var selects = new EntityUpdate[updates.Count];
        for (var index = 0; index < updates.Count; index++)
        {
            var update = updates[index];
            var entityName = update.Slot switch
            {
                1 => "Task 1 State",
                2 => "Task 2 State",
                3 => "Task 3 State",
                4 => "Task 4 State",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(updates),
                    update.Slot,
                    "ESPHome task slots must be between 1 and 4."),
            };
            selects[index] = EntityUpdate.Select(entityName, ToOption(update.State));
        }

        return PostEntityUpdatesAsync(selects, cancellationToken);
    }

    /// <summary>
    /// Replaces all four task-card workspace footers as one serialized group of ESPHome text
    /// requests. Values are display labels and must not contain full workspace paths.
    /// </summary>
    public Task SetTaskWorkspaceLabelsAsync(
        string task1,
        string task2,
        string task3,
        string task4,
        CancellationToken cancellationToken = default) =>
        PostEntityUpdatesAsync(
            [
                EntityUpdate.Text("Task 1 Workspace", ValidateWorkspaceLabel(task1)),
                EntityUpdate.Text("Task 2 Workspace", ValidateWorkspaceLabel(task2)),
                EntityUpdate.Text("Task 3 Workspace", ValidateWorkspaceLabel(task3)),
                EntityUpdate.Text("Task 4 Workspace", ValidateWorkspaceLabel(task4)),
            ],
            cancellationToken);

    /// <summary>Updates only the task-card workspace footers whose labels changed.</summary>
    public Task SetTaskWorkspaceLabelUpdatesAsync(
        IReadOnlyList<EspHomeTaskWorkspaceLabelUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var texts = new EntityUpdate[updates.Count];
        for (var index = 0; index < updates.Count; index++)
        {
            var update = updates[index];
            var entityName = update.Slot switch
            {
                1 => "Task 1 Workspace",
                2 => "Task 2 Workspace",
                3 => "Task 3 Workspace",
                4 => "Task 4 Workspace",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(updates),
                    update.Slot,
                    "ESPHome task slots must be between 1 and 4."),
            };
            texts[index] = EntityUpdate.Text(
                entityName,
                ValidateWorkspaceLabel(update.Label));
        }

        return PostEntityUpdatesAsync(texts, cancellationToken);
    }

    /// <summary>Cancels the active stream and waits for in-flight transport work to stop.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? runTask;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _disposeCancellation.Cancel();
            runTask = _runTask;
        }

        try
        {
            if (runTask is not null)
            {
                try
                {
                    await runTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
                {
                }
            }
        }
        finally
        {
            await _postGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_ownsHttpClient)
                {
                    _httpClient.Dispose();
                }
            }
            finally
            {
                _postGate.Release();
            }

            _postGate.Dispose();
            _disposeCancellation.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private async Task RunCoreAsync(
        Func<EspHomePanelButton, CancellationToken, ValueTask> onPressed,
        Func<CancellationToken, ValueTask>? onConnected,
        CancellationToken callerCancellation)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation,
            _disposeCancellation.Token);
        var cancellationToken = linkedCancellation.Token;
        var consecutiveFailures = 0;
        var failureReported = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReadOneConnectionAsync(
                        onPressed,
                        onConnected,
                        MarkConnectionHealthy,
                        cancellationToken)
                    .ConfigureAwait(false);
                ReportFailure("ESPHome panel event stream ended; reconnecting.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException exception)
            {
                ReportFailure($"ESPHome panel connection failed: {exception.Message}");
            }
            catch (IOException exception)
            {
                ReportFailure($"ESPHome panel event stream failed: {exception.Message}");
            }
            catch (TimeoutException exception)
            {
                ReportFailure($"ESPHome panel request timed out: {exception.Message}");
            }

            var multiplier = 1 << Math.Min(consecutiveFailures - 1, 4);
            var reconnectDelay = TimeSpan.FromMilliseconds(
                Math.Min(
                    TimeSpan.FromSeconds(30).TotalMilliseconds,
                    _reconnectDelay.TotalMilliseconds * multiplier));
            try
            {
                await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        void MarkConnectionHealthy()
        {
            consecutiveFailures = 0;
            if (failureReported)
            {
                _log?.Invoke("ESPHome panel event stream resumed.");
                failureReported = false;
            }
        }

        void ReportFailure(string message)
        {
            consecutiveFailures++;
            if (!failureReported)
            {
                _log?.Invoke(message);
                failureReported = true;
            }
        }
    }

    private async Task ReadOneConnectionAsync(
        Func<EspHomePanelButton, CancellationToken, ValueTask> onPressed,
        Func<CancellationToken, ValueTask>? onConnected,
        Action onHealthy,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("events"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException(
                $"ESPHome /events returned unexpected content type '{mediaType ?? "<missing>"}'.");
        }

        Volatile.Write(ref _workspaceLabelSupport, WorkspaceLabelSupportUnknown);
        if (onConnected is not null)
        {
            await onConnected(cancellationToken).ConfigureAwait(false);
        }

        var tracker = new EspHomePressTracker();
        var healthy = false;
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await EspHomeSseParser.ReadAsync(
                stream,
                async (sseEvent, token) =>
                {
                    if (!healthy)
                    {
                        healthy = true;
                        onHealthy();
                    }

                    if (!string.Equals(sseEvent.EventType, "state", StringComparison.Ordinal) ||
                        !EspHomeStateEventParser.TryParse(sseEvent.Data, out var stateEvent) ||
                        !tracker.TryObserve(stateEvent, out var pressed))
                    {
                        return;
                    }

                    await onPressed(pressed, token).ConfigureAwait(false);
                },
                _sseIdleTimeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PostEntityUpdatesAsync(
        IReadOnlyList<EntityUpdate> updates,
        CancellationToken callerCancellation)
    {
        if (updates.Count > 0
            && updates[0].Domain == "text"
            && Volatile.Read(ref _workspaceLabelSupport) == WorkspaceLabelSupportUnavailable)
        {
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation,
            _disposeCancellation.Token);
        var cancellationToken = linkedCancellation.Token;
        await _postGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var update in updates)
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(_postTimeout);
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    BuildUri(
                        $"{update.Domain}/{Uri.EscapeDataString(update.EntityName)}/set" +
                        $"?{update.Parameter}={Uri.EscapeDataString(update.Value)}"));
                HttpResponseMessage response;
                try
                {
                    response = await _httpClient
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                    when (!cancellationToken.IsCancellationRequested && requestTimeout.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Setting ESPHome {update.Domain} '{update.EntityName}' exceeded {_postTimeout.TotalSeconds:g} seconds.",
                        exception);
                }

                using (response)
                {
                    if (update.Domain == "text" && response.StatusCode == HttpStatusCode.NotFound)
                    {
                        if (Interlocked.Exchange(
                                ref _workspaceLabelSupport,
                                WorkspaceLabelSupportUnavailable)
                            != WorkspaceLabelSupportUnavailable)
                        {
                            _log?.Invoke(
                                "ESPHome panel firmware does not expose workspace labels; task states remain active.");
                        }

                        return;
                    }

                    response.EnsureSuccessStatusCode();
                    if (update.Domain == "text")
                    {
                        Volatile.Write(
                            ref _workspaceLabelSupport,
                            WorkspaceLabelSupportAvailable);
                    }
                }
            }
        }
        finally
        {
            _postGate.Release();
        }
    }

    private Uri BuildUri(string relativePath) => new(_baseUri, relativePath);

    private static HttpClient CreateDigestClient(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true,
            UseProxy = false,
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static Uri NormalizeBaseUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(baseUri.Host))
        {
            throw new ArgumentException("The ESPHome panel endpoint must be an absolute HTTP or HTTPS URI.", nameof(baseUri));
        }

        if (!string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment) ||
            !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new ArgumentException(
                "The ESPHome panel endpoint cannot contain credentials, a query, or a fragment.",
                nameof(baseUri));
        }

        var builder = new UriBuilder(baseUri);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static string ToOption(EspHomeTaskState state) => state switch
    {
        EspHomeTaskState.Empty => "EMPTY",
        EspHomeTaskState.Running => "RUNNING",
        EspHomeTaskState.Attention => "ATTENTION",
        EspHomeTaskState.Complete => "COMPLETE",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static string ValidateWorkspaceLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (label.Length > EspHomePanelAdapter.MaximumWorkspaceLabelLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(label),
                label.Length,
                $"ESPHome workspace labels cannot exceed {EspHomePanelAdapter.MaximumWorkspaceLabelLength} characters.");
        }

        if (label.Any(character =>
                character is < ' ' or > '~' or '\\' or '/' or ':'))
        {
            throw new ArgumentException(
                "ESPHome workspace labels must contain display-safe ASCII without path separators.",
                nameof(label));
        }

        return label;
    }

    private sealed record EntityUpdate(
        string Domain,
        string EntityName,
        string Parameter,
        string Value)
    {
        public static EntityUpdate Select(string entityName, string option) =>
            new("select", entityName, "option", option);

        public static EntityUpdate Text(string entityName, string value) =>
            new("text", entityName, "value", value);
    }
}

/// <summary>One changed ESPHome task-card projection.</summary>
public readonly record struct EspHomeTaskStateUpdate(int Slot, EspHomeTaskState State);

/// <summary>One changed ESPHome task-card workspace footer.</summary>
public readonly record struct EspHomeTaskWorkspaceLabelUpdate(int Slot, string Label);
