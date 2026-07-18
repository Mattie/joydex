using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using HidSharp;
using Joydex.Core.TaskAlerts;

namespace Joydex.Windows.TaskAlerts;

public sealed record LinkToolTelemetryState(
    int JoydexBank,
    int JoydexPrimaryB1State,
    int JoydexPrimaryB2State,
    int JoydexPrimaryB4State,
    int JoydexPrimaryB5State,
    int JoydexOverflowB1State,
    int JoydexOverflowB2State,
    int JoydexOverflowB3State,
    int JoydexOverflowB4State,
    int JoydexOverflowB5State,
    int JoydexOverflowB6State,
    int JoydexAlphaState)
{
    public bool HasAlert => JoydexPrimaryB1State != 0
        || JoydexPrimaryB2State != 0
        || JoydexPrimaryB4State != 0
        || JoydexPrimaryB5State != 0
        || JoydexOverflowB1State != 0
        || JoydexOverflowB2State != 0
        || JoydexOverflowB3State != 0
        || JoydexOverflowB4State != 0
        || JoydexOverflowB5State != 0
        || JoydexOverflowB6State != 0
        || JoydexAlphaState != 0;

    public static LinkToolTelemetryState From(TaskAlertSnapshot snapshot, bool suppressAlerts = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var states = new int[11];
        if (snapshot.Enabled && !suppressAlerts)
        {
            foreach (var assignment in snapshot.Assignments)
            {
                if (assignment.Slot is >= 1 and <= 10)
                {
                    states[assignment.Slot] = Encode(assignment.State);
                }
            }
        }

        var alpha = snapshot.Enabled && !suppressAlerts
            ? snapshot.Assignments
                .OrderByDescending(assignment => Priority(assignment.State))
                .Select(assignment => Encode(assignment.State))
                .FirstOrDefault()
            : 0;

        return new LinkToolTelemetryState(
            snapshot.Bank,
            states[1],
            states[2],
            states[3],
            states[4],
            states[5],
            states[6],
            states[7],
            states[8],
            states[9],
            states[10],
            alpha);
    }

    private static int Encode(TaskAlertState state) => state switch
    {
        TaskAlertState.Running => 1,
        TaskAlertState.Approval => 2,
        TaskAlertState.Completed => 3,
        TaskAlertState.Fault => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static int Priority(TaskAlertState state) => state switch
    {
        TaskAlertState.Fault => 4,
        TaskAlertState.Approval => 3,
        TaskAlertState.Completed => 2,
        TaskAlertState.Running => 1,
        _ => 0,
    };
}

public interface ILinkToolTelemetrySender
{
    bool IsListening { get; }

    Task<bool> SendAsync(LinkToolTelemetryState state, CancellationToken cancellationToken);
}

public sealed class UdpLinkToolTelemetrySender(string host = "127.0.0.1", int port = 4123)
    : ILinkToolTelemetrySender
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };
    private readonly IPEndPoint _endpoint = new(IPAddress.Parse(host), port);

    public bool IsListening
    {
        get
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveUdpListeners()
                    .Any(endpoint => endpoint.Port == _endpoint.Port
                        && (endpoint.Address.Equals(_endpoint.Address)
                            || endpoint.Address.Equals(IPAddress.Any)
                            || endpoint.Address.Equals(IPAddress.IPv6Any)));
            }
            catch (NetworkInformationException)
            {
                return false;
            }
        }
    }

    public async Task<bool> SendAsync(LinkToolTelemetryState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            using var client = new UdpClient(_endpoint.AddressFamily);
            var payload = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            await client.SendAsync(payload, _endpoint, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }
}

public static class LinkToolProfileWriter
{
    public const string ThrottleName = "MongoosT-50CM3 Throttle";
    public const ushort ThrottleVendorId = 0x3344;
    public const ushort ThrottleProductId = 0x8194;
    public const string AlphaName = "Constellation ALPHA-R Grip";
    public const ushort AlphaVendorId = 0x3344;
    public const ushort AlphaProductId = 0x40CC;

    private static readonly IReadOnlyDictionary<int, VirpilLedColor> DefaultBankColors =
        new Dictionary<int, VirpilLedColor>
        {
            [1] = new(0x00, 0x00, 0x00),
            [2] = new(0x00, 0x00, 0xFF),
            [3] = new(0x00, 0xFF, 0x00),
            [4] = new(0xFF, 0x00, 0x00),
            [5] = new(0xFF, 0xFF, 0x00),
        };

    public static string Write(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var throttleKey = FindDevicePath(ThrottleVendorId, ThrottleProductId);
        var alphaKey = FindDevicePath(AlphaVendorId, AlphaProductId);
        return Write(path, throttleKey, alphaKey);
    }

