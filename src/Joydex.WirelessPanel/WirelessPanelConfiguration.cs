using System.Text.Json.Serialization;

namespace Joydex.WirelessPanel;

/// <summary>
/// Holds the validated connection settings Joydex needs for the attended ESPHome panel.
/// </summary>
public sealed class WirelessPanelConfiguration
{
    private const int MaximumEndpointLength = 2048;
    private const int MaximumUsernameLength = 256;
    private const int MaximumPasswordLength = 4096;

    private WirelessPanelConfiguration(
        Uri endpoint,
        string username,
        string password,
        bool enabled)
    {
        Endpoint = endpoint;
        Username = username;
        Password = password;
        Enabled = enabled;
    }

    /// <summary>Gets the absolute HTTP base address of the ESPHome panel.</summary>
    public Uri Endpoint { get; }

    /// <summary>Gets the ESPHome Web Server Digest username.</summary>
    public string Username { get; }

    /// <summary>
    /// Gets the ESPHome Web Server Digest password after DPAPI decryption.
    /// The property is excluded from incidental JSON serialization.
    /// </summary>
    [JsonIgnore]
    public string Password { get; }

    /// <summary>Gets whether Joydex should connect to the configured panel.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// Parses and validates settings for ESPHome's attended, HTTP-only direct connection.
    /// </summary>
    /// <param name="endpoint">An absolute HTTP endpoint without credentials, query, or fragment.</param>
    /// <param name="username">The nonempty Digest username.</param>
    /// <param name="password">The nonempty Digest password.</param>
    /// <param name="enabled">Whether Joydex should connect to the panel.</param>
    /// <returns>An immutable, validated configuration.</returns>
    /// <exception cref="ArgumentException">A setting is absent or outside the supported boundary.</exception>
    public static WirelessPanelConfiguration Create(
        string endpoint,
        string username,
        string password,
        bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        endpoint = endpoint.Trim();
        username = username.Trim();

        if (endpoint.Length > MaximumEndpointLength)
        {
            throw new ArgumentException(
                $"Panel endpoint must be {MaximumEndpointLength} characters or fewer.",
                nameof(endpoint));
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new ArgumentException("Panel endpoint must be an absolute URI.", nameof(endpoint));
        }

        if (!string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Attended ESPHome direct mode requires an http:// endpoint.",
                nameof(endpoint));
        }

        if (string.IsNullOrWhiteSpace(endpointUri.Host))
        {
            throw new ArgumentException("Panel endpoint must include a host.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpointUri.UserInfo)
            || endpointUri.GetLeftPart(UriPartial.Authority).Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Panel endpoint must not contain embedded credentials.",
                nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpointUri.Query))
        {
            throw new ArgumentException("Panel endpoint must not contain a query.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpointUri.Fragment))
        {
            throw new ArgumentException("Panel endpoint must not contain a fragment.", nameof(endpoint));
        }

        if (endpointUri.Port <= 0 || endpointUri.Port > 65_535)
        {
            throw new ArgumentException(
                "Panel endpoint port must be between 1 and 65535.",
                nameof(endpoint));
        }

        if (username.Length > MaximumUsernameLength)
        {
            throw new ArgumentException(
                $"Panel username must be {MaximumUsernameLength} characters or fewer.",
                nameof(username));
        }

        if (username.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Panel username must not contain control characters.",
                nameof(username));
        }

        if (password.Length > MaximumPasswordLength)
        {
            throw new ArgumentException(
                $"Panel password must be {MaximumPasswordLength} characters or fewer.",
                nameof(password));
        }

        if (password.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Panel password must not contain control characters.",
                nameof(password));
        }

        return new WirelessPanelConfiguration(endpointUri, username, password, enabled);
    }

    /// <summary>
    /// Returns a diagnostic description that deliberately excludes the password.
    /// </summary>
    public override string ToString() =>
        $"Wireless panel {Endpoint.AbsoluteUri} (username: {Username}, enabled: {Enabled})";
}
