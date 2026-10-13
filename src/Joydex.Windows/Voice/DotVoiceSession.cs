using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

/// <summary>Owns native dot signaling while the existing media session owns WebRTC.</summary>
public sealed class DotVoiceSession : IAsyncDisposable
{
    public const string ValidatedCodexSha256 = "D13914CED6C7AF174D8D638DB938284231227312104B06A25E9608C632A7DAEC";
    public const string ValidatedCodeModeHostSha256 = "BABAF50818BCE44CEBAFFF9BCE1F517E441BCCBD6468EEF9B3029B51470209E4";
    private readonly Process _process;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stopGate = new();
    private Task? _stop;
    private int _identifier;
    private int _disposed;
    private bool _createIssued;
    private int _ending;
    private int _attachStarted;
    private readonly DotCallAllocationGuard _allocation = new(DotCallAllocationGuard.DefaultPath);

    private DotVoiceSession(Process process, Action<string>? log)
    {
        _process = process;
        _log = log;
    }

    public Task Ready => _ready.Task;
    public Task Completion => _completion.Task;

    public static async Task<DotVoiceTarget> DiscoverAsync(
        VoicePePreferences preferences, CancellationToken cancellationToken = default)
    {
        await using var session = await LaunchAsync(preferences, cancellationToken).ConfigureAwait(false);
        var result = await session.CommandAsync("discover", new { }, cancellationToken).ConfigureAwait(false);
        var target = result.Deserialize<DotVoiceTarget>(JsonOptions)
            ?? throw new InvalidDataException("The dot lookup returned no target.");
        if (!target.IsValid) throw new InvalidDataException("The dot lookup returned an invalid target.");
        return target;
    }

    public static Task<DotVoiceSession> CreateAsync(
        VoicePePreferences preferences, CancellationToken cancellationToken = default) =>
        CreateAsync(preferences, cancellationToken, log: null);

