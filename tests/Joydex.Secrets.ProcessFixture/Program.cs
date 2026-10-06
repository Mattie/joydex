using Joydex.Secrets;

// Headless synthetic owner used to test abrupt owner termination in a separate process.
var root = Path.GetFullPath(args[0]);
var source = Path.Combine(root, "synthetic.env");
File.WriteAllText(source, "TOKEN=synthetic-fixture-value\n");
using var values = new ExactEnvSecretProvider(source, [new("test", "TOKEN", "fixture", 1)]).Fetch(["test"]);
var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
var script = "$p=Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 120'; "
    + $"[IO.File]::WriteAllText('{Path.Combine(root, "tree.txt")}', [string]$PID + ',' + [string]$p.Id); Start-Sleep -Seconds 120";
var operation = ExecOperationCanonicalizer.Resolve(new("fixture", "fixture", root, "fixture", root, 1), ["test"],
    new(shell, ["-NoProfile", "-NonInteractive", "-Command", script], root,
        new Dictionary<string, string> { ["TOKEN"] = "test" }, [], SecretOutputDisclosure.Summary));
var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
    .ToDictionary(p => (string)p.Key, p => (string)p.Value!, StringComparer.OrdinalIgnoreCase);
await new SecretsExecLauncher(new(Path.Combine(root, "audit.jsonl")))
    .RunAsync(Guid.NewGuid(), "fixture", "fixture", "fixture", ["test"], operation, values, env, CancellationToken.None);
