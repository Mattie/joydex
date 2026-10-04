using System.Text.Json;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Ipc;
using Joydex.RuntimeHost.Production;

namespace Joydex.RuntimeHost;

internal static class Program
{
    private const string Usage =
        "Usage: Joydex.RuntimeHost [--config <absolute-path>] | "
        + "(--demo|--synthetic) --config <absolute-path> --pipe-name <name> "
        + "[--instance-name <name>]";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        using var stopping = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await RunAsync(
                    args,
                    Console.In,
                    Console.Out,
                    Console.Error,
                    stopping.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextReader standardInput,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken stoppingToken,
        IRuntimeHostLiveRunner? liveRunner = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        RuntimeHostCommandLine commandLine;
        try
        {
            commandLine = RuntimeHostCommandLine.Parse(args);
        }
        catch (RuntimeHostUsageException exception)
        {
            await standardError.WriteLineAsync(exception.Message).ConfigureAwait(false);
            await standardError.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        if (commandLine.ShowHelp)
        {
            await standardOutput.WriteLineAsync(Usage).ConfigureAwait(false);
            return 0;
        }

        try
        {
            if (commandLine.Mode != RuntimeHostLaunchMode.Synthetic)
            {
                var livePolicy = RuntimeHostLiveLaunchPolicy.Create(
                    commandLine.Mode,
                    commandLine.ConfigurationPath!,
                    commandLine.PipeName,
                    commandLine.InstanceName);
                liveRunner ??= RuntimeHostLiveRunner.CreateDefault();
                await liveRunner.RunAsync(livePolicy, stoppingToken).ConfigureAwait(false);
                return 0;
            }

            var launchPolicy = SyntheticRuntimeLaunchPolicy.Validate(
                commandLine.ConfigurationPath!);
            var configurationPath = launchPolicy.ConfigurationPath;
            var dataRoot = launchPolicy.DataRoot;
            var endpoint = RuntimeIpcEndpoint.CreateSynthetic(
                dataRoot,
                configurationPath,
                commandLine.PipeName!);
            await using var engine = await RuntimeEngine.StartSyntheticForConfigurationAsync(
                    configurationPath,
                    endpoint.DataRootId,
                    commandLine.InstanceName ?? commandLine.PipeName!,
                    TimeProvider.System)
                .ConfigureAwait(false);
            await using var server = RuntimeIpcServer.Start(
                endpoint,
                (context, clientKind, client, abortConnection, connectionCancellationToken) =>
                    ValueTask.FromResult<IRuntimeRpcServer>(engine.CreateSession(
                        context.ConnectionId,
                        clientKind,
                        client,
                        connectionCancellationToken,
                        abortConnection)));
            var tickets = Enumerable.Range(0, 2)
                .Select(_ => server.IssueLaunchTicket(RuntimeClientKind.HeadlessTest))
                .Select(ticket => new SyntheticRuntimeLaunchTicket(
                    ticket.Value,
                    ticket.ExpiresAtUtc))
                .ToArray();
            var ready = new SyntheticRuntimeHostReady(
                SchemaVersion: 2,
                Environment.ProcessId,
                endpoint.PipeName,
                endpoint.DataRootId,
                tickets);
            await standardOutput
                .WriteLineAsync(JsonSerializer.Serialize(ready, JsonOptions))
                .ConfigureAwait(false);
            await standardOutput.FlushAsync().ConfigureAwait(false);

            await WaitForShutdownAsync(
                    standardInput,
                    engine.ShutdownRequested,
                    stoppingToken)
                .ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            await standardError
                .WriteLineAsync($"Joydex.RuntimeHost failed: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task WaitForShutdownAsync(
        TextReader standardInput,
        Task shutdownRequested,
        CancellationToken stoppingToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var input = WaitForInputShutdownAsync(standardInput, waiting.Token);
        var runtime = shutdownRequested.WaitAsync(waiting.Token);
        var completed = await Task.WhenAny(input, runtime).ConfigureAwait(false);
        await completed.ConfigureAwait(false);
        await waiting.CancelAsync().ConfigureAwait(false);
        try
        {
            await (ReferenceEquals(completed, input) ? runtime : input).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested)
        {
        }
    }

    private static async Task WaitForInputShutdownAsync(
        TextReader standardInput,
        CancellationToken stoppingToken)
    {
        while (await standardInput.ReadLineAsync(stoppingToken).ConfigureAwait(false) is { } command)
        {
            if (string.Equals(command.Trim(), "shutdown", StringComparison.OrdinalIgnoreCase)) return;
        }
    }

    private sealed record SyntheticRuntimeHostReady(
        int SchemaVersion,
        int ProcessId,
        string PipeName,
        string DataRootId,
        SyntheticRuntimeLaunchTicket[] LaunchTickets);

    private sealed record SyntheticRuntimeLaunchTicket(
        string Value,
        DateTimeOffset ExpiresAtUtc);

    private sealed record RuntimeHostCommandLine(
        bool ShowHelp,
        RuntimeHostLaunchMode Mode,
        string? ConfigurationPath,
        string? PipeName,
        string? InstanceName)
    {
        public static RuntimeHostCommandLine Parse(IReadOnlyList<string> args)
        {
            if (args.Count == 1
                && args[0] is "--help" or "-h")
            {
                return new RuntimeHostCommandLine(
                    true,
                    RuntimeHostLaunchMode.Production,
                    null,
                    null,
                    null);
            }

            var synthetic = false;
            var demo = false;
            string? configurationPath = null;
            string? pipeName = null;
            string? instanceName = null;
            for (var index = 0; index < args.Count; index++)
            {
                switch (args[index])
                {
                    case "--synthetic" when !synthetic:
                        synthetic = true;
                        break;
                    case "--demo" when !demo:
                        demo = true;
                        break;
                    case "--config" when configurationPath is null:
                        configurationPath = ReadValue(args, ref index, "--config");
                        break;
                    case "--pipe-name" when pipeName is null:
                        pipeName = ReadValue(args, ref index, "--pipe-name");
                        break;
                    case "--instance-name" when instanceName is null:
                        instanceName = ReadValue(args, ref index, "--instance-name");
                        break;
                    default:
                        throw new RuntimeHostUsageException(
                            $"Unknown or duplicate runtime argument '{args[index]}'.");
                }
            }

            if (synthetic && demo)
            {
                throw new RuntimeHostUsageException(
                    "--demo and --synthetic cannot be combined.");
            }
            var mode = synthetic
                ? RuntimeHostLaunchMode.Synthetic
                : demo
                    ? RuntimeHostLaunchMode.Demo
                    : RuntimeHostLaunchMode.Production;
            if (mode == RuntimeHostLaunchMode.Production)
            {
                if (pipeName is not null || instanceName is not null)
                {
                    throw new RuntimeHostUsageException(
                        "--pipe-name and --instance-name require --demo or --synthetic.");
                }
                configurationPath ??= DefaultProductionConfigurationPath();
            }
            else if (string.IsNullOrWhiteSpace(pipeName))
            {
                throw new RuntimeHostUsageException("--pipe-name is required.");
            }
            if (string.IsNullOrWhiteSpace(configurationPath)
                || !Path.IsPathFullyQualified(configurationPath))
            {
                throw new RuntimeHostUsageException(
                    "--config must select an absolute companion configuration path.");
            }
            if (instanceName is not null && string.IsNullOrWhiteSpace(instanceName))
            {
                throw new RuntimeHostUsageException("--instance-name cannot be empty.");
            }
            return new RuntimeHostCommandLine(
                false,
                mode,
                configurationPath.Trim(),
                pipeName?.Trim(),
                instanceName?.Trim());
        }

        private static string DefaultProductionConfigurationPath()
        {
            var environmentPath = Environment.GetEnvironmentVariable("JOYDEX_CONFIG");
            return string.IsNullOrWhiteSpace(environmentPath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Joydex",
                    "config.json")
                : environmentPath.Trim();
        }

        private static string ReadValue(
            IReadOnlyList<string> args,
            ref int index,
            string option)
        {
            if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new RuntimeHostUsageException($"{option} requires a value.");
            }
            return args[index];
        }
    }

    private sealed class RuntimeHostUsageException(string message) : Exception(message);
}

/// <summary>
/// Validates the synthetic host's existing scratch configuration before the engine or settings
/// stores can create files.
/// </summary>
internal sealed record SyntheticRuntimeLaunchPolicy(string ConfigurationPath, string DataRoot)
{
    internal static SyntheticRuntimeLaunchPolicy Validate(
        string configurationPath,
        string? normalDataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        if (!Path.IsPathFullyQualified(configurationPath))
        {
            throw new InvalidDataException("The synthetic configuration path must be absolute.");
        }

        var normalizedPath = Path.GetFullPath(configurationPath.Trim());
        if (!File.Exists(normalizedPath))
        {
            throw new InvalidDataException(
                "The synthetic configuration file must already exist.");
        }

        var dataRoot = Path.GetDirectoryName(normalizedPath)
            ?? throw new InvalidDataException(
                "The synthetic configuration path has no parent directory.");
        normalDataRoot ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Joydex");
        var normalizedNormalRoot = Path.GetFullPath(normalDataRoot);
        if (IsWithin(dataRoot, normalizedNormalRoot))
        {
            throw new InvalidDataException(
                "The synthetic configuration must be in a dedicated scratch directory outside "
                + "the normal Joydex data directory.");
        }

        CompanionConfig config;
        try
        {
            config = ConfigStore.LoadOrCreate(normalizedPath);
        }
        catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or JsonException)
        {
            throw new InvalidDataException(
                "The synthetic configuration could not be loaded and validated: "
                + exception.Message,
                exception);
        }

        if (!config.Safety.DryRun)
        {
            throw new InvalidDataException(
                "The synthetic configuration must have safety.dryRun set to true.");
        }

        return new SyntheticRuntimeLaunchPolicy(normalizedPath, dataRoot);
    }

    private static bool IsWithin(string candidate, string directory)
    {
        var relative = Path.GetRelativePath(directory, candidate);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
