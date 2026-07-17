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
    int WheelNotches = ButtonBinding.DefaultWheelNotches);

public sealed class BindingEngine(CompanionConfig config)
{
    private readonly Dictionary<string, DateTimeOffset> _lastDispatch = new(StringComparer.OrdinalIgnoreCase);

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
            var bindings = config.Bindings.Where(candidate =>
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
                    : config.Polling.ActionCooldownMs;
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
                    binding.WheelNotches));
            }
        }

        return requests;
    }

    public void Reset() => _lastDispatch.Clear();

    public string? ResolveActiveBank(JoystickSnapshot snapshot)
    {
        var activeBanks = config.BankSelectors
            .Where(pair => pair.Value > 0
                && pair.Value <= snapshot.Buttons.Length
                && snapshot.Buttons[pair.Value - 1])
            .Select(pair => pair.Key)
            .Take(2)
            .ToArray();

        return activeBanks.Length == 1 ? activeBanks[0] : null;
    }
}
