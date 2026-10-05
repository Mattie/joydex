using System.Diagnostics;
using System.Security.Cryptography;
using Joydex.Secrets;

namespace Joydex.SecretsHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 6 && args[0] == "--task-host")
        {
            try { return SecretsDetachedTask.RunHostAsync(args[1], args[2], int.Parse(args[3]), long.Parse(args[4]), args[5]).GetAwaiter().GetResult(); }
            catch { return 2; } // Headless mode must never show a native error dialog.
        }
        ApplicationConfiguration.Initialize();
        var brokerMode = args.Contains("--broker", StringComparer.Ordinal);
        try
        {
            if (args.Contains("--synthetic-canary", StringComparer.Ordinal))
            {
                return RunSyntheticCanary();
            }
            if (TryArgument(args, "--broker", out var dataRoot))
            {
                return RunBroker(dataRoot, args);
            }
            return 2;
        }
        catch (Exception exception)
        {
            if (!brokerMode)
            {
                MessageBox.Show(
                    "Joydex Secrets stopped before it was ready.\n\n" + exception.Message,
                    "Joydex Secrets Manager",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            return 1;
        }
    }

    private static int RunBroker(string dataRoot, IReadOnlyList<string> args)
    {
        var endpoint = SecretsBrokerEndpoint.Create(dataRoot);
        using var ownership = new Mutex(initiallyOwned: true, endpoint.MutexName, out var ownsBroker);
        if (!ownsBroker) return 0;
        using var ready = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            endpoint.ReadyEventName);
        ready.Reset();

        Process? parent = null;
        if (TryArgument(args, "--parent-pid", out var parentIdText)
            || TryArgument(args, "--parent-start-ticks", out _))
        {
            if (!int.TryParse(parentIdText, out var parentId)
                || !TryArgument(args, "--parent-start-ticks", out var startTicksText)
                || !long.TryParse(startTicksText, out var startTicks))
            {
                throw new InvalidDataException("The Secrets broker parent identity is invalid.");
            }
            parent = Process.GetProcessById(parentId);
            if (parent.StartTime.ToUniversalTime().Ticks != startTicks)
            {
                parent.Dispose();
                throw new InvalidDataException("The Secrets broker parent process changed before startup.");
            }
        }

        using (parent)
        using (var context = new SecretsBrokerApplicationContext(endpoint, parent))
        {
            ready.Set();
            Application.Run(context);
        }
        return 0;
    }

    private static int RunSyntheticCanary()
    {
        var root = Path.Combine(Path.GetTempPath(), "joydex-secrets-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var credential = Array.Empty<byte>();
        try
        {
            var secret = "canary-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var envPath = Path.Combine(root, ".env");
            var auditPath = Path.Combine(root, "audit.jsonl");
            File.WriteAllText(envPath, "TOKEN=" + secret + Environment.NewLine);
            var project = new SecretsProjectIdentity(
                "Joydex", "joydex-secrets-ui-canary", root,
                "preview", root, 1,
                WorktreeReference: "preview");
            var clients = new NamedClientRegistry(Path.Combine(root, "clients.json"));
            using (var enrollment = clients.Enroll(
                       "live-canary-helper", "Live canary helper", [project],
                       [SecretDeliveryMode.ExecInject]))
            {
                credential = enrollment.CopyCredential();
            }
            var provider = new ExactEnvSecretProvider(
                envPath, [new("synthetic-token", "TOKEN", "live-canary-token", 1)]);
            var policy = new SecretsPolicyStore(Path.Combine(root, "policy.json"));
            var audit = new SecretsAuditJournal(auditPath);
            var aliases = new[]
            {
                new SecretsAliasIdentity("synthetic-token", "env", "live-canary-token", 1),
            };
            var broker = new SecretsBrokerCore(clients, policy, audit, provider, aliases);
            var submission = new SecretsRequestSubmission(
                "native-prompt-canary-1",
                "live-canary-helper",
                "Joydex",
                new ExecOperationProposal(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    ["/d", "/c", "if defined SYNTHETIC_TOKEN (echo %SYNTHETIC_TOKEN% & exit /b 0) else (exit /b 7)"],
                    root,
                    new Dictionary<string, string> { ["SYNTHETIC_TOKEN"] = "synthetic-token" },
                    [],
                    SecretOutputDisclosure.Summary),
                "Confirm native consent and one approved synthetic secret injection.");
            var pendingResponse = broker.Submit(submission, credential);
            var pending = broker.PendingRequests().Single();
            using var toast = new SecretsConsentToast(pending);
            Application.Run(toast);
            if (toast.Decision is null)
            {
                ShowResult(
                    toast.TimedOut
                        ? "REQUEST TIMED OUT\n\nNothing ran. Start the canary again to make a new request."
                        : "CANARY CANCELLED\n\nNo secret was delivered.",
                    MessageBoxIcon.Information);
                return 0;
            }

            var decision = broker.Decide(
                pendingResponse.AttemptId,
                pending.DisplayChallenge,
                toast.Decision.Choice,
                requireOperation: !toast.Decision.ApplyToAllCommands);
            if (decision.Status != SecretsRequestStatus.Allowed)
            {
                var explanation = decision.Status == SecretsRequestStatus.Expired
                    ? "This request timed out before you answered. Nothing ran. Start the canary again to make a new request."
                    : "Nothing ran and no secret was delivered.";
                ShowResult(
                    $"CANARY STOPPED\n\n{explanation}",
                    MessageBoxIcon.Information);
                return 0;
            }

            var callerEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            {
                if (variable.Key is string name && !string.IsNullOrEmpty(name) && !name.Contains('='))
                    callerEnvironment[name] = variable.Value?.ToString() ?? string.Empty;
            }
            var result = broker.RunAsync(
                    "live-canary-helper", credential, "Joydex",
                    decision.RequestId, decision.Reservation!, callerEnvironment, CancellationToken.None)
                .GetAwaiter().GetResult();
            var replayBlocked = false;
            try
            {
                _ = broker.RunAsync(
                        "live-canary-helper", credential, "Joydex",
                        decision.RequestId, decision.Reservation!, callerEnvironment, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidOperationException) { replayBlocked = true; }
            var reopened = new SecretsBrokerCore(clients, policy, audit, provider, aliases);
            var durable = reopened.Submit(submission, credential);
            var auditClean = !File.ReadAllText(auditPath).Contains(secret, StringComparison.Ordinal);
            var passed = result.ExitCode == 0
                && result.StandardOutput.Trim() == "[REDACTED]"
                && replayBlocked
                && durable.Status == SecretsRequestStatus.Completed
                && auditClean;
            ShowResult(
                $"{(passed ? "CANARY PASSED" : "CANARY FAILED")}\n\n"
                + $"Decision: {toast.Decision.Choice}\n"
                + $"Child exit code: {result.ExitCode}\n"
                + $"Returned output: {result.StandardOutput.Trim()}\n"
                + $"Reservation replay blocked: {replayBlocked}\n"
                + $"Restart replay status: {durable.Status}\n"
                + $"Secret absent from audit: {auditClean}",
                passed ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            return passed ? 0 : 1;
        }
        finally
        {
            if (credential.Length > 0) CryptographicOperations.ZeroMemory(credential);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void ShowResult(string message, MessageBoxIcon icon) => MessageBox.Show(
        message,
        "Joydex Secrets canary",
        MessageBoxButtons.OK,
        icon);

    private static bool TryArgument(
        IReadOnlyList<string> args,
        string name,
        out string value)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            value = args[index + 1];
            return !string.IsNullOrWhiteSpace(value);
        }
        value = string.Empty;
        return false;
    }
}
