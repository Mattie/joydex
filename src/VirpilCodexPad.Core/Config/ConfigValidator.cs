using VirpilCodexPad.Core.Mapping;

namespace VirpilCodexPad.Core.Config;

public static class ConfigValidator
{
    public static IReadOnlyList<string> Validate(CompanionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Device.ProductNameContains)
            && string.IsNullOrWhiteSpace(config.Device.InstanceGuid))
        {
            errors.Add("device.productNameContains or device.instanceGuid must identify a device");
        }

        AddGuidError(errors, config.Device.InstanceGuid, "device.instanceGuid");
        AddGuidError(errors, config.Device.ProductGuid, "device.productGuid");

        AddRangeError(errors, config.Polling.ConnectWarmupMs, 0, 5000, "polling.connectWarmupMs");
        AddRangeError(errors, config.Polling.PollIntervalMs, 5, 1000, "polling.pollIntervalMs");
        AddRangeError(errors, config.Polling.ReconnectIntervalMs, 250, 60_000, "polling.reconnectIntervalMs");
        AddRangeError(errors, config.Polling.ActionCooldownMs, 0, 10_000, "polling.actionCooldownMs");
        AddRangeError(errors, config.Polling.AxisTraceThreshold, 1, 65_535, "polling.axisTraceThreshold");

        if (!config.Safety.RequireCodexForeground)
        {
            errors.Add("safety.requireCodexForeground must remain true in this release");
        }

        if (config.Safety.CodexProcessNames.Length == 0
            || config.Safety.CodexProcessNames.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("safety.codexProcessNames must contain at least one non-empty process name");
        }

        if (config.Safety.SimulatorProcessNames.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("safety.simulatorProcessNames cannot contain empty process names");
        }

        if (!string.Equals(
                config.OpenWorkingDirectory.Target,
                OpenWorkingDirectoryOptions.VisualStudioCodeTarget,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                config.OpenWorkingDirectory.Target,
                OpenWorkingDirectoryOptions.FileExplorerTarget,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("openWorkingDirectory.target must be 'vscode' or 'explorer'");
        }

        var selectorButtons = new HashSet<int>();
        foreach (var (bank, button) in config.BankSelectors)
        {
            if (string.IsNullOrWhiteSpace(bank))
            {
                errors.Add("bank selector names cannot be empty");
            }
            else if (string.Equals(bank, CompanionConfig.AlwaysBank, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"'{CompanionConfig.AlwaysBank}' is reserved for bindings that do not need the mode dial");
            }

            if (button < 1)
            {
                errors.Add($"bank selector '{bank}' must use a one-based button number");
            }
            else if (!selectorButtons.Add(button))
            {
                errors.Add($"logical button {button} is assigned to more than one bank selector");
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bankButtons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in config.Bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.Name) || !names.Add(binding.Name))
            {
                errors.Add($"binding name '{binding.Name}' is empty or duplicated");
            }

            var isAlways = string.Equals(
                binding.Bank,
                CompanionConfig.AlwaysBank,
                StringComparison.OrdinalIgnoreCase);
            if (!isAlways
                && !config.BankSelectors.Keys.Contains(binding.Bank, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"binding '{binding.Name}' references unknown bank '{binding.Bank}'");
            }

            if (binding.Button < 1)
            {
                errors.Add($"binding '{binding.Name}' must use a one-based button number");
            }
            else if (selectorButtons.Contains(binding.Button))
            {
                errors.Add($"binding '{binding.Name}' reuses bank selector button {binding.Button}");
            }
            else if (!bankButtons.Add($"{binding.Bank}:{binding.Button}:{binding.Trigger}"))
            {
                errors.Add($"bank '{binding.Bank}' has more than one {binding.Trigger} binding for button {binding.Button}");
            }

            if (!string.Equals(binding.Trigger, "press", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(binding.Trigger, "release", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"binding '{binding.Name}' uses unsupported trigger '{binding.Trigger}'");
            }

            if (!CodexActionCatalog.TryParse(binding.Action, out var action))
            {
                errors.Add($"binding '{binding.Name}' uses unsupported action '{binding.Action}'");
            }
            else if (string.Equals(binding.Trigger, "release", StringComparison.OrdinalIgnoreCase)
                && action is not (CodexAction.PushToTalk or CodexAction.ButtonMap))
            {
                errors.Add($"binding '{binding.Name}' cannot use a release trigger with action '{binding.Action}'");
            }

            if (action is CodexAction.ScrollUp or CodexAction.ScrollDown
                && (binding.WheelNotches < ButtonBinding.DefaultWheelNotches
                    || binding.WheelNotches > ButtonBinding.MaximumWheelNotches))
            {
                errors.Add(
                    $"binding '{binding.Name}' wheelNotches must be between "
                    + $"{ButtonBinding.DefaultWheelNotches} and {ButtonBinding.MaximumWheelNotches}");
            }
        }

        return errors;
    }

    private static void AddRangeError(List<string> errors, int value, int minimum, int maximum, string path)
    {
        if (value < minimum || value > maximum)
        {
            errors.Add($"{path} must be between {minimum} and {maximum}");
        }
    }

    private static void AddGuidError(List<string> errors, string? value, string path)
    {
        if (!string.IsNullOrWhiteSpace(value) && !Guid.TryParse(value, out _))
        {
            errors.Add($"{path} must be a valid GUID when provided");
        }
    }
}
