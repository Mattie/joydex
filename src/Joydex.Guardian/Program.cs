using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

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
                        await ClearAlertsAsync(port).ConfigureAwait(false);
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
                await ClearAlertsAsync(port).ConfigureAwait(false);
            }
        }
        finally
        {
            parent?.Dispose();
        }

        return 0;
    }

    private static async Task ClearAlertsAsync(int port)
    {
        if (port is < 1 or > 65535)
        {
            return;
        }

        var json = "{\"JoydexB1State\":0,\"JoydexB2State\":0,\"JoydexB4State\":0," +
            "\"JoydexB5State\":0,\"JoydexAlphaState\":0}";
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
