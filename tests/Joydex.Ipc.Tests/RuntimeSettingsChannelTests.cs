using System.Text;
using System.Text.Json;
using Joydex.Contracts;

namespace Joydex.Ipc.Tests;

public sealed class RuntimeSettingsChannelTests
{
    [Fact]
    public async Task BootstrapRoundTripsExactCustomConfigurationWithoutPrintingTicket()
    {
        var root = Path.Combine(Path.GetTempPath(), "joydex-settings-channel", Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(root, "custom.json");
        var endpoint = RuntimeIpcEndpoint.CreateSynthetic(
            root,
            configurationPath,
            $"joydex-settings-{Guid.NewGuid():N}");
        var message = RuntimeSettingsChannel.CreateBootstrap(
            endpoint,
            root,
            configurationPath,
            "secret-ticket");
        await using var channel = new MemoryStream();

        await RuntimeSettingsChannel.WriteAsync(channel, message);
        channel.Position = 0;
        var decoded = await RuntimeSettingsChannel.ReadAsync(channel);

        Assert.Equal(message, decoded);
        var reconstructed = RuntimeSettingsChannel.GetBootstrapEndpoint(decoded!);
        Assert.Equal(endpoint.PipeName, reconstructed.PipeName);
        Assert.Equal(endpoint.DataRootId, reconstructed.DataRootId);
        Assert.DoesNotContain("secret-ticket", message.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReaderRejectsOversizeLineBeforeJsonDeserialization()
    {
        var bytes = Encoding.UTF8.GetBytes(
            new string('x', RuntimeSettingsChannel.MaximumMessageBytes + 1) + "\n");
        await using var channel = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<RuntimeIpcMessageTooLargeException>(async () =>
            await RuntimeSettingsChannel.ReadAsync(channel));

        Assert.Equal(RuntimeSettingsChannel.MaximumMessageBytes + 1, exception.ActualBytes);
    }

    [Fact]
    public async Task WriterRejectsOversizeBootstrapBeforeWritingTicket()
    {
        var root = Path.Combine(Path.GetTempPath(), "joydex-settings-channel", Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(root, "custom.json");
        var endpoint = RuntimeIpcEndpoint.CreateSynthetic(root, configurationPath, "bounded-channel");
        var message = RuntimeSettingsChannel.CreateBootstrap(
            endpoint,
            root,
            configurationPath,
            new string('s', RuntimeSettingsChannel.MaximumMessageBytes));
        await using var channel = new MemoryStream();

        await Assert.ThrowsAsync<RuntimeIpcMessageTooLargeException>(async () =>
            await RuntimeSettingsChannel.WriteAsync(channel, message));

        Assert.Equal(0, channel.Length);
    }

    [Fact]
    public async Task ReaderRejectsUnknownFieldsAndInvalidActivationPayload()
    {
        await AssertInvalidAsync("{\"version\":1,\"kind\":2,\"extra\":true}\n");
        await AssertInvalidAsync("{\"version\":1,\"kind\":2,\"launchTicket\":\"replacement\"}\n");
        await AssertInvalidAsync("{\"version\":1,\"kind\":3}\n");
        await AssertInvalidAsync("{\"version\":1,\"kind\":3,\"pipeName\":\"replacement\",\"launchTicket\":\"ticket\"}\n");
        await AssertInvalidAsync("{\"version\":2,\"kind\":2}\n");
        await AssertInvalidAsync("{\"version\":1,\"kind\":999}\n");
    }

    [Fact]
    public void ProductionBootstrapRejectsATransmittedPipeMismatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "joydex-settings-channel", Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(root, "custom.json");
        var message = new RuntimeSettingsChannelMessage(
            RuntimeSettingsChannel.Version,
            RuntimeSettingsChannelMessageKind.Bootstrap,
            RuntimeInstanceKind.Production,
            root,
            configurationPath,
            "another-runtime",
            "secret-ticket");

        Assert.Throws<InvalidDataException>(() =>
            RuntimeSettingsChannel.GetBootstrapEndpoint(message));
    }

    private static async Task AssertInvalidAsync(string line)
    {
        await using var channel = new MemoryStream(Encoding.UTF8.GetBytes(line));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await RuntimeSettingsChannel.ReadAsync(channel));
    }
}
