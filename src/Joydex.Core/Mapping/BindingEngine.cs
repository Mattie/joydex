using Joydex.Core.Config;
using Joydex.Core.Input;

namespace Joydex.Core.Mapping;

public sealed record ActionRequest(
    string BindingName,
    string Bank,
    int Button,
    string Trigger,
    CodexAction Action,
    DateTimeOffset RequestedAt,
    int WheelNotches = ButtonBinding.DefaultWheelNotches,
    string DeviceId = CompanionConfigNormalizer.PrimaryDeviceId);

public sealed class BindingEngine
{
    private readonly CompanionConfig _config;
    private readonly string _deviceId;
    private readonly Dictionary<string, DateTimeOffset> _lastDispatch = new(StringComparer.OrdinalIgnoreCase);

    public BindingEngine(CompanionConfig config, string? deviceId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = CompanionConfigNormalizer.Normalize(config);
        _deviceId = deviceId ?? _config.Devices[0].Id;
    }

    public IReadOnlyList<ActionRequest> Resolve(
        JoystickSnapshot snapshot,
        IReadOnlyList<JoystickEvent> events,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(events);

        var activeBank = ResolveActiveBank(snapshot);

        var requests = new List<ActionRequest>();
        foreach (var inputEvent in events)
        {
            var trigger = inputEvent.Kind switch
            {
                JoystickEventKind.ButtonPressed => "press",
                JoystickEventKind.ButtonReleased => "release",
                _ => null,
            };
            if (trigger is null)
            {
                continue;
            }

            var button = inputEvent.DisplayIndex;
            var bindings = _config.Bindings.Where(candidate =>
                string.Equals(candidate.DeviceId, _deviceId, StringComparison.OrdinalIgnoreCase)
                &&
                (string.Equals(candidate.Bank, CompanionConfig.AlwaysBank, StringComparison.OrdinalIgnoreCase)
                    || activeBank is not null
                    && string.Equals(candidate.Bank, activeBank, StringComparison.OrdinalIgnoreCase))
                && candidate.Button == button
                && string.Equals(candidate.Trigger, trigger, StringComparison.OrdinalIgnoreCase));

            foreach (var binding in bindings)
            {
                if (!CodexActionCatalog.TryParse(binding.Action, out var action))
                {
                    continue;
                }

                var cooldownMs = action is CodexAction.IncreaseReasoning
                    or CodexAction.DecreaseReasoning
                    or CodexAction.ScrollUp
                    or CodexAction.ScrollDown
                    ? 0
                    : _config.Polling.ActionCooldownMs;
                if (_lastDispatch.TryGetValue(binding.Name, out var last)
                    && now - last < TimeSpan.FromMilliseconds(cooldownMs))
                {
                    continue;
                }

                _lastDispatch[binding.Name] = now;
                requests.Add(new ActionRequest(
                    binding.Name,
                    binding.Bank,
                    button,
                    trigger,
                    action,
                    now,
                    binding.WheelNotches,
                    _deviceId));
            }
        }

        return requests;
    }

    public void Reset() => _lastDispatch.Clear();

    public string? ResolveActiveBank(JoystickSnapshot snapshot)
    {
        var selectors = _config.Devices.FirstOrDefault(device =>
            string.Equals(device.Id, _deviceId, StringComparison.OrdinalIgnoreCase))?.BankSelectors
            ?? new Dictionary<string, int>();
        var activeBanks = selectors
            .Where(pair => pair.Value > 0
                && pair.Value <= snapshot.Buttons.Length
                && snapshot.Buttons[pair.Value - 1])
            .Select(pair => pair.Key)
            .Take(2)
            .ToArray();

        return activeBanks.Length == 1 ? activeBanks[0] : null;
    }
}
