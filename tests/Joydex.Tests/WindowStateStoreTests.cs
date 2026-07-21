using Joydex.App;

namespace Joydex.Tests;

public sealed class WindowStateStoreTests
{
    [Fact]
    public void ConfigurationStateLoadsLegacyFilesWithoutDpi()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllText(path, """{"width":1200,"height":800,"maximized":false}""");

            var state = ConfigurationWindowStateStore.Load(path);

            Assert.NotNull(state);
            Assert.Equal(0, state.Dpi);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ButtonMapStateRoundTripsItsSourceDpi()
    {
        var path = TemporaryPath();
        try
        {
            ButtonMapWindowStateStore.Save(
                path,
                new ButtonMapWindowState(100, 200, 1200, 800, Maximized: false, Dpi: 144));

            var state = ButtonMapWindowStateStore.Load(path);

            Assert.NotNull(state);
            Assert.Equal(144, state.Dpi);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TemporaryPath() => Path.Combine(
        Path.GetTempPath(),
        $"joydex-window-state-{Guid.NewGuid():N}.json");
}
