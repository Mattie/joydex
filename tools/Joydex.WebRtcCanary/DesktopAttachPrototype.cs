using System.Text;
using System.Text.RegularExpressions;

namespace Joydex.WebRtcCanary;

internal sealed record DesktopAppServerObservation(
    int ProcessId,
    string CommandLine,
    IReadOnlyList<string> TcpListeners)
{
    public static DesktopAppServerObservation FromOptions(
        int? processId,
        string? commandLineBase64,
        IReadOnlyList<string> listeners)
    {
        if (processId is null || string.IsNullOrWhiteSpace(commandLineBase64))
        {
            throw new ArgumentException("Desktop App Server process observation is incomplete.");
        }

        string commandLine;
        try
        {
            commandLine = Encoding.UTF8.GetString(Convert.FromBase64String(commandLineBase64));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Desktop command line is not valid base64.", exception);
        }

        return new DesktopAppServerObservation(processId.Value, commandLine, listeners);
    }
}

internal sealed record DesktopAttachAssessment(
    string Transport,
    string Status,
    string Reason,
    string? CandidateEndpoint,
    string? VerifiedEndpoint);

/// <summary>
/// Pure assessment for the DESKTOPATTACH question. It accepts observed process facts, rejects
/// parent-owned stdio, and keeps any declared joinable transport at candidate status until a
/// separate handshake can verify it.
/// </summary>
internal static partial class DesktopAttachPrototype
{
    public static DesktopAttachAssessment Assess(DesktopAppServerObservation observation)
    {
        var listenUrl = ReadListenUrl(observation.CommandLine);
        if (string.Equals(listenUrl, "stdio://", StringComparison.OrdinalIgnoreCase))
        {
            if (observation.TcpListeners.Count > 0)
            {
                return new DesktopAttachAssessment(
                    "stdio://",
                    "INCONCLUSIVE — UNEXPLAINED LISTENER",
                    "Desktop uses parent-owned stdio, while the observed process also has a TCP listener that this prototype has not identified or initialized.",
                    null,
                    null);
            }

            return new DesktopAttachAssessment(
                "stdio://",
                "NO SUPPORTED ATTACH ENDPOINT",
                "Desktop owns both stdio streams. A second process cannot join that existing transport, and the Desktop App Server has no observed TCP listener.",
                null,
                null);
        }

        if (listenUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
            return new DesktopAttachAssessment(
                listenUrl,
                "EXPERIMENTAL ENDPOINT OBSERVED",
                "Codex accepts WebSocket listeners, while its App Server documentation marks that transport experimental and unsupported for production workloads.",
                listenUrl,
                null);
        }

        if (listenUrl.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
        {
            return new DesktopAttachAssessment(
                listenUrl,
                "SUPPORTED CANDIDATE REQUIRES INITIALIZE",
                "Desktop declared the documented local control-plane transport. The endpoint remains a candidate until a client connects, completes the WebSocket upgrade, initializes App Server, and verifies that the observed process owns it.",
                listenUrl,
                null);
        }

        return new DesktopAttachAssessment(
            listenUrl,
            "NO SUPPORTED ATTACH ENDPOINT",
            "The observed Desktop App Server transport does not expose a documented external connection path.",
            null,
            null);
    }

    public static void Render(DesktopAppServerObservation observation)
    {
        var assessment = Assess(observation);
        Console.WriteLine("DESKTOPATTACH — existing Desktop App Server discovery prototype");
        Console.WriteLine();
        Console.WriteLine($"Desktop App Server PID: {observation.ProcessId}");
        Console.WriteLine($"Observed command:       {observation.CommandLine}");
        Console.WriteLine($"Effective transport:    {assessment.Transport}");
        Console.WriteLine($"TCP listeners:          {(observation.TcpListeners.Count == 0 ? "none" : string.Join(", ", observation.TcpListeners))}");
        Console.WriteLine($"Status:                 {assessment.Status}");
        Console.WriteLine($"Reason:                 {assessment.Reason}");
        Console.WriteLine($"Candidate endpoint:     {assessment.CandidateEndpoint ?? "none"}");
        Console.WriteLine($"Verified endpoint:      {assessment.VerifiedEndpoint ?? "none"}");
    }

    private static string ReadListenUrl(string commandLine)
    {
        if (StdioOptionRegex().IsMatch(commandLine))
        {
            return "stdio://";
        }

        var match = ListenOptionRegex().Match(commandLine);
        if (match.Success)
        {
            return match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["plain"].Value;
        }

        return "stdio://";
    }

    [GeneratedRegex("(?:^|\\s)--stdio(?:\\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex StdioOptionRegex();

    [GeneratedRegex("(?:^|\\s)--listen\\s+(?:\"(?<quoted>[^\"]+)\"|(?<plain>\\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex ListenOptionRegex();
}
