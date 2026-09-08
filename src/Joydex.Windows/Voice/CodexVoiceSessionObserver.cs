using System.Text;

namespace Joydex.Windows.Voice;

public enum CodexVoiceLogMarker
{
    None,
    Started,
    Stopped,
}

internal static class CodexVoiceLogMarkerParser
{
    private static readonly byte[] Started = Encoding.ASCII.GetBytes("realtime_session_started");
    private static readonly byte[] Stopped = Encoding.ASCII.GetBytes("method=thread/realtime/stop");
    private static readonly byte[] StopStructure = Encoding.ASCII.GetBytes("originWebcontentsId=");

    public static CodexVoiceLogMarker Detect(ReadOnlySpan<byte> bytes)
    {
        var stoppedAt = bytes.IndexOf(Stopped);
        if (stoppedAt >= 0
            && bytes[stoppedAt..Math.Min(bytes.Length, stoppedAt + 256)].IndexOf(StopStructure) >= 0)
        {
            return CodexVoiceLogMarker.Stopped;
        }

        return bytes.IndexOf(Started) >= 0
            ? CodexVoiceLogMarker.Started
            : CodexVoiceLogMarker.None;
    }
}

/// <summary>
/// Tails only newly appended Codex desktop log bytes and emits allowlisted realtime lifecycle
/// markers. It never decodes, stores, or logs transcript-bearing lines.
/// </summary>
public sealed class CodexVoiceSessionObserver(
    string logRoot,
    Func<CodexVoiceLogMarker, CancellationToken, ValueTask> onMarker,
    Action<string>? log = null) : IAsyncDisposable
{
    private const int ReadBufferSize = 64 * 1024;
    // The stop parser accepts its structural proof up to 256 bytes after the
    // method token. Retain the full proof window plus the token across writes.
    private const int MarkerBoundaryBytes = 320;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);

    private readonly string _logRoot = Path.GetFullPath(logRoot);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Dictionary<string, TailState> _files = new(StringComparer.OrdinalIgnoreCase);
    private Task? _runTask;

    public void Start()
    {
        if (_runTask is { IsCompleted: false })
        {
            throw new InvalidOperationException("The Codex Voice session observer is already running.");
        }

        if (!Directory.Exists(_logRoot))
        {
            throw new DirectoryNotFoundException($"Codex desktop log directory '{_logRoot}' was not found.");
        }

        SeedExistingFiles();
        _runTask = RunAsync(_cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
        }

        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    public static string? FindDefaultLogRoot()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(localApplicationData, "Packages");
        if (!Directory.Exists(packages))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(packages, "OpenAI.Codex_*", SearchOption.TopDirectoryOnly)
                .Select(package => Path.Combine(package, "LocalCache", "Local", "Codex", "Logs"))
                .Where(Directory.Exists)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SeedExistingFiles()
    {
        foreach (var path in EnumerateLogFiles())
        {
            try
            {
                _files[path] = new TailState(new FileInfo(path).Length, []);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var nextRescan = DateTimeOffset.UtcNow + RescanInterval;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= nextRescan)
            {
                DiscoverNewFiles();
                nextRescan = DateTimeOffset.UtcNow + RescanInterval;
            }

            foreach (var path in _files.Keys.ToArray())
            {
                await ReadAppendedBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private void DiscoverNewFiles()
    {
        foreach (var path in EnumerateLogFiles())
        {
            if (!_files.ContainsKey(path))
            {
                _files[path] = new TailState(0, []);
            }
        }
    }

    private IEnumerable<string> EnumerateLogFiles()
    {
        try
        {
            return Directory.EnumerateFiles(
                    _logRoot,
                    "codex-desktop-*.log",
                    SearchOption.AllDirectories)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Codex Voice log rescan failed: {exception.Message}");
            return [];
        }
    }

    private async Task ReadAppendedBytesAsync(string path, CancellationToken cancellationToken)
    {
        if (!_files.TryGetValue(path, out var state))
        {
            return;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (state.Offset > stream.Length)
            {
                state = new TailState(0, []);
            }

            stream.Position = state.Offset;
            var readBuffer = new byte[ReadBufferSize];
            while (stream.Position < stream.Length)
            {
                var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                var combined = new byte[state.Boundary.Length + bytesRead];
                state.Boundary.CopyTo(combined, 0);
                readBuffer.AsSpan(0, bytesRead).CopyTo(combined.AsSpan(state.Boundary.Length));
                var marker = CodexVoiceLogMarkerParser.Detect(combined);
                if (marker != CodexVoiceLogMarker.None)
                {
                    try
                    {
                        await onMarker(marker, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        log?.Invoke($"Codex Voice lifecycle callback failed: {exception.Message}");
                    }
                }

                var boundaryLength = Math.Min(MarkerBoundaryBytes, combined.Length);
                state = new TailState(
                    stream.Position,
                    combined.AsSpan(combined.Length - boundaryLength, boundaryLength).ToArray());
            }

            _files[path] = state with { Offset = stream.Position };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Codex Voice log tail is temporarily unavailable: {exception.Message}");
        }
    }

    private sealed record TailState(long Offset, byte[] Boundary);
}
