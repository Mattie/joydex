using System.Text.Json;
using System.Text.Json.Serialization;
using Joydex.Contracts;

namespace Joydex.Ipc;

/// <summary>Identifies a host-to-Settings process control message.</summary>
public enum RuntimeSettingsChannelMessageKind
{
    Bootstrap = 1,
    Activate = 2,
    Reconnect = 3,
}

/// <summary>
/// Carries the exact runtime selection and one process-bound launch ticket over inherited stdin.
/// Bootstrap fields are absent from later activation messages.
/// </summary>
public sealed record RuntimeSettingsChannelMessage(
    int Version,
    RuntimeSettingsChannelMessageKind Kind,
    RuntimeInstanceKind? InstanceKind = null,
    string? DataRoot = null,
    string? ConfigurationPath = null,
    string? PipeName = null,
    string? LaunchTicket = null)
{
    public override string ToString() =>
        $"RuntimeSettingsChannelMessage {{ Version = {Version}, Kind = {Kind}, "
        + $"InstanceKind = {InstanceKind}, DataRoot = {DataRoot}, "
        + $"ConfigurationPath = {ConfigurationPath}, PipeName = {PipeName}, "
        + "LaunchTicket = [redacted] }";
}

/// <summary>
/// Reads and writes the bounded, newline-delimited channel inherited by one Settings child.
/// </summary>
public static class RuntimeSettingsChannel
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static RuntimeSettingsChannelMessage CreateBootstrap(
        RuntimeIpcEndpoint endpoint,
        string dataRoot,
        string configurationPath,
        string launchTicket)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var message = new RuntimeSettingsChannelMessage(
            Version,
            RuntimeSettingsChannelMessageKind.Bootstrap,
            endpoint.InstanceKind,
            dataRoot,
            configurationPath,
            endpoint.PipeName,
            launchTicket);
        _ = GetBootstrapEndpoint(message);
        return message;
    }

    public static RuntimeSettingsChannelMessage CreateActivate() =>
        new(Version, RuntimeSettingsChannelMessageKind.Activate);

    public static RuntimeSettingsChannelMessage CreateReconnect(string launchTicket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchTicket);
        return new RuntimeSettingsChannelMessage(
            Version,
            RuntimeSettingsChannelMessageKind.Reconnect,
            LaunchTicket: launchTicket);
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        RuntimeSettingsChannelMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Validate(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new RuntimeIpcMessageTooLargeException(
                payload.Length,
                MaximumMessageBytes);
        }

        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<RuntimeSettingsChannelMessage?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = new byte[MaximumMessageBytes];
        var next = new byte[1];
        var length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(next, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (length == 0)
                {
                    return null;
                }
                throw new EndOfStreamException(
                    "The Settings channel closed in the middle of a message.");
            }
            if (next[0] == (byte)'\n')
            {
                break;
            }
            if (length == MaximumMessageBytes)
            {
                throw new RuntimeIpcMessageTooLargeException(
                    length + 1,
                    MaximumMessageBytes);
            }
            payload[length++] = next[0];
        }

        if (length > 0 && payload[length - 1] == (byte)'\r')
        {
            length--;
        }
        if (length == 0)
        {
            throw new InvalidDataException("The Settings channel message was empty.");
        }

        RuntimeSettingsChannelMessage message;
        try
        {
            message = JsonSerializer.Deserialize<RuntimeSettingsChannelMessage>(
                    payload.AsSpan(0, length),
                    JsonOptions)
                ?? throw new InvalidDataException("The Settings channel message was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Settings channel message was malformed.", exception);
        }
        Validate(message);
        return message;
    }

    /// <summary>Validates a Bootstrap message and reconstructs its exact runtime endpoint.</summary>
    public static RuntimeIpcEndpoint GetBootstrapEndpoint(RuntimeSettingsChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Version != Version
            || message.Kind != RuntimeSettingsChannelMessageKind.Bootstrap
            || message.InstanceKind is null
            || string.IsNullOrWhiteSpace(message.DataRoot)
            || string.IsNullOrWhiteSpace(message.ConfigurationPath)
            || string.IsNullOrWhiteSpace(message.PipeName)
            || string.IsNullOrWhiteSpace(message.LaunchTicket))
        {
            throw new InvalidDataException("The Settings Bootstrap message is incomplete.");
        }
        if (!Enum.IsDefined(message.InstanceKind.Value))
        {
            throw new InvalidDataException("The Settings Bootstrap instance kind is invalid.");
        }

        string dataRoot;
        string configurationPath;
        try
        {
            if (!Path.IsPathFullyQualified(message.DataRoot)
                || !Path.IsPathFullyQualified(message.ConfigurationPath))
            {
                throw new InvalidDataException(
                    "The Settings Bootstrap paths must be absolute.");
            }
            dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(message.DataRoot));
            configurationPath = Path.GetFullPath(message.ConfigurationPath);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            throw new InvalidDataException(
                "The Settings Bootstrap paths are invalid.",
                exception);
        }

        var configurationRoot = Path.GetDirectoryName(configurationPath);
        if (!string.Equals(
                dataRoot,
                configurationRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The Settings Bootstrap configuration is outside its selected data root.");
        }

        RuntimeIpcEndpoint endpoint;
        try
        {
            endpoint = message.InstanceKind.Value switch
            {
                RuntimeInstanceKind.Production => RuntimeIpcEndpoint.CreateProduction(
                    dataRoot,
                    configurationPath),
                RuntimeInstanceKind.Synthetic => RuntimeIpcEndpoint.CreateSynthetic(
                    dataRoot,
                    configurationPath,
                    message.PipeName),
                _ => throw new InvalidDataException(
                    "The Settings Bootstrap instance kind is unsupported."),
            };
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException)
        {
            throw new InvalidDataException(
                "The Settings Bootstrap endpoint is invalid.",
                exception);
        }
        if (!string.Equals(endpoint.PipeName, message.PipeName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Settings Bootstrap pipe does not match the selected endpoint.");
        }
        return endpoint;
    }

    private static void Validate(RuntimeSettingsChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Version != Version)
        {
            throw new InvalidDataException(
                $"Settings channel version {message.Version} is unsupported.");
        }
        if (!Enum.IsDefined(message.Kind))
        {
            throw new InvalidDataException("The Settings channel message kind is invalid.");
        }

        switch (message.Kind)
        {
            case RuntimeSettingsChannelMessageKind.Bootstrap:
                _ = GetBootstrapEndpoint(message);
                break;
            case RuntimeSettingsChannelMessageKind.Activate:
                if (message.InstanceKind is not null
                    || message.DataRoot is not null
                    || message.ConfigurationPath is not null
                    || message.PipeName is not null
                    || message.LaunchTicket is not null)
                {
                    throw new InvalidDataException(
                        "A Settings Activate message cannot replace Bootstrap state.");
                }
                break;
            case RuntimeSettingsChannelMessageKind.Reconnect:
                if (message.InstanceKind is not null
                    || message.DataRoot is not null
                    || message.ConfigurationPath is not null
                    || message.PipeName is not null
                    || string.IsNullOrWhiteSpace(message.LaunchTicket))
                {
                    throw new InvalidDataException(
                        "A Settings Reconnect message can carry only a fresh launch ticket.");
                }
                break;
        }
    }
}