    internal static string Write(string path, string throttleKey, string alphaKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(throttleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(alphaKey);
        var rules = new List<object>();

        foreach (var slot in TaskAlertSlots.Primary)
        {
            foreach (var bank in TaskAlertSlots.Banks(slot))
            {
                foreach (var state in Enum.GetValues<TaskAlertState>())
                {
                    rules.Add(Rule(
                        ThrottleName,
                        throttleKey,
                        TelemetryArgument(slot),
                        Encode(state),
                        TaskAlertSlots.Button(slot),
                        VirpilLedColor.For(state),
                        $"Joydex primary M{bank} B{TaskAlertSlots.Button(slot)} {state.ToString().ToLowerInvariant()}",
                        priority: 100,
                        bank: bank));
                }
            }
        }

        foreach (var slot in TaskAlertSlots.Overflow)
        {
            foreach (var state in Enum.GetValues<TaskAlertState>())
            {
                rules.Add(Rule(
                    ThrottleName,
                    throttleKey,
                    TelemetryArgument(slot),
                    Encode(state),
                    TaskAlertSlots.Button(slot),
                    VirpilLedColor.For(state),
                    $"Joydex overflow M1 B{TaskAlertSlots.Button(slot)} {state.ToString().ToLowerInvariant()}",
                    priority: 100,
                    bank: 1));
            }
        }

        // LinkTool resolves competing rules for one LED in profile order. Keep
        // bank-gated state rules ahead of the always-matching baselines. M1's
        // baseline is black so an empty overflow page is dark. M5 has baseline
        // rules only, so every control on that page keeps its command.
        foreach (var channel in Enumerable.Range(1, 6))
        {
            foreach (var bank in Enumerable.Range(1, 5))
            {
                rules.Add(Rule(
                    ThrottleName,
                    throttleKey,
                    "JoydexBank",
                    bank,
                    channel,
                    DefaultBankColors[bank],
                    $"Joydex M{bank} B{channel} baseline",
                    priority: 0));
            }
        }

        foreach (var state in Enum.GetValues<TaskAlertState>())
        {
            rules.Add(Rule(
                AlphaName,
                alphaKey,
                "JoydexAlphaState",
                Encode(state),
                1,
                VirpilLedColor.For(state),
                $"Joydex Alpha {state.ToString().ToLowerInvariant()}",
                priority: 100));
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The LinkTool profile path has no parent directory."));
        var temporaryPath = fullPath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new { rules }, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return fullPath;
    }

    private static object Rule(
        string device,
        string deviceKey,
        string argument,
        int value,
        int led,
        VirpilLedColor color,
        string comment,
        int priority,
        int? bank = null)
    {
        var conditions = new List<object>
        {
            new { argument, condition = "==", value = value.ToString() },
        };
        if (bank is not null)
        {
            conditions.Add(new { argument = "JoydexBank", condition = "==", value = bank.Value.ToString() });
        }

        return new
        {
            device,
            argument,
            deviceKey,
            primaryCondition = "==",
            primaryValue = value.ToString(),
            ledMode = "Solid",
            secondaryCondition = "-",
            secondaryValue = string.Empty,
            ledNumber = $"LED {led}",
            ledNumbers = new[] { $"LED {led}" },
            colorOne = Bgr(color).ToString(),
            colorTwo = "1511950",
            comment,
            isEnabled = true,
            priority,
            ruleType = "Telemetry",
            keyboardCombo = string.Empty,
            conditions,
            buttonRule = new
            {
                sourceDevice = AlphaName,
                button = "1",
                mode = "Pressed",
                shiftSourceDevice = string.Empty,
                shiftButton = string.Empty,
                shiftMode = "Pressed",
            },
            axisRule = new
            {
                sourceDevice = AlphaName,
                axisName = "Axis 1",
                axisCondition = ">=",
                primaryValue = string.Empty,
                secondaryValue = string.Empty,
            },
        };
    }

    private static string FindDevicePath(ushort vendorId, ushort productId) =>
        DeviceList.Local.GetHidDevices(vendorId, productId)
            .FirstOrDefault(device => device.GetMaxFeatureReportLength() >= 38)
            ?.DevicePath
        ?? throw new IOException($"VIRPIL HID device {vendorId:X4}:{productId:X4} is unavailable.");

    private static int Bgr(VirpilLedColor color) => color.Red | (color.Green << 8) | (color.Blue << 16);

    internal static string TelemetryArgument(int slot) => TaskAlertSlots.Page(slot) switch
    {
        TaskAlertPage.Primary => $"JoydexPrimaryB{TaskAlertSlots.Button(slot)}State",
        TaskAlertPage.Overflow => $"JoydexOverflowB{TaskAlertSlots.Button(slot)}State",
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    private static int Encode(TaskAlertState state) => state switch
    {
        TaskAlertState.Running => 1,
        TaskAlertState.Approval => 2,
        TaskAlertState.Completed => 3,
        TaskAlertState.Fault => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
