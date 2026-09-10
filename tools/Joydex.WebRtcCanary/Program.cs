using System.Diagnostics;

namespace Joydex.WebRtcCanary;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CanaryOptions? options = null;
        try
        {
            options = CanaryOptions.Parse(args);
            var state = new CanaryState();
            state.Configure(options.CodexVersion, "ChatGPT", options.AttestationMode);
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            await using var appServer = new CodexAppServerClient(
                options.CodexPath,
                requestAttestation: string.Equals(options.AttestationMode, "observe", StringComparison.OrdinalIgnoreCase));
            appServer.AttestationRequested += state.AttestationRequested;

            if (string.Equals(options.Mode, "ownership", StringComparison.OrdinalIgnoreCase))
            {
                var prototype = new CodexOwnershipPrototype(
                    options.CodexPath,
                    options.ThreadId,
                    options.ThreadTitle,
                    Environment.CurrentDirectory);
                return options.Auto
                    ? await prototype.RunAutomatedAsync(cancellation.Token).ConfigureAwait(false)
                    : await prototype.RunInteractiveAsync(cancellation.Token).ConfigureAwait(false);
            }

            if (string.Equals(options.Mode, "desktop-attach", StringComparison.OrdinalIgnoreCase))
            {
                var observation = DesktopAppServerObservation.FromOptions(
                    options.DesktopPid,
                    options.DesktopCommandLineBase64,
                    options.DesktopListeners);
                DesktopAttachPrototype.Render(observation);
                return DesktopAttachPrototype.Assess(observation).VerifiedEndpoint is null ? 2 : 0;
            }

            if (string.Equals(options.Mode, "dedicated-create", StringComparison.OrdinalIgnoreCase))
            {
                var creator = new CodexDedicatedTaskCreator(
                    options.CodexPath,
                    options.ThreadTitle,
                    Environment.CurrentDirectory);
                await creator.RunAsync(cancellation.Token).ConfigureAwait(false);
                return 0;
            }

            await appServer.StartAsync(cancellation.Token).ConfigureAwait(false);

            state.HostReady();

            var realtime = new CodexRealtimeCanary(
                appServer,
                state,
                options.ThreadId,
                options.ThreadTitle,
                options.EphemeralThread);
            await using var host = new WebRtcCanaryHost(
                options.Port,
                state,
                realtime,
                options.InputWav,
                options.CaptureWebm);
            host.Start();

            Console.WriteLine($"Joydex WebRTC canary: {host.Url}");
            Console.WriteLine("Press Ctrl+C to stop the host.");
            if (options.OpenBrowser)
            {
                Process.Start(new ProcessStartInfo(host.Url) { UseShellExecute = true });
            }

            await host.RunAsync(cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return string.Equals(options?.Mode, "ownership", StringComparison.OrdinalIgnoreCase)
                ? 130
                : 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"JOYDEX CANARY FAILED: {exception.Message}");
            return 1;
        }
    }
}

internal sealed record CanaryOptions(
    int Port,
    string Mode,
    string CodexPath,
    string CodexVersion,
    string ThreadTitle,
    string? ThreadId,
    string? InputWav,
    string? CaptureWebm,
    string AttestationMode,
    bool EphemeralThread,
    bool OpenBrowser,
    bool Auto,
    int? DesktopPid,
    string? DesktopCommandLineBase64,
    string[] DesktopListeners)
{
    public static CanaryOptions Parse(IReadOnlyList<string> args)
    {
        var port = ParseInt(GetOption(args, "--port") ?? "8766", "--port", 1, 65_535);
        var mode = GetOption(args, "--mode") ?? "webrtc";
        if (!string.Equals(mode, "webrtc", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "ownership", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "desktop-attach", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "dedicated-create", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "--mode must be 'webrtc', 'ownership', 'desktop-attach', or 'dedicated-create'.");
        }

        var codexPath = GetOption(args, "--codex-path") ?? "codex.exe";
        var codexVersion = GetOption(args, "--codex-version") ?? "unknown";
        var threadTitle = GetOption(args, "--thread-title") ?? "Codex Voice Chat";
        var threadId = GetOption(args, "--thread-id");
        var inputWav = GetOption(args, "--input-wav");
        var captureWebm = GetOption(args, "--capture-webm");

        var attestationMode = GetOption(args, "--attestation-mode") ?? "disabled";
        if (!string.Equals(attestationMode, "disabled", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(attestationMode, "observe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--attestation-mode must be 'disabled' or 'observe'.");
        }

        var openBrowser = !args.Contains("--no-open", StringComparer.OrdinalIgnoreCase);
        var auto = args.Contains("--auto", StringComparer.OrdinalIgnoreCase);
        var ephemeralThread = args.Contains("--ephemeral-thread", StringComparer.OrdinalIgnoreCase);
        if (ephemeralThread && !string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("--ephemeral-thread cannot be combined with --thread-id.");
        }

        int? desktopPid = null;
        if (GetOption(args, "--desktop-pid") is { } desktopPidRaw)
        {
            desktopPid = ParseInt(desktopPidRaw, "--desktop-pid", 1, int.MaxValue);
        }

        var desktopCommandLineBase64 = GetOption(args, "--desktop-command-line-base64");
        var desktopListeners = GetOptions(args, "--desktop-listener").ToArray();
        if (string.Equals(mode, "desktop-attach", StringComparison.OrdinalIgnoreCase) &&
            (desktopPid is null || string.IsNullOrWhiteSpace(desktopCommandLineBase64)))
        {
            throw new ArgumentException(
                "Desktop attach mode requires --desktop-pid and --desktop-command-line-base64.");
        }

        return new CanaryOptions(
            port,
            mode.ToLowerInvariant(),
            codexPath,
            codexVersion,
            threadTitle,
            threadId,
            inputWav,
            captureWebm,
            attestationMode.ToLowerInvariant(),
            ephemeralThread,
            openBrowser,
            auto,
            desktopPid,
            desktopCommandLineBase64,
            desktopListeners);
    }

    private static string? GetOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static IEnumerable<string> GetOptions(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                yield return args[index + 1];
            }
        }
    }

    private static int ParseInt(string raw, string name, int minimum, int maximum)
    {
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
        {
            throw new ArgumentException($"{name} must be an integer between {minimum} and {maximum}.");
        }

        return value;
    }
}
