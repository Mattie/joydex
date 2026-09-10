using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

internal interface ICodexAppServerClient : IAsyncDisposable
{
    event Action<string, JsonElement>? NotificationReceived;

    int? ProcessId { get; }

    Task Completion { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class CodexAppServerRpcException(int code, string message) : InvalidOperationException(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Owns one Codex App Server child process and its newline-delimited JSON-RPC connection.
/// </summary>
public sealed class CodexAppServerClient : ICodexAppServerClient
{
    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly CodexAppServerBinary _binary;
    private readonly string? _workingDirectory;
    private readonly CodexVoiceToolConfiguration? _voiceTools;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private int _nextRequestId;
    private int _started;
    private int _disposeRequested;
    private int _stderrLineCount;

    internal CodexAppServerClient(CodexAppServerBinary binary, Action<string>? log = null)
        : this(binary, null, log, null)
    {
    }

    internal CodexAppServerClient(
        CodexAppServerBinary binary,
        string? workingDirectory,
        Action<string>? log = null,
        CodexVoiceToolConfiguration? voiceTools = null)
    {
        _binary = binary ?? throw new ArgumentNullException(nameof(binary));
        _workingDirectory = NormalizeWorkingDirectory(workingDirectory);
        _log = log;
        _voiceTools = voiceTools?.Normalize();
    }

    public event Action<string, JsonElement>? NotificationReceived;

    public int? ProcessId => _process?.Id;

    public Task Completion => _completion.Task;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("The Codex App Server client has already been started.");
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
        var startInfo = CreateStartInfo(_binary.ExecutablePath, _workingDirectory, _voiceTools);

        // This route intentionally exercises the user's cached ChatGPT login. An inherited API
        // key must not silently select the separately billed API-key Realtime path.
        startInfo.Environment.Remove("OPENAI_API_KEY");

        try
        {
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Codex App Server did not start.");
            _stdoutTask = ReadStdoutAsync(_lifetime.Token);
            _stderrTask = ReadStderrAsync(_lifetime.Token);

            await RequestAsync(
                "initialize",
                new
                {
                    capabilities = new
                    {
                        experimentalApi = true,
                        requestAttestation = false,
                    },
                    clientInfo = new
                    {
                        name = "joydex",
                        title = "Joydex",
                        version = typeof(CodexAppServerClient).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    },
                },
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(false);

            await NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string? workingDirectory = null,
        CodexVoiceToolConfiguration? voiceTools = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = NormalizeWorkingDirectory(workingDirectory) ?? string.Empty,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("features.realtime_conversation=true");
        if (voiceTools is not null)
        {
            var normalized = voiceTools.Normalize();
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(
                $"mcp_servers.joydex_voice.command={JsonSerializer.Serialize(normalized.HostExecutablePath)}");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(
                "mcp_servers.joydex_voice.args=["
                + string.Join(',', new[]
                {
                    "--voice-tools",
                    normalized.PreferencesPath,
                    normalized.SourceThreadId,
                }.Select(value => JsonSerializer.Serialize(value)))
                + "]");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("mcp_servers.joydex_voice.startup_timeout_sec=10");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("mcp_servers.joydex_voice.tool_timeout_sec=30");
        }
        startInfo.ArgumentList.Add("app-server");
        return startInfo;
    }

    private static string? NormalizeWorkingDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(value.Trim());
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"The Codex App Server working directory does not exist: {fullPath}");
        }

        return fullPath;
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
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
        if (process.HasExited)
        {
            throw new InvalidOperationException("Codex App Server exited before the request could be written.");
        }

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
        catch (Exception exception)
        {
            CompleteConnection(exception);
            return;
        }

        if (Volatile.Read(ref _disposeRequested) != 0)
        {
            _completion.TrySetResult();
            return;
        }

        CompleteConnection(new InvalidOperationException(
            $"Codex App Server output closed unexpectedly.{FormatWithheldStderr()}"));
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
            _log?.Invoke("Codex App Server emitted a non-JSON stdout line; content was withheld.");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idElement))
            {
                if (root.TryGetProperty("method", out var requestMethodElement)
                    && requestMethodElement.GetString() is { Length: > 0 } requestMethod)
                {
                    _ = RespondToServerRequestAsync(idElement.Clone(), requestMethod);
                    return;
                }

                if (!idElement.TryGetInt32(out var id) || !_pending.TryGetValue(id, out var pending))
                {
                    return;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value)
                        ? value
                        : -1;
                    var message = error.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : null;
                    pending.TrySetException(new CodexAppServerRpcException(
                        code,
                        message ?? "Unknown Codex App Server error."));
                    return;
                }

                pending.TrySetResult(root.TryGetProperty("result", out var result)
                    ? result.Clone()
                    : default);
                return;
            }

            if (root.TryGetProperty("method", out var methodElement)
                && methodElement.GetString() is { Length: > 0 } method)
            {
                var parameters = root.TryGetProperty("params", out var paramsElement)
                    ? paramsElement.Clone()
                    : default;
                DispatchNotification(method, parameters);
            }
        }
    }

    private void DispatchNotification(string method, JsonElement parameters)
    {
        var handlers = NotificationReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<string, JsonElement> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(method, parameters);
            }
            catch (Exception exception)
            {
                _log?.Invoke(
                    $"A Codex App Server notification consumer failed for {method}; error={exception.GetType().Name}.");
            }
        }
    }

    private async Task RespondToServerRequestAsync(JsonElement id, string method)
    {
        try
        {
            await WriteAsync(
                new
                {
                    id,
                    error = new
                    {
                        code = -32_601,
                        message = $"Unsupported Codex App Server request: {method}",
                    },
                },
                _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log?.Invoke(
                $"Could not reject unsupported Codex App Server request {method}; error={exception.GetType().Name}.");
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

                Interlocked.Increment(ref _stderrLineCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CompleteConnection(Exception exception)
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(exception);
        }

        _completion.TrySetException(exception);
    }

    private string FormatWithheldStderr()
    {
        var lineCount = Volatile.Read(ref _stderrLineCount);
        return lineCount == 0
            ? string.Empty
            : $" {lineCount} stderr line(s) were withheld from diagnostics.";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            return;
        }

        var disposed = new ObjectDisposedException(nameof(CodexAppServerClient));
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(disposed);
        }

        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Close();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        _lifetime.Cancel();
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

        _completion.TrySetResult();
        process?.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed record CodexVoiceToolConfiguration(
    string HostExecutablePath,
    string PreferencesPath,
    string SourceThreadId)
{
    public CodexVoiceToolConfiguration Normalize()
    {
        if (!CodexTaskReference.TryParse(SourceThreadId, out var sourceThreadId))
        {
            throw new ArgumentException("A valid source task ID is required.", nameof(SourceThreadId));
        }
        var host = Path.GetFullPath(HostExecutablePath.Trim());
        var preferences = Path.GetFullPath(PreferencesPath.Trim());
        if (!File.Exists(host))
        {
            throw new FileNotFoundException("The Joydex voice tool host is missing.", host);
        }
        return this with
        {
            HostExecutablePath = host,
            PreferencesPath = preferences,
            SourceThreadId = sourceThreadId,
        };
    }
}
