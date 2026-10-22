namespace Joydex.Secrets;

/// <summary>Whether the current Windows session can present fresh local consent.</summary>
public enum SecretsConsentAvailability
{
    Available,
    Locked,
    Disconnected,
    LoggedOff,
    Unknown,
}
