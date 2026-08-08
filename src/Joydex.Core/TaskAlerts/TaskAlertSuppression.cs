using System.Security.Cryptography;
using System.Text;

namespace Joydex.Core.TaskAlerts;

public static class TaskAlertSuppression
{
    public const int MaximumRules = 100;
    public const int MaximumTaskIdLength = 512;
    public const int MaximumWorkspaceLength = 4096;

    public static IEqualityComparer<TaskAlertSuppressionRule> RuleComparer { get; } =
        new SuppressionRuleComparer();

    public static TaskAlertSuppressionRule? Normalize(TaskAlertSuppressionRule? rule)
    {
        if (rule is null || !Enum.IsDefined(rule.Scope))
        {
            return null;
        }

        var value = rule.Scope switch
        {
            TaskAlertSuppressionScope.Task => NormalizeTaskId(rule.Value),
            TaskAlertSuppressionScope.Workspace => NormalizeWorkspace(rule.Value),
            _ => null,
        };
        return value is null ? null : rule with { Value = value };
    }

    public static string? NormalizeTaskId(string? sessionId)
    {
        var value = sessionId?.Trim();
        return string.IsNullOrWhiteSpace(value) || value.Length > MaximumTaskIdLength
            ? null
            : value;
    }

    public static string? NormalizeWorkspace(string? workspace)
    {
        var value = workspace?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumWorkspaceLength)
        {
            return null;
        }

        try
        {
            value = Path.GetFullPath(value);
            var root = Path.GetPathRoot(value);
            if (!string.Equals(value, root, WorkspaceComparison))
            {
                value = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return value;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static string? CreateWorkspaceKey(string? workspace)
    {
        var normalized = NormalizeWorkspace(workspace);
        if (normalized is null)
        {
            return null;
        }

        var canonical = OperatingSystem.IsWindows()
            ? normalized.ToUpperInvariant()
            : normalized;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool IsWorkspaceKey(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9'
            or >= 'A' and <= 'F'
            or >= 'a' and <= 'f');

    public static bool Matches(
        TaskAlertSuppressionRule rule,
        string sessionId,
        string? workspaceKey) =>
        rule.Scope switch
        {
            TaskAlertSuppressionScope.Task => string.Equals(
                rule.Value,
                sessionId,
                StringComparison.Ordinal),
            TaskAlertSuppressionScope.Workspace => string.Equals(
                CreateWorkspaceKey(rule.Value),
                workspaceKey,
                StringComparison.Ordinal),
            _ => false,
        };

    private static StringComparison WorkspaceComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed class SuppressionRuleComparer : IEqualityComparer<TaskAlertSuppressionRule>
    {
        public bool Equals(TaskAlertSuppressionRule? left, TaskAlertSuppressionRule? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null || left.Scope != right.Scope)
            {
                return false;
            }

            return string.Equals(
                left.Value,
                right.Value,
                left.Scope == TaskAlertSuppressionScope.Workspace
                    ? WorkspaceComparison
                    : StringComparison.Ordinal);
        }

        public int GetHashCode(TaskAlertSuppressionRule rule) => rule.Scope switch
        {
            TaskAlertSuppressionScope.Task => HashCode.Combine(
                rule.Scope,
                StringComparer.Ordinal.GetHashCode(rule.Value)),
            TaskAlertSuppressionScope.Workspace => HashCode.Combine(
                rule.Scope,
                (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .GetHashCode(rule.Value)),
            _ => rule.GetHashCode(),
        };
    }
}
