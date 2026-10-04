namespace Joydex.App;

/// <summary>Loads the multiresolution icon used by the Configuration window.</summary>
internal static class ConfigurationIconFactory
{
    private const string ResourceName =
        "Joydex.App.Assets.Icons.joydex-configuration.ico";

    public static Icon Create()
    {
        using var stream = typeof(ConfigurationIconFactory).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded Configuration icon '{ResourceName}' is unavailable.");
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }
}
