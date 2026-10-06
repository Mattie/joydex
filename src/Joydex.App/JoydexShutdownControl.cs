using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Joydex.Ipc;

namespace Joydex.App;

/// <summary>
/// Lets the packaged App request the same shutdown as the tray's Exit Joydex item.
/// The pipe is limited to the current Windows user and this executable path.
/// </summary>
internal sealed class JoydexShutdownControl : IAsyncDisposable
{
    private const byte ShutdownRequest = 1;
    private const byte ProbeRequest = 2;
    private const int MaximumConfigurationBytes = 32768;
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _pipeName;
    private readonly byte[] _configurationBytes;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serveTask;
    private NamedPipeServerStream _listener;

    public JoydexShutdownControl(
        string executablePath,
        string configurationPath,
        SynchronizationContext ui,
        Action beginExit)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(beginExit);
        _pipeName = PipeName(executablePath);
        _configurationBytes = StrictUtf8.GetBytes(Path.GetFullPath(configurationPath));
        if (_configurationBytes.Length == 0 || _configurationBytes.Length > MaximumConfigurationBytes)
        {
            throw new ArgumentException("The configuration path is too long.", nameof(configurationPath));
        }
        _listener = CreateListener(_pipeName);
        _serveTask = ServeAsync(ui, beginExit);
    }

    /// <summary>Requests graceful tray exit and returns only after the App and runtime exit.</summary>
    public static async Task<int> RequestAsync(string executablePath) =>
        (await ShutdownAndWaitAsync(executablePath).ConfigureAwait(false)).ExitCode;

    /// <summary>Restarts the tray, or starts the profile's package when Joydex is stopped.</summary>
    public static async Task<int> RestartAsync(string executablePath)
    {
        (string ApplicationPath, string ConfigurationPath) launchPaths;
        try
        {
            launchPaths = JoydexLocalProfileStore.ReadLaunchPaths();
            if (!SamePath(launchPaths.ApplicationPath, executablePath)) return 2;
        }
        catch (Exception exception) when (exception is IOException
            or ArgumentException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return 4;
        }

        var shutdown = await ShutdownAndWaitAsync(executablePath).ConfigureAwait(false);
        if (shutdown.ExitCode is not (0 or 2)) return shutdown.ExitCode;

        try
        {
            var configurationPath = shutdown.ConfigurationPath;
            if (shutdown.ExitCode == 2)
            {
                if (AnotherJoydexInstanceExists()) return 2;
                configurationPath = launchPaths.ConfigurationPath;
            }
            using var started = Process.Start(CreateRestartStartInfo(
                executablePath,
                configurationPath!));
            if (started is null) return 4;
            return await WaitForReadyAsync(
                    started,
                    executablePath,
                    configurationPath!)
                .ConfigureAwait(false) ? 0 : 5;
        }
        catch (Exception exception) when (exception is IOException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return 4;
        }
    }

    private static bool AnotherJoydexInstanceExists()
    {
        if (Mutex.TryOpenExisting(@"Local\Joydex", out var ownership))
        {
            ownership.Dispose();
            return true;
        }
        foreach (var process in Process.GetProcessesByName("Joydex.App"))
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId) return true;
            }
        }
        return false;
    }

    private static ProcessStartInfo CreateRestartStartInfo(
        string executablePath,
        string configurationPath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var startInfo = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(Path.GetFullPath(configurationPath));
        return startInfo;
    }

    private static async Task<(int ExitCode, string? ConfigurationPath)> ShutdownAndWaitAsync(
        string executablePath)
    {
        try
        {
            var target = await RequestTargetAsync(executablePath).ConfigureAwait(false);
            using (target.App)
            {
                if (!target.App.WaitForExit(ExitTimeout)) return (3, null);
            }
            return await WaitForRuntimeExitAsync().ConfigureAwait(false)
                ? (0, target.ConfigurationPath)
                : (3, null);
        }
        catch (Exception exception) when (exception is IOException
            or TimeoutException
            or OperationCanceledException
            or ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return (2, null);
        }
    }

    internal static Task<(Process App, string ConfigurationPath)> RequestTargetAsync(
        string executablePath) => RequestTargetAsync(
            executablePath,
            ShutdownRequest,
            TimeSpan.FromSeconds(5));

    internal static Task<(Process App, string ConfigurationPath)> ProbeTargetAsync(
        string executablePath) => RequestTargetAsync(
            executablePath,
            ProbeRequest,
            TimeSpan.FromSeconds(1));

    private static async Task<(Process App, string ConfigurationPath)> RequestTargetAsync(
        string executablePath,
        byte request,
        TimeSpan timeout)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            PipeName(executablePath),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connecting = new CancellationTokenSource(timeout);
        await pipe.ConnectAsync(connecting.Token).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[] { request }, connecting.Token)
            .ConfigureAwait(false);
        await pipe.FlushAsync(connecting.Token).ConfigureAwait(false);

        var reply = new byte[sizeof(int) + sizeof(long) + sizeof(int)];
        await pipe.ReadExactlyAsync(reply, connecting.Token).ConfigureAwait(false);
        var processId = BinaryPrimitives.ReadInt32LittleEndian(reply);
        var startTicks = BinaryPrimitives.ReadInt64LittleEndian(reply.AsSpan(sizeof(int)));
        var configurationLength = BinaryPrimitives.ReadInt32LittleEndian(
            reply.AsSpan(sizeof(int) + sizeof(long)));
        if (configurationLength is <= 0 or > MaximumConfigurationBytes)
        {
            throw new InvalidDataException("The shutdown responder returned an invalid configuration path length.");
        }
        var configurationBytes = new byte[configurationLength];
        await pipe.ReadExactlyAsync(configurationBytes, connecting.Token).ConfigureAwait(false);
        var configurationPath = Path.GetFullPath(StrictUtf8.GetString(configurationBytes));
        var app = Process.GetProcessById(processId);
        try
        {
            if (app.StartTime.ToUniversalTime().Ticks == startTicks
                && SamePath(app.MainModule?.FileName, executablePath))
            {
                return (app, configurationPath);
            }
        }
        catch
        {
            app.Dispose();
            throw;
        }
        app.Dispose();
        throw new InvalidOperationException("The shutdown responder is not this Joydex executable.");
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();
        try { await _serveTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        _stopping.Dispose();
    }

    private async Task ServeAsync(SynchronizationContext ui, Action beginExit)
    {
        while (!_stopping.IsCancellationRequested)
        {
            var listener = _listener;
            try
            {
                await listener.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
                var request = new byte[1];
                await listener.ReadExactlyAsync(request, _stopping.Token).ConfigureAwait(false);
                if (request[0] is ShutdownRequest or ProbeRequest)
                {
                    using var app = Process.GetCurrentProcess();
                    var reply = new byte[sizeof(int) + sizeof(long) + sizeof(int)];
                    BinaryPrimitives.WriteInt32LittleEndian(reply, app.Id);
                    BinaryPrimitives.WriteInt64LittleEndian(
                        reply.AsSpan(sizeof(int)), app.StartTime.ToUniversalTime().Ticks);
                    BinaryPrimitives.WriteInt32LittleEndian(
                        reply.AsSpan(sizeof(int) + sizeof(long)), _configurationBytes.Length);
                    await listener.WriteAsync(reply, _stopping.Token).ConfigureAwait(false);
                    await listener.WriteAsync(_configurationBytes, _stopping.Token)
                        .ConfigureAwait(false);
                    await listener.FlushAsync(_stopping.Token).ConfigureAwait(false);
                    if (request[0] == ShutdownRequest)
                    {
                        ui.Post(_ => beginExit(), null);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (IOException) when (_stopping.IsCancellationRequested) { }
            catch (IOException) { }
            finally { listener.Dispose(); }

            if (!_stopping.IsCancellationRequested)
            {
                _listener = CreateListener(_pipeName);
            }
        }
    }

    private static NamedPipeServerStream CreateListener(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);

    private static string PipeName(string executablePath)
    {
        var path = Path.GetFullPath(executablePath).ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];
        return RuntimeIpcEndpoint.CreateProduction(ConfigPathResolver.DefaultPath).PipeName
            + "-app-shutdown-" + digest;
    }

    private static bool SamePath(string? actual, string expected) => actual is not null
        && string.Equals(
            Path.GetFullPath(actual),
            Path.GetFullPath(expected),
            StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> WaitForReadyAsync(
        Process started,
        string executablePath,
        string configurationPath)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < ReadyTimeout)
        {
            if (started.HasExited) return false;
            try
            {
                var target = await ProbeTargetAsync(executablePath).ConfigureAwait(false);
                using (target.App)
                {
                    if (target.App.Id == started.Id
                        && SamePath(target.ConfigurationPath, configurationPath))
                    {
                        return true;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException
                or TimeoutException
                or OperationCanceledException
                or ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception) { }
            await Task.Delay(200).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<bool> WaitForRuntimeExitAsync()
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < ExitTimeout)
        {
            if (!Mutex.TryOpenExisting(@"Local\Joydex", out var ownership)) return true;
            ownership.Dispose();
            await Task.Delay(100).ConfigureAwait(false);
        }
        return false;
    }
}
