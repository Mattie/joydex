namespace Joydex.Core.T3;

public sealed record T3DesktopDiscoveryResult(bool Success, bool IsComplete,
    IReadOnlyList<T3DesktopEnvironment> Environments, string? Error = null);

/// <summary>Metadata only. EnvironmentId is native; ConnectionId identifies a route, never a task source.</summary>
public sealed record T3DesktopEnvironment(string EnvironmentId, string DisplayName, bool IsLocal,
    string? SshHost, bool Enabled, string? UnsupportedReason = null, string? ConnectionId = null,
    string TransportKind = "local", string? WslDistro = null);
