using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Joydex.Secrets;

namespace Joydex.Secrets.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static async Task<int> Main(string[] args)
        => await RunAsync(args);

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var parsed = Arguments.Parse(args);
            var client = new SecretsBrokerPipeClient(parsed.DataRoot);
            return parsed.Command switch
            {
                "aliases" or "catalog" => await CatalogAsync(client, parsed),
                "exec" => await ExecAsync(client, parsed),
                "request" => await RequestAsync(client, parsed),
                "wait" => await WaitAsync(client, parsed),
                "cancel" => await CancelAsync(client, parsed),
                "run" => await RunAsync(client, parsed),
                "status" or "stop" => await TaskControlAsync(parsed),
                _ => throw new ArgumentException(Usage),
            };
        }
        catch (EndOfStreamException)
        {
            Console.Error.WriteLine("The Joydex broker connection was lost. Execution outcome is unconfirmed; check the task ID before retrying a detached launch.");
            return 2;
        }
        catch (Exception exception) when (exception is ArgumentException
            or System.ComponentModel.Win32Exception
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or OperationCanceledException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static async Task<int> ExecAsync(SecretsBrokerPipeClient client, Arguments args)
    {
        var detached = args.Optional("detach") is not null;
        if (detached && (args.Optional("output-mode") is not null
            || args.Optional("on-approval-timeout") is not (null or "cancel")))
            throw new ArgumentException("Detached execution requires normal approval cancellation and returns its own launch receipt; omit --output-mode.");
        var outputMode = args.Optional("output-mode") ?? "passthrough";
        if (outputMode is not ("passthrough" or "json"))
            throw new ArgumentException("--output-mode must be passthrough or json.");
        var passthrough = !detached && outputMode == "passthrough";
        var clientId = args.Required("client");
        var projectReference = args.Required("project");
        var reason = args.Required("reason");
        var secrets = args.Many("secret");
        if (secrets.Count is < 1 or > 64)
        {
            throw new ArgumentException("Exec requires between 1 and 64 --secret ENV_NAME options.");
        }
        if (secrets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != secrets.Count
            || secrets.Any(secret => !IsEnvironmentName(secret)))
        {
            throw new ArgumentException("Each --secret must be a unique environment variable name.");
        }
        if (args.CommandTokens.Count == 0)
        {
            throw new ArgumentException("Exec requires an executable after --.");
        }

        var approvalTimeout = ParseSeconds(args.Optional("approval-timeout"), 90, 1, 110, "approval-timeout");
        var executionTimeout = detached ? 30 : ParseSeconds(args.Optional("execution-timeout"), 3600, 1, 3600, "execution-timeout");
        int? detachedTimeout = detached && args.Optional("execution-timeout") is { } duration
            ? ParseSeconds(duration, 3600, 1, 2147483, "execution-timeout") : null;
        var requestId = args.Optional("request") ?? $"exec-{Guid.NewGuid():N}";
        var taskId = detached ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(clientId + "\n" + projectReference + "\n" + requestId)))[..32].ToLowerInvariant() : null;
        if (taskId is not null) Console.Error.WriteLine("Joydex task ID: " + taskId);
        var timeoutAction = ParseTimeoutAction(args.Optional("on-approval-timeout"));
        var workingDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        var operation = new ExecOperationProposal(
            ResolveExecutable(args.CommandTokens[0], workingDirectory),
            args.CommandTokens.Skip(1).ToArray(),
            workingDirectory,
            secrets.ToDictionary(secret => secret, secret => secret, StringComparer.OrdinalIgnoreCase),
            args.Many("fingerprint").Select(path => Path.GetFullPath(path, workingDirectory)).ToArray(),
            detached ? SecretOutputDisclosure.None : passthrough ? SecretOutputDisclosure.Passthrough : SecretOutputDisclosure.Summary,
            Lifetime: detached ? SecretsExecutionLifetime.Detached : SecretsExecutionLifetime.Attached,
            DetachedTimeoutSeconds: detachedTimeout, TaskId: taskId);
        EnsureLocalRequester(args, clientId, projectReference);
        var reply = await SendAsync(client, new(
            SecretsBrokerCommandKind.Request,
            clientId,
            projectReference,
            requestId,
            reason,
            Operation: operation));

        if (reply.Status == SecretsRequestStatus.Pending)
        {
            var poll = new SecretsBrokerCommand(
                SecretsBrokerCommandKind.Poll,
                clientId,
                projectReference,
                requestId);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(approvalTimeout);
            while (reply.Status == SecretsRequestStatus.Pending && DateTimeOffset.UtcNow < deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(500)
                    ? remaining
                    : TimeSpan.FromMilliseconds(500));
                reply = await SendAsync(client, poll);
            }
        }

        if (reply.Status == SecretsRequestStatus.Pending)
        {
            reply = await ResolveApprovalTimeoutAsync(
                client,
                timeoutAction,
                clientId,
                projectReference,
                requestId,
                operation,
                executionTimeout, passthrough);
            if (reply.Status == SecretsRequestStatus.Completed && reply.Execution is { } fallbackExecution)
            {
                WriteExecReply(reply, passthrough);
                return fallbackExecution.ExitCode == 0 ? 0 : fallbackExecution.ExitCode;
            }
            if (reply.Status == SecretsRequestStatus.Allowed)
            {
                return await RunApprovedAndWriteAsync(
                    client,
                    clientId,
                    projectReference,
                    requestId,
                    reply.Reservation!,
                    executionTimeout, passthrough);
            }
            WriteExecReply(reply, passthrough);
            return reply.Status == SecretsRequestStatus.AgentDetached ? 12 : ExitCode(reply.Status);
        }

        if (reply.Status == SecretsRequestStatus.Allowed)
        {
            return await RunApprovedAndWriteAsync(
                client,
                clientId,
                projectReference,
                requestId,
                reply.Reservation!,
                executionTimeout, passthrough);
        }

        WriteExecReply(reply, passthrough);
        return ExitCode(reply.Status);
    }

    private static async Task<SecretsBrokerReply> ResolveApprovalTimeoutAsync(
        SecretsBrokerPipeClient client,
        ApprovalTimeoutAction action,
        string clientId,
        string projectReference,
        string requestId,
        ExecOperationProposal operation,
        int executionTimeout,
        bool passthrough)
    {
        if (action == ApprovalTimeoutAction.Cancel)
        {
            var cancelled = await SendAsync(client, new(
                SecretsBrokerCommandKind.Cancel,
                clientId,
                projectReference,
                requestId));
            return cancelled.Status == SecretsRequestStatus.Denied
                ? cancelled with { Error = "Approval timed out. The command was not started." }
                : cancelled;
        }

        var reason = action == ApprovalTimeoutAction.RunWithoutSecrets
            ? SecretsDetachReason.RunWithoutSecrets
            : SecretsDetachReason.LeavePending;
        var detached = await SendAsync(client, new(
            SecretsBrokerCommandKind.Detach,
            clientId,
            projectReference,
            requestId,
            DetachReason: reason));
        if (detached.Status != SecretsRequestStatus.AgentDetached
            || action == ApprovalTimeoutAction.LeavePending)
        {
            return detached;
        }

        return await RunWithoutSecretsAsync(operation, executionTimeout, requestId, passthrough);
    }

    private static async Task<int> RunApprovedAndWriteAsync(
        SecretsBrokerPipeClient client,
        string clientId,
        string projectReference,
        string requestId,
        string reservation,
        int executionTimeout,
        bool passthrough)
    {
        using var streams = passthrough ? new CliStandardStreams() : null;
        var reply = await client.SendAsync(
            new(
                SecretsBrokerCommandKind.Run,
                clientId,
                projectReference,
                requestId,
                Reservation: reservation,
                ExecutionTimeoutSeconds: executionTimeout,
                StandardStreamsId: streams?.Id,
                CallerEnvironment: CaptureCallerEnvironment()),
            TimeSpan.FromSeconds(executionTimeout) + TimeSpan.FromSeconds(15),
            CancellationToken.None);
        if (streams is not null && reply.Status == SecretsRequestStatus.Completed)
            await streams.DrainAsync();
        WriteExecReply(reply, passthrough);
        return reply.Execution is { } execution ? execution.ExitCode : ExitCode(reply.Status);
    }

    private static async Task<int> CatalogAsync(SecretsBrokerPipeClient client, Arguments args)
    {
        var clientId = args.Required("client");
        var projectReference = args.Required("project");
        EnsureLocalRequester(args, clientId, projectReference);
        var reply = await SendAsync(client, new(
            SecretsBrokerCommandKind.Catalog,
            clientId,
            projectReference));
        Write(reply);
        return ExitCode(reply.Status);
    }

    private static async Task<int> TaskControlAsync(Arguments args)
    {
        var receipt = await SecretsDetachedTask.ControlAsync(args.DataRoot, args.Required("task"), args.Command == "stop");
        Console.WriteLine(JsonSerializer.Serialize(receipt, JsonOptions));
        return receipt.State == "unknown" ? 2 : 0;
    }

    private static async Task<int> RequestAsync(SecretsBrokerPipeClient client, Arguments args)
    {
        var recipe = args.Optional("recipe");
        var operationPath = args.Optional("operation-file");
        if ((recipe is null) == (operationPath is null))
        {
            throw new ArgumentException("Request requires exactly one --recipe or --operation-file.");
        }

        ExecOperationProposal? operation = null;
        if (operationPath is not null)
        {
            var fullPath = Path.GetFullPath(operationPath);
            if (Path.GetFileName(fullPath).StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("An operation file cannot be a dotenv file.");
            }
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length is <= 0 or > 512 * 1024)
            {
                throw new InvalidDataException("The operation file is missing or exceeds 512 KiB.");
            }
            await using var stream = info.OpenRead();
            operation = await JsonSerializer.DeserializeAsync<ExecOperationProposal>(
                stream,
                JsonOptions) ?? throw new InvalidDataException("The operation file is empty.");
        }

        var parameters = args.Many("parameter")
            .Select(ParseAssignment)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var requestId = args.Optional("request") ?? $"request-{Guid.NewGuid():N}";
        var clientId = args.Required("client");
        var projectReference = args.Required("project");
        EnsureLocalRequester(args, clientId, projectReference);
        var reply = await SendAsync(client, new(
            SecretsBrokerCommandKind.Request,
            clientId,
            projectReference,
            requestId,
            args.Required("reason"),
            recipe,
            parameters,
            operation));
        Write(reply);
        return ExitCode(reply.Status);
    }

    private static async Task<int> WaitAsync(SecretsBrokerPipeClient client, Arguments args)
    {
        var command = new SecretsBrokerCommand(
            SecretsBrokerCommandKind.Poll,
            args.Required("client"),
            args.Required("project"),
            args.Required("request"));
        var timeoutText = args.Optional("timeout");
        var seconds = 600;
        if (timeoutText is not null
            && !int.TryParse(timeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
        {
            throw new ArgumentException("--timeout must be a whole number of seconds.");
        }
        if (seconds is < 1 or > 3600)
        {
            throw new ArgumentException("--timeout must be between 1 and 3600 seconds.");
        }
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        SecretsBrokerReply reply;
        do
        {
            reply = await SendAsync(client, command);
            if (reply.Status != SecretsRequestStatus.Pending) break;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(500)
                ? remaining
                : TimeSpan.FromMilliseconds(500));
        }
        while (true);
        Write(reply);
        return reply.Status == SecretsRequestStatus.Pending ? 12 : ExitCode(reply.Status);
    }

    private static async Task<int> CancelAsync(SecretsBrokerPipeClient client, Arguments args)
    {
        var reply = await SendAsync(client, new(
            SecretsBrokerCommandKind.Cancel,
            args.Required("client"),
            args.Required("project"),
            args.Required("request")));
        Write(reply);
        return ExitCode(reply.Status);
    }

    private static async Task<int> RunAsync(SecretsBrokerPipeClient client, Arguments args)
    {
        var outputMode = args.Optional("output-mode") ?? "json";
        if (outputMode is not ("passthrough" or "json"))
            throw new ArgumentException("--output-mode must be passthrough or json.");
        if (outputMode == "passthrough")
            return await RunApprovedAndWriteAsync(client, args.Required("client"), args.Required("project"),
                args.Required("request"), args.Required("reservation"), 3600, passthrough: true);
        var reply = await SendAsync(client, new(
            SecretsBrokerCommandKind.Run,
            args.Required("client"),
            args.Required("project"),
            args.Required("request"),
            Reservation: args.Required("reservation"),
            CallerEnvironment: CaptureCallerEnvironment()));
        Write(reply);
        return reply.Status == SecretsRequestStatus.Completed
            && reply.Execution is { ExitCode: not 0 } execution
                ? NormalizeChildExitCode(execution.ExitCode)
                : ExitCode(reply.Status);
    }

    private static Task<SecretsBrokerReply> SendAsync(
        SecretsBrokerPipeClient client,
        SecretsBrokerCommand command) =>
        client.SendAsync(
            command,
            command.Kind == SecretsBrokerCommandKind.Run
                ? TimeSpan.FromHours(1) + TimeSpan.FromSeconds(15)
                : TimeSpan.FromSeconds(3),
            CancellationToken.None);

    private static int ParseSeconds(
        string? value,
        int defaultValue,
        int minimum,
        int maximum,
        string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds < minimum
            || seconds > maximum)
        {
            throw new ArgumentException(
                $"--{option} must be between {minimum} and {maximum} seconds.");
        }
        return seconds;
    }

    private static ApprovalTimeoutAction ParseTimeoutAction(string? value) => value switch
    {
        null or "cancel" => ApprovalTimeoutAction.Cancel,
        "run-without-secrets" => ApprovalTimeoutAction.RunWithoutSecrets,
        "leave-pending" => ApprovalTimeoutAction.LeavePending,
        _ => throw new ArgumentException(
            "--on-approval-timeout must be cancel, run-without-secrets, or leave-pending."),
    };

    private static string ResolveExecutable(string value, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || Path.IsPathFullyQualified(value))
        {
            return RequireExistingFile(Path.GetFullPath(value, workingDirectory));
        }

        var extensions = Path.HasExtension(value)
            ? new[] { string.Empty }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Prepend(string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var directories = new[] { workingDirectory }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), value + extension));
                    if (File.Exists(candidate)) return RequireRunnableFile(candidate);
                }
                catch (Exception exception) when (exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
                {
                }
            }
        }
        throw new ArgumentException($"The executable '{value}' was not found in the current directory or PATH.");
    }

    private static string RequireExistingFile(string path) => File.Exists(path)
        ? RequireRunnableFile(path)
        : throw new ArgumentException($"The executable '{path}' does not exist.");

    private static string RequireRunnableFile(string path)
    {
        if (Path.GetExtension(path) is { } extension
            && (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"'{path}' is a script. Name cmd.exe or powershell.exe after --, pass the script as an argument, and add --fingerprint SCRIPT_PATH before -- to bind approval to its contents.");
        }
        return path;
    }

    private static bool IsEnvironmentName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && !char.IsAsciiDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string DisplayLabel(string clientId)
    {
        var words = clientId.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var label = string.Join(
            " ",
            words.Select(word => word.Length == 0
                ? word
                : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
        return string.IsNullOrWhiteSpace(label) ? clientId : label;
    }

    private static void EnsureLocalRequester(
        Arguments args,
        string clientId,
        string projectReference) =>
        _ = LocalSecretsRequester.EnsureReady(
            args.DataRoot,
            clientId,
            DisplayLabel(clientId),
            projectReference,
            Environment.CurrentDirectory);

    private static async Task<SecretsBrokerReply> RunWithoutSecretsAsync(
        ExecOperationProposal operation,
        int executionTimeout,
        string requestId,
        bool passthrough)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = operation.Executable,
            WorkingDirectory = operation.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = !passthrough,
            RedirectStandardError = !passthrough,
            RedirectStandardInput = passthrough,
        };
        foreach (var argument in operation.Arguments) startInfo.ArgumentList.Add(argument);

        using var process = SecretsOwnedProcess.Start(startInfo);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(executionTimeout));
        async Task<string> Relay(Stream source, Stream destination)
        {
            try { await source.CopyToAsync(destination, timeout.Token); return string.Empty; }
            catch { timeout.Cancel(); throw; }
        }
        async Task Input()
        {
            try { await Console.OpenStandardInput().CopyToAsync(process.StandardInput.BaseStream, timeout.Token); }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { try { process.StandardInput.Close(); } catch (ObjectDisposedException) { } }
        }
        if (passthrough) _ = Input();
        var outputTask = passthrough ? Relay(process.StandardOutput.BaseStream, Console.OpenStandardOutput()) : ReadBoundedAsync(process.StandardOutput);
        var errorTask = passthrough ? Relay(process.StandardError.BaseStream, Console.OpenStandardError()) : ReadBoundedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            var stopped = process.TerminateAndConfirm();
            await ObserveCaptureAsync(outputTask);
            await ObserveCaptureAsync(errorTask);
            return new(
                stopped ? SecretsRequestStatus.Revoked : SecretsRequestStatus.LaunchUnconfirmed,
                requestId,
                Error: stopped ? "The command was cancelled and its process tree stopped." : "Process cleanup could not be confirmed.");
        }
        var output = await outputTask;
        var error = await errorTask;
        timeout.Cancel();
        return new(
            SecretsRequestStatus.Completed,
            requestId,
            Execution: new(process.ExitCode, output, error),
            SecretsInjected: false);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        const int limit = 16 * 1024;
        var buffer = new char[4096];
        var result = new System.Text.StringBuilder();
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer);
            if (count == 0) break;
            var remaining = limit - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        return truncated ? "[OUTPUT TRUNCATED]" : result.ToString();
    }

    private static async Task ObserveCaptureAsync(Task<string> capture)
    {
        try { _ = await capture.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception exception) when (exception is IOException
            or ObjectDisposedException
            or OperationCanceledException
            or TimeoutException)
        {
        }
    }

    private static IReadOnlyDictionary<string, string> CaptureCallerEnvironment()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && !string.IsNullOrEmpty(name) && !name.Contains('='))
                snapshot[name] = variable.Value?.ToString() ?? string.Empty;
        }
        return snapshot;
    }

    private static KeyValuePair<string, string> ParseAssignment(string value)
    {
        var separator = value.IndexOf('=');
        if (separator < 1 || separator == value.Length - 1)
        {
            throw new ArgumentException("Parameters use --parameter name=value.");
        }
        return new(value[..separator], value[(separator + 1)..]);
    }

    private static void WriteExecReply(SecretsBrokerReply reply, bool passthrough)
    {
        if (reply.Execution?.Task is { } task)
        {
            Console.WriteLine(JsonSerializer.Serialize(task, JsonOptions));
            return;
        }
        if (!passthrough) { Write(reply); return; }
        if (reply.Status != SecretsRequestStatus.Completed)
            Console.Error.WriteLine("joydex-secrets: " + (reply.Error ?? $"Command was not launched ({reply.Status})."));
    }

    private static void Write(SecretsBrokerReply reply) =>
        Console.WriteLine(JsonSerializer.Serialize(reply, JsonOptions));

    private static int ExitCode(SecretsRequestStatus status) => status switch
    {
        SecretsRequestStatus.Allowed or SecretsRequestStatus.Completed => 0,
        SecretsRequestStatus.Pending => 10,
        SecretsRequestStatus.AgentDetached => 12,
        SecretsRequestStatus.Denied or SecretsRequestStatus.Expired or SecretsRequestStatus.Revoked => 11,
        SecretsRequestStatus.ProviderUnavailable => 13,
        SecretsRequestStatus.IdentityUnverified => 14,
        _ => 15,
    };

    private static int NormalizeChildExitCode(int exitCode) =>
        exitCode is >= 1 and <= 255 ? exitCode : 15;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private const string Usage = "Usage: joydex-secrets exec [--detach] --client ID --project REF --reason TEXT --secret ENV_NAME [--secret ENV_NAME] [--fingerprint PATH ...] [--approval-timeout SECONDS] [--on-approval-timeout cancel|run-without-secrets|leave-pending] [--execution-timeout SECONDS] [--output-mode passthrough|json] -- PROGRAM [ARG ...]\nDetached control: joydex-secrets <status|stop> --task TASK_ID [--data-root PATH]\nDetached execution rejects --output-mode and alternative approval-timeout actions.\nAdvanced: joydex-secrets <aliases|request|wait|cancel|run> --client ID --project REF [options]\nAdvanced run: add --output-mode passthrough when the approved operation declares outputDisclosure: passthrough; otherwise omit it for JSON results.";

    private enum ApprovalTimeoutAction
    {
        Cancel,
        RunWithoutSecrets,
        LeavePending,
    }

    private sealed class Arguments
    {
        private readonly Dictionary<string, List<string>> _options;

        private Arguments(
            string command,
            string dataRoot,
            Dictionary<string, List<string>> options,
            IReadOnlyList<string> commandTokens)
        {
            Command = command;
            DataRoot = dataRoot;
            _options = options;
            CommandTokens = commandTokens;
        }

        public string Command { get; }

        public string DataRoot { get; }

        public IReadOnlyList<string> CommandTokens { get; }

        public string Required(string name) => Optional(name)
            ?? throw new ArgumentException($"--{name} is required.\n{Usage}");

        public string? Optional(string name)
        {
            if (!_options.TryGetValue(name, out var values)) return null;
            if (values.Count != 1)
            {
                throw new ArgumentException($"--{name} may be supplied once.");
            }
            return values[0];
        }

        public IReadOnlyList<string> Many(string name) => _options.TryGetValue(name, out var values)
            ? values
            : [];

        public static Arguments Parse(IReadOnlyList<string> args)
        {
            if (args.Count == 0) throw new ArgumentException(Usage);
            var command = args[0].Trim().ToLowerInvariant();
            var separator = command == "exec"
                ? Enumerable.Range(1, Math.Max(0, args.Count - 1))
                    .FirstOrDefault(index => string.Equals(args[index], "--", StringComparison.Ordinal), -1)
                : -1;
            if (command == "exec" && (separator < 0 || separator == args.Count - 1))
            {
                throw new ArgumentException("Exec requires -- followed by an executable.\n" + Usage);
            }
            var optionsEnd = separator >= 0 ? separator : args.Count;
            var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            for (var index = 1; index < optionsEnd; index += 2)
            {
                var option = args[index];
                if (option == "--detach" && command == "exec")
                {
                    if (!options.TryAdd("detach", ["true"])) throw new ArgumentException("--detach may be supplied once.");
                    index--; continue;
                }
                if (!option.StartsWith("--", StringComparison.Ordinal)
                    || option.Length < 3
                    || index + 1 >= optionsEnd)
                {
                    throw new ArgumentException(Usage);
                }
                var name = option[2..];
                if (!options.TryGetValue(name, out var values))
                {
                    values = [];
                    options.Add(name, values);
                }
                values.Add(args[index + 1]);
            }
            var dataRoot = options.TryGetValue("data-root", out var roots)
                ? roots.Count == 1
                    ? roots[0]
                    : throw new ArgumentException("--data-root may be supplied once.")
                : SecretsPaths.GetDefaultDataRoot();
            options.Remove("data-root");
            var allowed = command switch
            {
                "aliases" or "catalog" => new[] { "client", "project" },
                "exec" => [
                    "detach",
                    "client",
                    "project",
                    "request",
                    "reason",
                    "secret",
                    "fingerprint",
                    "approval-timeout",
                    "on-approval-timeout",
                    "execution-timeout",
                    "output-mode"],
                "request" => ["client", "project", "request", "reason", "recipe", "operation-file", "parameter"],
                "wait" => ["client", "project", "request", "timeout"],
                "cancel" => ["client", "project", "request"],
                "run" => ["client", "project", "request", "reservation", "output-mode"],
                "status" or "stop" => ["task"],
                _ => throw new ArgumentException(Usage),
            };
            var unknown = options.Keys.FirstOrDefault(name => !allowed.Contains(name, StringComparer.Ordinal));
            if (unknown is not null) throw new ArgumentException($"--{unknown} is not valid for {command}.");
            return new(
                command,
                Path.GetFullPath(dataRoot),
                options,
                separator >= 0 ? args.Skip(separator + 1).ToArray() : []);
        }
    }
}
