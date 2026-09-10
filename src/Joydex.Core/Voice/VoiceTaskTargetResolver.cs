namespace Joydex.Core.Voice;

public enum VoiceTaskTargetResolutionKind
{
    Found,
    Missing,
    Ambiguous,
}

public sealed record VoiceTaskTargetResolution(
    VoiceTaskTargetResolutionKind Kind,
    DesktopTaskSummary? Target,
    IReadOnlyList<DesktopTaskSummary> Candidates)
{
    public static VoiceTaskTargetResolution Resolve(
        IReadOnlyList<DesktopTaskSummary> tasks,
        string? spokenTarget,
        string? savedTaskId,
        string? savedHostId)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (string.IsNullOrWhiteSpace(spokenTarget))
        {
            var saved = tasks.FirstOrDefault(task =>
                string.Equals(task.Id, savedTaskId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(task.HostId, savedHostId, StringComparison.OrdinalIgnoreCase));
            return saved is null
                ? new(VoiceTaskTargetResolutionKind.Missing, null, [])
                : new(VoiceTaskTargetResolutionKind.Found, saved, [saved]);
        }

        var requested = spokenTarget.Trim();
        foreach (var predicate in new Func<DesktopTaskSummary, bool>[]
        {
            task => string.Equals(task.Title, requested, StringComparison.OrdinalIgnoreCase),
            task => task.Title.StartsWith(requested, StringComparison.OrdinalIgnoreCase),
            task => task.Title.Contains(requested, StringComparison.OrdinalIgnoreCase),
        })
        {
            var matches = tasks.Where(predicate).ToArray();
            if (matches.Length == 1)
            {
                return new(VoiceTaskTargetResolutionKind.Found, matches[0], matches);
            }
            if (matches.Length > 1)
            {
                return new(VoiceTaskTargetResolutionKind.Ambiguous, null, matches);
            }
        }

        return new(VoiceTaskTargetResolutionKind.Missing, null, []);
    }
}
