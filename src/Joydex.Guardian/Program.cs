using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Joydex.Virpil;

return await Guardian.RunAsync(args).ConfigureAwait(false);

internal static class Guardian
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryArgument(args, "--parent", out var parentValue)
            || !int.TryParse(parentValue, out var parentId)
            || !TryArgument(args, "--clean-event", out var cleanEventName)
            || !TryArgument(args, "--restore-event", out var restoreEventName)
            || !TryArgument(args, "--port", out var portValue)
            || !int.TryParse(portValue, out var port))
        {
            return 0;
        }

        using var cleanEvent = EventWaitHandle.OpenExisting(cleanEventName);
        using var restoreEvent = EventWaitHandle.OpenExisting(restoreEventName);
        TryArgument(args, "--recovery", out var recoveryPath);
        TryArgument(args, "--token", out var sessionToken);
        Process? parent = null;
        try
        {
            parent = Process.GetProcessById(parentId);
            while (!cleanEvent.WaitOne(0))
            {
                if (parent.HasExited)
                {
                    if (restoreEvent.WaitOne(0))
                    {
                        await RecoverAsync(port, recoveryPath, sessionToken).ConfigureAwait(false);
                    }

                    return 0;
                }

                await Task.Delay(250).ConfigureAwait(false);
                parent.Refresh();
            }
        }
        catch (ArgumentException)
        {
            if (restoreEvent.WaitOne(0))
            {
                await RecoverAsync(port, recoveryPath, sessionToken).ConfigureAwait(false);
            }
        }
        finally
        {
            parent?.Dispose();
        }

        return 0;
    }

    private static async Task RecoverAsync(int port, string recoveryPath, string sessionToken)
    {
        JoydexLedRecoveryDocument? document = null;
        if (!string.IsNullOrWhiteSpace(recoveryPath) && File.Exists(recoveryPath))
        {
            try
            {
                await using var stream = File.OpenRead(recoveryPath);
                document = await JsonSerializer.DeserializeAsync(
                    stream,
                    GuardianJsonContext.Default.JoydexLedRecoveryDocument).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
            {
            }
        }

        if (document is not null
            && document.Version == JoydexLedRecoveryDocument.CurrentVersion
            && !string.IsNullOrWhiteSpace(sessionToken)
            && string.Equals(document.SessionToken, sessionToken, StringComparison.Ordinal)
            && string.Equals(document.Backend, JoydexLedRecoveryDocument.DirectHidBackend, StringComparison.Ordinal)
            && IsFrame(document.ThrottleFrame)
            && IsFrame(document.AlphaFrame))
        {
            if (VirpilWriterProcessDetector.FindConflict() is null)
            {
                RecoverDirect(document);
            }
            return;
        }

        await ClearAlertsAsync(port).ConfigureAwait(false);
    }

    private static void RecoverDirect(JoydexLedRecoveryDocument document)
    {
        try
        {
            using var throttle = new VirpilHidTransport(VirpilDevices.Throttle);
            throttle.Send(VirpilLedProtocol.BuildReport(
                VirpilDevices.Throttle.LedCommand,
                document.ThrottleFrame!));
        }
        catch (Exception)
        {
        }

        try
        {
            using var alpha = new VirpilHidTransport(VirpilDevices.Alpha);
            var reports = new List<byte[]>();
            if (document.ResetAlpha)
            {
                reports.Add(VirpilLedProtocol.BuildResetReport());
            }

            reports.Add(VirpilLedProtocol.BuildReport(
                VirpilDevices.Alpha.LedCommand,
                document.AlphaFrame!));
            alpha.SendBatch(reports);
        }
        catch (Exception)
        {
        }
    }

    private static bool IsFrame(byte[]? frame) => frame is { Length: VirpilLedProtocol.FrameLength };

    private static async Task ClearAlertsAsync(int port)
    {
        if (port is < 1 or > 65535)
        {
            return;
        }

        var json = "{\"JoydexPrimaryB1State\":0,\"JoydexPrimaryB2State\":0," +
            "\"JoydexPrimaryB4State\":0,\"JoydexPrimaryB5State\":0," +
            "\"JoydexOverflowB1State\":0,\"JoydexOverflowB2State\":0," +
            "\"JoydexOverflowB3State\":0,\"JoydexOverflowB4State\":0," +
            "\"JoydexOverflowB5State\":0,\"JoydexOverflowB6State\":0," +
            "\"JoydexAlphaState\":0}";
        var payload = Encoding.UTF8.GetBytes(json);
        using var client = new UdpClient(AddressFamily.InterNetwork);
        await client.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, port)).ConfigureAwait(false);
    }

    private static bool TryArgument(IReadOnlyList<string> args, string name, out string value)
    {
        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                value = args[index + 1];
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}

[JsonSerializable(typeof(JoydexLedRecoveryDocument))]
internal sealed partial class GuardianJsonContext : JsonSerializerContext;
