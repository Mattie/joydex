using System.Text;
using System.Text.Json;

namespace Joydex.Windows.WirelessPanel;

internal readonly record struct ServerSentEvent(string EventType, string Data);

internal static class EspHomeSseParser
{
    private const int MaximumLineCharacters = 16 * 1024;
    private const int MaximumEventDataCharacters = 64 * 1024;

    public static Task ReadAsync(
        Stream stream,
        Func<ServerSentEvent, CancellationToken, ValueTask> onEvent,
        CancellationToken cancellationToken) =>
        ReadAsync(
            stream,
            onEvent,
            Timeout.InfiniteTimeSpan,
            cancellationToken);

    public static async Task ReadAsync(
        Stream stream,
        Func<ServerSentEvent, CancellationToken, ValueTask> onEvent,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onEvent);
        if (idleTimeout <= TimeSpan.Zero && idleTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        }

        using var reader = new BoundedSseLineReader(stream, MaximumLineCharacters);
        var eventType = "message";
        StringBuilder? data = null;

        while (await reader.ReadLineAsync(idleTimeout, cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data is not null)
                {
                    if (data.Length > 0)
                    {
                        data.Length--;
                    }

                    await onEvent(
                            new ServerSentEvent(eventType, data.ToString()),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                eventType = "message";
                data?.Clear();
                data = null;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "event":
                    eventType = value;
                    break;
                case "data":
                    data ??= new StringBuilder();
                    if (data.Length + value.Length + 1 > MaximumEventDataCharacters)
                    {
                        throw new IOException(
                            $"The ESPHome event exceeded {MaximumEventDataCharacters} characters.");
                    }

                    data.Append(value);
                    data.Append('\n');
                    break;
            }
        }
    }

    private sealed class BoundedSseLineReader : IDisposable
    {
        private readonly StreamReader _reader;
        private readonly int _maximumLineCharacters;
        private readonly char[] _buffer = new char[1024];
        private int _position;
        private int _count;
        private bool _skipLeadingLineFeed;

        public BoundedSseLineReader(Stream stream, int maximumLineCharacters)
        {
            _reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024,
                leaveOpen: true);
            _maximumLineCharacters = maximumLineCharacters;
        }

        public async ValueTask<string?> ReadLineAsync(
            TimeSpan idleTimeout,
            CancellationToken cancellationToken)
        {
            if (idleTimeout == Timeout.InfiniteTimeSpan)
            {
                return await ReadLineCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            using var idleCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idleCancellation.CancelAfter(idleTimeout);
            try
            {
                return await ReadLineCoreAsync(idleCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested
                      && idleCancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The ESPHome event stream was idle for more than " +
                    $"{idleTimeout.TotalSeconds:g} seconds.",
                    exception);
            }
        }

        public void Dispose() => _reader.Dispose();

        private async ValueTask<string?> ReadLineCoreAsync(
            CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_position >= _count)
                {
                    _count = await _reader
                        .ReadAsync(_buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    _position = 0;
                    if (_count == 0)
                    {
                        return line.Length == 0 ? null : line.ToString();
                    }
                }

                var character = _buffer[_position++];
                if (_skipLeadingLineFeed)
                {
                    _skipLeadingLineFeed = false;
                    if (character == '\n')
                    {
                        continue;
                    }
                }

                if (character == '\r')
                {
                    _skipLeadingLineFeed = true;
                    return line.ToString();
                }

                if (character == '\n')
                {
                    return line.ToString();
                }

                if (line.Length >= _maximumLineCharacters)
                {
                    throw new IOException(
                        $"The ESPHome event stream line exceeded " +
                        $"{_maximumLineCharacters} characters.");
                }

                line.Append(character);
            }
        }
    }
}

internal readonly record struct EspHomeStateEvent(string Identifier, bool IsOn);

internal static class EspHomeStateEventParser
{
    public static bool TryParse(string json, out EspHomeStateEvent stateEvent)
    {
        stateEvent = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var identifier = ReadIdentifier(root);
            if (identifier is null || !TryReadState(root, out var isOn))
            {
                return false;
            }

            stateEvent = new EspHomeStateEvent(identifier, isOn);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadIdentifier(JsonElement root)
    {
        if (root.TryGetProperty("name_id", out var nameId) &&
            nameId.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(nameId.GetString()))
        {
            return nameId.GetString();
        }

        if (root.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(id.GetString()))
        {
            return id.GetString();
        }

        return null;
    }

    private static bool TryReadState(JsonElement root, out bool isOn)
    {
        isOn = false;
        if (root.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String)
        {
            if (string.Equals(state.GetString(), "ON", StringComparison.OrdinalIgnoreCase))
            {
                isOn = true;
                return true;
            }

            if (string.Equals(state.GetString(), "OFF", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (root.TryGetProperty("value", out var value))
        {
            if (value.ValueKind == JsonValueKind.True)
            {
                isOn = true;
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class EspHomePressTracker
{
    private static readonly IReadOnlyDictionary<string, EspHomePanelButton> Buttons =
        new Dictionary<string, EspHomePanelButton>(StringComparer.Ordinal)
        {
            ["binary_sensor/Task 1"] = EspHomePanelButton.Task1,
            ["binary_sensor/Task 2"] = EspHomePanelButton.Task2,
            ["binary_sensor/Task 3"] = EspHomePanelButton.Task3,
            ["binary_sensor/Task 4"] = EspHomePanelButton.Task4,
            ["binary_sensor/Sidebar"] = EspHomePanelButton.PlanMode,
            ["binary_sensor-task_1"] = EspHomePanelButton.Task1,
            ["binary_sensor-task_2"] = EspHomePanelButton.Task2,
            ["binary_sensor-task_3"] = EspHomePanelButton.Task3,
            ["binary_sensor-task_4"] = EspHomePanelButton.Task4,
            ["binary_sensor-sidebar"] = EspHomePanelButton.PlanMode,
        };

    private readonly Dictionary<EspHomePanelButton, bool> _lastStates = [];

    public bool TryObserve(EspHomeStateEvent stateEvent, out EspHomePanelButton pressed)
    {
        pressed = default;
        if (!Buttons.TryGetValue(stateEvent.Identifier, out var button))
        {
            return false;
        }

        if (!_lastStates.TryGetValue(button, out var previous))
        {
            // ESPHome sends every current state after an SSE connection. The first value for each
            // allowlisted entity seeds the edge detector and cannot dispatch a stale held press.
            _lastStates[button] = stateEvent.IsOn;
            return false;
        }

        _lastStates[button] = stateEvent.IsOn;
        if (previous || !stateEvent.IsOn)
        {
            return false;
        }

        pressed = button;
        return true;
    }
}
