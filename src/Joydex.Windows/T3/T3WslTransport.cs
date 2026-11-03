using System.Diagnostics;
using System.Text.Json;
using Joydex.Core.T3;

namespace Joydex.Windows.T3;

/// <summary>Runs the existing Linux metadata projector in an already-running WSL distribution.</summary>
internal sealed class T3WslTransport : IT3SshTransport
{
    public async Task<string> ExecuteAsync(string host, string script, CancellationToken cancellationToken)
    {
        if (!await IsRunningAsync(host, cancellationToken).ConfigureAwait(false))
            throw new T3TransportException("WslUnavailable", "The configured WSL distribution is not already running.");
        return await new T3SshTransport(CreateStartInfo, TimeSpan.FromSeconds(35))
            .ExecuteAsync(host, script, cancellationToken).ConfigureAwait(false);
    }

    internal static ProcessStartInfo CreateStartInfo(string distro)
    {
        if (!T3SshTransport.IsValidHost(distro)) throw new ArgumentException("Unsupported WSL distribution name.");
        var start = T3SshTransport.CreateStartInfo("metadata");
        start.FileName = "wsl.exe";
        start.ArgumentList.Clear();
        foreach (var value in new[] { "-d", distro, "--exec", "python3", "-" }) start.ArgumentList.Add(value);
        return start;
    }

    private static async Task<bool> IsRunningAsync(string distro, CancellationToken cancellationToken)
    {
        if (!T3SshTransport.IsValidHost(distro)) return false;
        var transport = new T3SshTransport(_ =>
        {
            var start = CreateStartInfo("metadata"); start.ArgumentList.Clear();
            foreach (var argument in new[] { "--list", "--running", "--quiet" }) start.ArgumentList.Add(argument);
            return start;
        }, TimeSpan.FromSeconds(5));
        var running = await transport.ExecuteAsync("metadata", "", cancellationToken).ConfigureAwait(false);
        return running.Replace("\0", "").TrimStart('\uFEFF').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(name => name.Trim().Equals(distro, StringComparison.OrdinalIgnoreCase));
    }

    internal static async Task<T3DesktopEnvironment> DiscoverAsync(T3DesktopEnvironment configured, CancellationToken cancellationToken)
    {
        if (!T3SshTransport.IsValidHost(configured.WslDistro))
            return configured with { UnsupportedReason = "The configured WSL route needs an explicit supported distribution name before its native identity can be verified." };
        try
        {
            var transport = new T3WslTransport();
            var raw = await transport.ExecuteAsync(configured.WslDistro!, """
                import json,pathlib,os
                root=pathlib.Path.home()/'.t3/userdata'
                identity=root/'environment-id'
                runtime=root/'server-runtime.json'
                if not identity.is_file() or identity.stat().st_size>1024 or not runtime.is_file() or runtime.stat().st_size>65536:
                    raise ValueError('runtime unavailable')
                value=json.loads(runtime.read_text())
                pid=value.get('pid')
                if not isinstance(pid,int) or pid<=0: raise ValueError('runtime unavailable')
                os.kill(pid,0)
                if not (pathlib.Path.home()/'.local/bin/t3').is_file(): raise ValueError('cli unavailable')
                print(json.dumps({'environmentId':identity.read_text().strip(),'serverVersion':value.get('serverVersion')}))
                """, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(raw);
            var id = document.RootElement.GetProperty("environmentId").GetString();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || id.Any(char.IsControl)) throw new InvalidDataException();
            return configured with { EnvironmentId = id, DisplayName = "T3 WSL (" + configured.WslDistro + ")", UnsupportedReason = null };
        }
        catch (OperationCanceledException) { throw; }
        catch { return configured with { UnsupportedReason = "The configured WSL route has no verified running T3 environment or usable metadata CLI; no distribution or server was started." }; }
    }
}
