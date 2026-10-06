using Joydex.Ipc;

namespace Joydex.App;

internal enum RuntimeAppStartupMode
{
    Tray,
    Settings,
}

/// <summary>
/// Selects the App process role and transfers startup ownership to the matching application
/// context. The Settings role reads exactly one bounded Bootstrap before handing off the stream.
/// </summary>
internal static class RuntimeAppStartup
{
    internal const string SettingsArgument = "--settings";

    public static RuntimeAppStartupMode SelectMode(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var settingsArguments = args.Count(
            argument => string.Equals(
                argument,
                SettingsArgument,
                StringComparison.OrdinalIgnoreCase));
        if (settingsArguments == 0)
        {
            return RuntimeAppStartupMode.Tray;
        }
        if (settingsArguments != 1 || args.Count != 1)
        {
            throw new InvalidDataException(
                "Settings child mode requires exactly: --settings.");
        }
        return RuntimeAppStartupMode.Settings;
    }

    public static void Run(
        IReadOnlyList<string> args,
        RuntimeAppStartupMode mode,
        DemoLaunchPolicy? demoPolicy,
        IRuntimeAppStartupHost host)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);
        if (SelectMode(args) != mode)
        {
            throw new InvalidOperationException("The selected App startup mode changed before launch.");
        }

        if (mode == RuntimeAppStartupMode.Settings)
        {
            if (demoPolicy is not null)
            {
                throw new InvalidDataException("Settings child mode cannot select demo startup.");
            }
            RunSettings(host);
            return;
        }

        var configurationPath = demoPolicy?.ConfigPath ?? ConfigPathResolver.Resolve(args);
        host.RunTray(
            configurationPath,
            demoMode: demoPolicy is not null,
            demoPipeName: null);
    }

    private static void RunSettings(IRuntimeAppStartupHost host)
    {
        Stream? channel = host.OpenSettingsChannel();
        try
        {
            ArgumentNullException.ThrowIfNull(channel);
            if (!channel.CanRead)
            {
                throw new InvalidDataException("The inherited Settings channel is not readable.");
            }

            var bootstrap = ReadBootstrap(channel);
            _ = RuntimeSettingsChannel.GetBootstrapEndpoint(bootstrap);

            var transferredChannel = channel;
            channel = null;
            host.RunSettings(bootstrap, transferredChannel);
        }
        finally
        {
            channel?.Dispose();
        }
    }

    private static RuntimeSettingsChannelMessage ReadBootstrap(Stream channel)
    {
        // Main remains on its STA thread. Reading on the pool avoids capturing a future WinForms
        // context while the host blocks for the one message required before the UI can be built.
        return Task.Run(
                async () => await RuntimeSettingsChannel
                    .ReadAsync(channel)
                    .ConfigureAwait(false))
            .GetAwaiter()
            .GetResult()
            ?? throw new EndOfStreamException(
                "The inherited Settings channel closed before Bootstrap.");
    }
}

/// <summary>Runs the selected startup role; tests replace it without opening UI or process handles.</summary>
internal interface IRuntimeAppStartupHost
{
    Stream OpenSettingsChannel();

    void RunSettings(RuntimeSettingsChannelMessage bootstrap, Stream channel);

    void RunTray(string configurationPath, bool demoMode, string? demoPipeName);
}

internal sealed class WindowsFormsRuntimeAppStartupHost : IRuntimeAppStartupHost
{
    public static WindowsFormsRuntimeAppStartupHost Instance { get; } = new();

    private WindowsFormsRuntimeAppStartupHost()
    {
    }

    public Stream OpenSettingsChannel() => Console.OpenStandardInput();

    public void RunSettings(RuntimeSettingsChannelMessage bootstrap, Stream channel)
    {
        var previousContext = SynchronizationContext.Current;
        WindowsFormsSynchronizationContext? installedContext = null;
        if (previousContext is null)
        {
            installedContext = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(installedContext);
        }

        var uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("The Settings UI context could not be initialized.");
        RuntimeSettingsApplicationContext? context = null;
        try
        {
            context = new RuntimeSettingsApplicationContext(bootstrap, channel, uiContext);
            using (context)
            {
                Application.Run(context);
            }
        }
        catch
        {
            if (context is null)
            {
                channel.Dispose();
            }
            throw;
        }
        finally
        {
            if (installedContext is not null)
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                installedContext.Dispose();
            }
        }
    }

    public void RunTray(string configurationPath, bool demoMode, string? demoPipeName)
    {
        if (!demoMode)
        {
            _ = JoydexLocalProfileStore.TryPublish(configurationPath, AppContext.BaseDirectory);
        }
        using var context = new RuntimeTrayApplicationContext(
            configurationPath,
            demoMode,
            demoPipeName);
        Application.Run(context);
    }
}
