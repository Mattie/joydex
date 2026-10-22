namespace Joydex.Secrets;

/// <summary>Value-free explanation of a failed check, suitable for replies and diagnostics.</summary>
public sealed record SecretsFailure(
    string Code,
    string Stage,
    string Message,
    string LaunchState = "not_started",
    long? ExpectedPolicyEpoch = null,
    long? CurrentPolicyEpoch = null,
    long? ExpectedOperatingModeEpoch = null,
    long? CurrentOperatingModeEpoch = null,
    long? ExpectedConfigurationEpoch = null,
    long? CurrentConfigurationEpoch = null)
{
    public static SecretsFailure ConsentUnavailable { get; } = new(
        "consent_unavailable", "consent",
        "Fresh approval is required, but the Windows session cannot show a consent prompt. Saved approvals remain valid.");

    /// <summary>Classifies unexpected exceptions without exposing their messages or payloads.</summary>
    public static SecretsFailure FromException(Exception exception) => exception switch
    {
        SecretsRequestException known => known.Failure,
        UnauthorizedAccessException => new("identity_unverified", "authentication", "The local requester or project could not be authenticated."),
        SecretsProviderUnavailableException => new("provider_unavailable", "provider", "The configured secret provider is unavailable."),
        System.ComponentModel.Win32Exception => new("launch_failed", "launch", "Windows could not start the approved process."),
        InvalidDataException or System.Text.Json.JsonException or ArgumentException => new("invalid_request", "validation", "The Secrets request is invalid."),
        IOException => new("io_failure", "transport", "The Secrets operation encountered an input/output failure."),
        OperationCanceledException => new("execution_cancelled", "execution", "The Secrets execution was cancelled."),
        _ => new("internal_error", "internal", "The Secrets broker encountered an internal error; inspect this request's diagnostics."),
    };
}

/// <summary>An explicit failed broker check whose message is safe to expose to the requester.</summary>
public sealed class SecretsRequestException(SecretsFailure failure) : InvalidOperationException(failure.Message)
{
    public SecretsFailure Failure { get; } = failure;
    internal bool DiagnosticRecorded { get; init; }
}
