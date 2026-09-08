using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Joydex.WebRtcCanary;

internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly string _codexPath;
    private readonly bool _requestAttestation;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<string> _stderrTail = new();
    private readonly object _stderrGate = new();
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private int _nextRequestId;

    public CodexAppServerClient(string codexPath, bool requestAttestation)
    {
        _codexPath = codexPath;
        _requestAttestation = requestAttestation;
    }

    public event Action<string, JsonElement>? NotificationReceived;
    public event Action? AttestationRequested;

    public int? ProcessId => _process?.Id;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _codexPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("features.realtime_conversation=true");
        startInfo.ArgumentList.Add("app-server");

        // This canary deliberately exercises the user's cached ChatGPT login. A shell-level
        // API key must not silently change which auth path is under test.
        startInfo.Environment.Remove("OPENAI_API_KEY");

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Codex App Server did not start.");
        _stdoutTask = ReadStdoutAsync(_lifetime.Token);
        _stderrTask = ReadStderrAsync(_lifetime.Token);

        try
        {
            await RequestAsync(
                "initialize",
                new
                {
                    capabilities = new
                    {
                        experimentalApi = true,
                        requestAttestation = _requestAttestation,
                    },
                    clientInfo = new
                    {
                        name = "joydex-webrtc-canary",
                        title = "Joydex WebRTC Canary",
                        version = "0.0.0",
                    },
                },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);

            await NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not initialize Codex App Server: {exception.Message}{FormatStderrTail()}",
                exception);
        }
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException($"Duplicate App Server request id {id}.");
        }

        try
        {
            await WriteAsync(new { id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteAsync(new { method, @params = parameters }, cancellationToken);

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Codex App Server is not running.");
        var json = JsonSerializer.Serialize(message, JsonOptions);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadStdoutAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Codex App Server is not running.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                HandleLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            var error = new InvalidOperationException(
                $"Codex App Server output closed unexpectedly.{FormatStderrTail()}");
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(error);
            }
        }
    }

    private void HandleLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            RememberStderr($"Non-JSON stdout: {line}");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idElement))
            {
                if (root.TryGetProperty("method", out var requestMethodElement) &&
                    requestMethodElement.GetString() is { Length: > 0 } requestMethod)
                {
                    _ = RespondToServerRequestAsync(idElement.Clone(), requestMethod);
                    return;
                }

                if (!idElement.TryGetInt32(out var id))
                {
                    return;
                }

                if (!_pending.TryGetValue(id, out var completion))
                {
                    return;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : error.GetRawText();
                    completion.TrySetException(new InvalidOperationException(message ?? "Unknown App Server error."));
                    return;
                }

                completion.TrySetResult(root.TryGetProperty("result", out var result)
                    ? result.Clone()
                    : default);
                return;
            }

            if (root.TryGetProperty("method", out var methodElement))
            {
                var method = methodElement.GetString();
                if (!string.IsNullOrWhiteSpace(method))
                {
                    var parameters = root.TryGetProperty("params", out var paramsElement)
                        ? paramsElement.Clone()
                        : default;
                    NotificationReceived?.Invoke(method, parameters);
                }
            }
        }
    }

    private async Task RespondToServerRequestAsync(JsonElement id, string method)
    {
        try
        {
            if (string.Equals(method, "attestation/generate", StringComparison.Ordinal))
            {
                AttestationRequested?.Invoke();
                await WriteAsync(
                    new
                    {
                        id,
                        error = new
                        {
                            code = -32_601,
                            message = "The Joydex canary can observe attestation requests but cannot mint a first-party token.",
                        },
                    },
                    _lifetime.Token).ConfigureAwait(false);
                return;
            }

            await WriteAsync(
                new
                {
                    id,
                    error = new
                    {
                        code = -32_601,
                        message = $"Unsupported App Server request: {method}",
                    },
                },
                _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RememberStderr($"Could not answer App Server request {method}: {exception.Message}");
        }
    }

    private async Task ReadStderrAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Codex App Server is not running.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                RememberStderr(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RememberStderr(string line)
    {
        lock (_stderrGate)
        {
            _stderrTail.Enqueue(line);
            while (_stderrTail.Count > 12)
            {
                _stderrTail.Dequeue();
            }
        }
    }

    private string FormatStderrTail()
    {
        lock (_stderrGate)
        {
            return _stderrTail.Count == 0
                ? string.Empty
                : $" App Server detail: {string.Join(" | ", _stderrTail)}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();

        if (_process is { HasExited: false } process)
        {
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (_stdoutTask is not null)
        {
            try
            {
                await _stdoutTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
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

        _process?.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
