namespace Joydex.Virpil;

public sealed record JoydexLedRecoveryDocument(
    int Version,
    string SessionToken,
    string Backend,
    byte[]? ThrottleFrame = null,
    byte[]? AlphaFrame = null,
    bool ResetAlpha = false)
{
    public const int CurrentVersion = 1;
    public const string LinkToolBackend = "linkTool";
    public const string DirectHidBackend = "directHid";
}
