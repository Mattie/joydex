using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Windows.Input;
using Joydex.Windows.Interop;

namespace Joydex.Trace;

internal static class Program
{
    private static readonly string[] AxisNames = ["X", "Y", "Z", "Rx", "Ry", "Rz", "Slider 1", "Slider 2"];

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        if (command is not ("list" or "trace"))
        {
            PrintUsage();
            return 64;
        }

        using var window = new CooperativeWindow("Joydex Input Trace");
        using var source = new DirectInputJoystickSource(window.Handle);

        var devices = source.EnumerateDevices();
        PrintDevices(devices);
        if (command == "list")
        {
            return devices.Count > 0 ? 0 : 2;
        }

        var selector = new DeviceSelector
        {
            ProductNameContains = GetOption(args, "--name") ?? "VPC Throttle MT-50CM3",
            InstanceGuid = GetOption(args, "--instance-guid"),
            ProductGuid = GetOption(args, "--product-guid"),
        };

        if (!source.TryConnect(selector, out var connectionMessage))
        {
            Console.Error.WriteLine(connectionMessage);
            return 2;
        }

        Console.WriteLine(connectionMessage);
        Console.WriteLine("Move one control at a time. Button numbers below are the one-based values used in config.json.");
        Console.WriteLine("Press Ctrl+C to stop.");

        var seconds = ParseIntOption(args, "--seconds", 60, minimum: 0, maximum: 3600);
        var axisThreshold = ParseIntOption(args, "--axis-threshold", 512, minimum: 1, maximum: 65_535);
        var warmupMs = ParseIntOption(args, "--warmup-ms", 250, minimum: 0, maximum: 5000);
        var detector = new InputEventDetector(axisThreshold);
        using var cancellation = new CancellationTokenSource();
        if (seconds > 0)
        {
            cancellation.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var describedShape = false;
        try
        {
            await Task.Delay(warmupMs, cancellation.Token).ConfigureAwait(false);
            while (!cancellation.IsCancellationRequested)
            {
                if (!source.TryRead(out var snapshot, out var error) || snapshot is null)
                {
                    Console.Error.WriteLine($"Throttle disconnected: {error ?? "unknown DirectInput error"}");
                    return 3;
                }

                if (!describedShape)
                {
                    Console.WriteLine($"Report shape: {snapshot.Buttons.Length} buttons, {snapshot.PointOfViewControllers.Length} POVs, {snapshot.Axes.Length} axes.");
                    describedShape = true;
                }

                foreach (var inputEvent in detector.Detect(snapshot))
                {
                    Console.WriteLine(Format(inputEvent));
                }

                await Task.Delay(16, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    private static string Format(JoystickEvent inputEvent)
    {
        var timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff");
        return inputEvent.Kind switch
        {
            JoystickEventKind.ButtonPressed => $"{timestamp}  BUTTON {inputEvent.DisplayIndex,3}  DOWN",
            JoystickEventKind.ButtonReleased => $"{timestamp}  BUTTON {inputEvent.DisplayIndex,3}  UP",
            JoystickEventKind.PointOfViewChanged => $"{timestamp}  POV {inputEvent.DisplayIndex}  {FormatPov(inputEvent.Value)}",
            JoystickEventKind.AxisChanged => $"{timestamp}  AXIS {GetAxisName(inputEvent.ControlIndex),-8}  {inputEvent.Value}",
            _ => throw new ArgumentOutOfRangeException(nameof(inputEvent)),
        };
    }

    private static string FormatPov(int value) => value < 0 ? "CENTER" : $"{value / 100.0:0.##} degrees";

    private static string GetAxisName(int index) => index >= 0 && index < AxisNames.Length
        ? AxisNames[index]
        : $"{index + 1}";

    private static void PrintDevices(IReadOnlyList<DirectInputDeviceInfo> devices)
    {
        Console.WriteLine($"Attached DirectInput game controllers: {devices.Count}");
        foreach (var device in devices)
        {
            Console.WriteLine($"- {device.ProductName}");
            Console.WriteLine($"  instance name: {device.InstanceName}");
            Console.WriteLine($"  instance GUID: {device.InstanceGuid}");
            Console.WriteLine($"  product GUID:  {device.ProductGuid}");
        }
    }

    private static string? GetOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static int ParseIntOption(
        IReadOnlyList<string> args,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var raw = GetOption(args, name);
        if (raw is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
        {
            throw new ArgumentException($"{name} must be an integer between {minimum} and {maximum}.");
        }

        return value;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Joydex.Trace list");
        Console.WriteLine("Joydex.Trace trace [--name text] [--instance-guid guid] [--seconds 60] [--axis-threshold 512] [--warmup-ms 250]");
    }
}
