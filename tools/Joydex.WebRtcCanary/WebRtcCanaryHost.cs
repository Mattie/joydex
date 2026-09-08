using System.Net;
using System.Text;

namespace Joydex.WebRtcCanary;

internal sealed class WebRtcCanaryHost : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CanaryState _state;
    private readonly CodexRealtimeCanary _canary;
    private readonly string? _fixtureWavPath;
    private readonly string? _captureWebmPath;
    private readonly CancellationTokenSource _lifetime = new();

    public WebRtcCanaryHost(
        int port,
        CanaryState state,
        CodexRealtimeCanary canary,
        string? fixtureWavPath,
        string? captureWebmPath)
    {
        Url = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Url);
        _state = state;
        _canary = canary;
        _fixtureWavPath = string.IsNullOrWhiteSpace(fixtureWavPath)
            ? null
            : Path.GetFullPath(fixtureWavPath);
        _captureWebmPath = string.IsNullOrWhiteSpace(captureWebmPath)
            ? null
            : Path.GetFullPath(captureWebmPath);
        _state.ConfigureMediaFixture(
            _fixtureWavPath is not null && File.Exists(_fixtureWavPath),
            _captureWebmPath is not null);
    }

    public string Url { get; }

    public void Start() => _listener.Start();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var registration = linked.Token.Register(() =>
        {
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        });

        while (!linked.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (linked.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (linked.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context, linked.Token), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            path = path.Length == 0 ? "/" : path;

            if (context.Request.HttpMethod == "GET" && path == "/")
            {
                await RespondAsync(context, CanaryPage.Html, "text/html; charset=utf-8", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/health")
            {
                await RespondAsync(context, "ok", "text/plain; charset=utf-8", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/state")
            {
                await RespondAsync(context, _state.ToJson(), "application/json", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/fixture.wav")
            {
                if (_fixtureWavPath is null || !File.Exists(_fixtureWavPath))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                    return;
                }

                await RespondFileAsync(context, _fixtureWavPath, "audio/wav", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/session")
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var offer = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var answer = await _canary.StartAsync(offer, cancellationToken).ConfigureAwait(false);
                await RespondAsync(context, answer, "application/sdp", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/media-connected")
            {
                _canary.MediaConnected();
                await RespondAsync(context, "ok", "text/plain; charset=utf-8", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/fixture-played")
            {
                _state.FixturePlayed();
                await RespondAsync(context, "ok", "text/plain; charset=utf-8", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/remote-track")
            {
                _state.RemoteTrackReceived();
                await RespondAsync(context, "ok", "text/plain; charset=utf-8", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/remote-audio")
            {
                if (_captureWebmPath is null)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                    return;
                }

                await SaveCaptureAsync(context, _captureWebmPath, cancellationToken).ConfigureAwait(false);
                _state.CaptureWritten();
                await RespondAsync(context, "ok", "text/plain; charset=utf-8", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/prompt")
            {
                await _canary.SendAudioCanaryAsync(cancellationToken).ConfigureAwait(false);
                await RespondAsync(context, "ok", "text/plain; charset=utf-8", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/audio-received")
            {
                _canary.AudioReceived();
                await RespondAsync(context, "ok", "text/plain; charset=utf-8", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/stop")
            {
                await _canary.StopAsync(cancellationToken).ConfigureAwait(false);
                await RespondAsync(context, "ok", "text/plain; charset=utf-8", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
        }
        catch (Exception exception)
        {
            _state.Failed(exception.Message);
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await RespondAsync(context, exception.Message, "text/plain; charset=utf-8", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task RespondAsync(
        HttpListenerContext context,
        string body,
        string contentType,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task RespondFileAsync(
        HttpListenerContext context,
        string path,
        string contentType,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = file.Length;
        context.Response.Headers[HttpResponseHeader.CacheControl] = "no-store";
        await using var stream = file.OpenRead();
        await stream.CopyToAsync(context.Response.OutputStream, cancellationToken).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task SaveCaptureAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        const long maximumCaptureBytes = 32L * 1024 * 1024;
        if (context.Request.ContentLength64 > maximumCaptureBytes)
        {
            throw new InvalidDataException("Remote-audio capture exceeds the 32 MiB canary limit.");
        }

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var count = await context.Request.InputStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            total += count;
            if (total > maximumCaptureBytes)
            {
                throw new InvalidDataException("Remote-audio capture exceeds the 32 MiB canary limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        if (total == 0)
        {
            throw new InvalidDataException("Browser returned an empty remote-audio capture.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        _listener.Close();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }
}