    public static async Task<DotVoiceSession> CreateAsync(
        VoicePePreferences preferences, CancellationToken cancellationToken, Action<string>? log)
    {
        DotCallAllocationGuard.RequireClear(DotCallAllocationGuard.DefaultPath);
        var target = preferences.DotTarget;
        if (target?.IsValid != true) throw new InvalidDataException("Select the dot again before calling.");
        var session = await LaunchAsync(preferences, cancellationToken, log).ConfigureAwait(false);
        try
        {
            await session.CommandAsync("verify", new
            {
                target.AccountId, target.UserId, target.DotId, target.ThreadId,
            }, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string> StartAsync(string offer, CancellationToken cancellationToken)
    {
        var result = await CommandAsync("create", new { sdp = offer }, cancellationToken).ConfigureAwait(false);
        var answer = result.GetProperty("sdp").GetString();
        if (answer is null || !answer.StartsWith("v=0", StringComparison.Ordinal))
            throw new InvalidDataException("The native dot call returned an invalid SDP answer.");
        return answer;
    }

    public void MarkMediaConnected() => MarkAnswerApplied();

    /// <summary>Begin attachment after applying SDP; microphone input still waits for connection and Ready.</summary>
    public void MarkAnswerApplied()
    {
        if (Interlocked.Exchange(ref _attachStarted, 1) == 0) _ = AttachAsync();
    }

    private async Task AttachAsync()
    {
        try
        {
            await CommandAsync("attach", new { }, CancellationToken.None).ConfigureAwait(false);
            _ready.TrySetResult();
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            _completion.TrySetException(exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stopGate)
        {
            Volatile.Write(ref _ending, 1);
            return (_stop ??= StopCoreAsync()).WaitAsync(cancellationToken);
        }
    }

    private async Task StopCoreAsync()
    {
        try
        {
            await _commands.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_createIssued) { _completion.TrySetResult(); return; }
            }
            finally { _commands.Release(); }
            var result = await CommandAsync("stop", new { }, CancellationToken.None).ConfigureAwait(false);
            if (!result.GetProperty("stopped").GetBoolean())
                throw new InvalidOperationException("The native dot stop was not acknowledged.");
            _allocation.ConfirmStopped();
            _completion.TrySetResult();
        }
        catch (Exception exception)
        {
            throw new VoiceOwnershipCleanupException("The native dot call stop could not be confirmed.", [exception]);
        }
    }

    private async Task<JsonElement> CommandAsync(string operation, object arguments, CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var elapsed = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            if (operation is "create" or "attach" && Volatile.Read(ref _ending) != 0)
                throw new OperationCanceledException("The native dot call is stopping.");
            var identifier = ++_identifier;
            var command = JsonSerializer.SerializeToElement(arguments, JsonOptions);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", identifier);
                writer.WriteString("op", operation);
                foreach (var property in command.EnumerateObject()) property.WriteTo(writer);
                writer.WriteEndObject();
            }
            if (operation == "create")
            {
                _allocation.Acquire();
                _createIssued = true;
            }
            await _process.StandardInput.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            // Drain the response even after cancellation: create might have allocated a call.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            var line = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null || line.Length > 256 * 1024)
                throw new InvalidOperationException("The dot signaling worker ended without a bounded response.");
            using var document = JsonDocument.Parse(line);
            var response = document.RootElement;
            if (response.TryGetProperty("failure", out var failure))
            {
                var code = failure.GetString();
                if (code is null || code.Length > 80 || !code.All(char.IsAsciiLetter)) code = "ProtocolFailure";
                throw new InvalidOperationException($"Native dot signaling failed: {code}.");
            }
            if (response.GetProperty("id").GetInt32() != identifier)
                throw new InvalidOperationException("The dot signaling response was out of sequence.");
            var result = response.GetProperty("result").Clone();
            try
            {
                _log?.Invoke($"Native dot signaling completed; phase={operation}; elapsedMs={elapsed.ElapsedMilliseconds}.");
                if (response.TryGetProperty("httpTiming", out var timing) && timing.ValueKind == JsonValueKind.Object
                    && timing.TryGetProperty("connectMs", out var connect) && connect.TryGetInt32(out var connectMs)
                    && timing.TryGetProperty("headersMs", out var headers) && headers.TryGetInt32(out var headersMs)
                    && timing.TryGetProperty("bodyMs", out var body) && body.TryGetInt32(out var bodyMs)
                    && connectMs is >= 0 and <= 70_000 && headersMs is >= 0 and <= 70_000 && bodyMs is >= 0 and <= 70_000)
                    _log?.Invoke($"Native dot HTTP completed; phase={operation}; connectMs={connectMs}; headersMs={headersMs}; bodyMs={bodyMs}.");
            }
            catch { /* Diagnostics do not control call lifetime. */ }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally { _commands.Release(); }
    }

    private static async Task<DotVoiceSession> LaunchAsync(VoicePePreferences preferences, CancellationToken cancellationToken, Action<string>? log = null)
    {
        var runtime = await CodexAppServerRuntimeResolver.ResolveAsync(
            preferences.CodexAppServerPath, cancellationToken).ConfigureAwait(false);
        await RequireHashAsync(runtime.ExecutablePath, ValidatedCodexSha256, cancellationToken).ConfigureAwait(false);
        await RequireHashAsync(runtime.CodeModeHostPath, ValidatedCodeModeHostSha256, cancellationToken).ConfigureAwait(false);
        var python = ResolvePython(preferences.DotPythonExecutable);
        var script = Path.Combine(AppContext.BaseDirectory, "Assets", "Voice", "dot-signaling.py");
        if (!File.Exists(script)) throw new FileNotFoundException("The native dot signaling asset is missing.", script);
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(runtime.ExecutablePath);
        start.Environment.Remove("OPENAI_API_KEY");
        var process = Process.Start(start) ?? throw new InvalidOperationException("The dot signaling worker could not start.");
        process.ErrorDataReceived += (_, _) => { }; // Never forward auth subprocess diagnostics.
        process.BeginErrorReadLine();
        return new DotVoiceSession(process, log);
    }

    internal static string ResolvePython(string configured)
    {
        var path = configured.Length > 0 ? configured : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes",
            "codex-primary-runtime", "dependencies", "python", "python.exe");
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException("Native dot calls need Codex's bundled Python or an absolute Python 3.11+ override.", path);
        return path;
    }

    internal static async Task RequireHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Native dot calling is pinned to Codex 0.162.0-alpha.17.2; this runtime needs revalidation.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Exception? failure = null;
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        try
        {
            _process.StandardInput.Close();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally { _process.Dispose(); }
        if (failure is not null)
            throw new VoiceOwnershipCleanupException("Native dot signaling cleanup was incomplete.", [failure]);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
