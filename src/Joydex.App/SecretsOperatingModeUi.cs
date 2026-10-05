using Joydex.Secrets;

namespace Joydex.App;

internal static class SecretsOperatingModeUi
{
    public static bool ConfirmAutoAllow(IWin32Window? owner, DateTimeOffset now)
    {
        var until = now.AddHours(24).ToLocalTime().ToString("g");
        var message = "Auto-allow every Secrets request for 24 hours?\n\n"
            + $"Until {until}, Joydex will let any local agent use any configured secret "
            + "for its current project without showing an approval popup.\n\n"
            + "This includes requests you previously chose to deny. Joydex will still verify "
            + "the agent, project, request, and secret source, and will record sanitized activity.\n\n"
            + "You can turn this off at any time.\n\nAre you sure?";
        return MessageBox.Show(
            owner,
            message,
            "Auto-allow Secrets requests?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    public static string MenuTitle(SecretsOperatingModeSnapshot mode) => mode.Mode switch
    {
        SecretsOperatingMode.AutoAllow24Hours => "Secrets — ⚠ Auto-allowing",
        SecretsOperatingMode.DenyAll => "Secrets — Denying all",
        _ => "Secrets — Asking for approval",
    };

    public static string Status(SecretsOperatingModeSnapshot mode) => mode.Mode switch
    {
        SecretsOperatingMode.AutoAllow24Hours =>
            $"Auto-allowing until {mode.ExpiresAt!.Value.ToLocalTime():g}",
        SecretsOperatingMode.DenyAll => "Denying all requests until you change this",
        _ => "Asking when a request is not covered by a remembered decision",
    };

    public static string Metric(string label, long total, long recent) =>
        $"{label}: {total:N0} total · {recent:N0} past 24h";

    public static string Metrics(SecretsMetricsSnapshot metrics) => string.Join(
        Environment.NewLine,
        Metric("Requests", metrics.RequestsTotal, metrics.Requests24Hours),
        Metric("Secrets used", metrics.ExecutionsTotal, metrics.Executions24Hours),
        Metric("Approved", metrics.ApprovedTotal, metrics.Approved24Hours),
        Metric("Denied", metrics.DeniedTotal, metrics.Denied24Hours));
}
