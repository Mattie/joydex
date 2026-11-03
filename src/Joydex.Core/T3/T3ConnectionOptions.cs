namespace Joydex.Core.T3;

public sealed record T3ConnectionOptions
{
    public bool Enabled { get; init; }
    public bool FollowDesktop { get; init; }
    public string SshHost { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public string DisplayName { get; init; } = "";

    public T3ConnectionOptions Normalize()
    {
        var result = this with
        {
            SshHost = (SshHost ?? "").Trim(),
            EnvironmentId = (EnvironmentId ?? "").Trim(),
            DisplayName = (DisplayName ?? "").Trim(),
        };
        if (result.SshHost.Length > 128 || result.EnvironmentId.Length > 512 || result.DisplayName.Length > 240
            || new[] { result.SshHost, result.EnvironmentId, result.DisplayName }.Any(value => value.Any(char.IsControl)))
            throw new ArgumentException("T3 connection fields exceed their supported bounds or contain control characters.");
        if (result.Enabled && !result.FollowDesktop && (result.SshHost.Length == 0 || result.EnvironmentId.Length == 0))
            throw new ArgumentException("An enabled T3 connection requires an SSH alias and a tested environment identity.");
        return result;
    }
}
