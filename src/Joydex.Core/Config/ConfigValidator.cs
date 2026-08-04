using Joydex.Core.Mapping;

namespace Joydex.Core.Config;

public static class ConfigValidator
{
    public static IReadOnlyList<string> Validate(CompanionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config = CompanionConfigNormalizer.Normalize(config);

        var errors = new List<string>();
        var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in config.Devices)
        {
            if (string.IsNullOrWhiteSpace(device.Id) || !deviceIds.Add(device.Id))
            {
                errors.Add($"device profile ID '{device.Id}' is empty or duplicated");
            }
            else if (device.Id.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            {
                errors.Add($"device profile ID '{device.Id}' may contain only letters, numbers, '-' and '_'");
            }

            if (string.IsNullOrWhiteSpace(device.DisplayName))
            {
                errors.Add($"device '{device.Id}' needs a display name");
            }

            if (string.IsNullOrWhiteSpace(device.Selector.ProductNameContains)
                && string.IsNullOrWhiteSpace(device.Selector.InstanceGuid))
            {
                errors.Add($"device '{device.Id}' needs productNameContains or instanceGuid");
            }

            AddGuidError(errors, device.Selector.InstanceGuid, $"devices[{device.Id}].selector.instanceGuid");
            AddGuidError(errors, device.Selector.ProductGuid, $"devices[{device.Id}].selector.productGuid");
            if (device.ButtonMapTemplate is not null
                && !string.Equals(device.ButtonMapTemplate, "cm3", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(device.ButtonMapTemplate, "alpha-warbrd", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"device '{device.Id}' uses unsupported button-map template '{device.ButtonMapTemplate}'");
            }

            if (device.ButtonMapHoldControl is not null && device.ButtonMapTemplate is null)
            {
                errors.Add($"device '{device.Id}' needs a button-map template before assigning a map hold");
            }

            var selectorKey = SelectorKey(device.Selector);
            if (!string.IsNullOrEmpty(selectorKey) && !selectorKeys.Add(selectorKey))
            {
                errors.Add($"device '{device.Id}' selects the same physical controller as another profile");
            }
        }

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

        var selectorButtonsByDevice = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in config.Devices)
        {
            var selectorButtons = new HashSet<int>();
            selectorButtonsByDevice[device.Id] = selectorButtons;
            foreach (var (bank, button) in device.BankSelectors)
            {
                if (string.IsNullOrWhiteSpace(bank))
                {
                    errors.Add($"device '{device.Id}' bank selector names cannot be empty");
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
                    errors.Add($"device '{device.Id}' button {button} is assigned to more than one bank selector");
                }
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var occupiedControls = new List<ControlUse>();
        foreach (var binding in config.Bindings)
        {
            var deviceId = string.IsNullOrWhiteSpace(binding.DeviceId)
                ? config.Devices[0].Id
                : binding.DeviceId;
            if (string.IsNullOrWhiteSpace(binding.Name) || !names.Add(binding.Name))
            {
                errors.Add($"binding name '{binding.Name}' is empty or duplicated");
            }

            var isAlways = string.Equals(
                binding.Bank,
                CompanionConfig.AlwaysBank,
                StringComparison.OrdinalIgnoreCase);
            var device = config.Devices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                errors.Add($"binding '{binding.Name}' references unknown device '{deviceId}'");
            }
            else if (!isAlways
                && !device.BankSelectors.Keys.Contains(binding.Bank, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"binding '{binding.Name}' references unknown bank '{binding.Bank}'");
            }

            if (binding.Button < 1)
            {
                errors.Add($"binding '{binding.Name}' must use a one-based button number");
            }
            else if (selectorButtonsByDevice.TryGetValue(deviceId!, out var selectorButtons)
                && selectorButtons.Contains(binding.Button))
            {
                errors.Add($"binding '{binding.Name}' reuses bank selector button {binding.Button}");
            }
            else if (!TryOccupyOrdinaryBinding(
                occupiedControls,
                deviceId!,
                binding.Bank,
                binding.Button,
                binding.Trigger))
            {
                errors.Add(
                    $"bank '{binding.Bank}' has more than one {binding.Trigger} binding for button {binding.Button}; "
                    + $"binding '{binding.Name}' conflicts with another control on device '{deviceId}'");
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
                && action is not (
                    CodexAction.PushToTalk
                    or CodexAction.ButtonMap
                    or CodexAction.ToggleVoiceChat
                    or CodexAction.EndVoiceChat))
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

        ValidatePromptPickers(config, errors, occupiedControls, selectorButtonsByDevice);

        foreach (var target in config.Devices.Where(device => device.ButtonMapHoldControl is not null))
        {
            var control = target.ButtonMapHoldControl!;
            var source = config.Devices.FirstOrDefault(device =>
                string.Equals(device.Id, control.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                errors.Add($"device '{target.Id}' button-map hold references unknown device '{control.DeviceId}'");
            }
            else if (!string.Equals(control.Bank, CompanionConfig.AlwaysBank, StringComparison.OrdinalIgnoreCase)
                && !source.BankSelectors.ContainsKey(control.Bank))
            {
                errors.Add($"device '{target.Id}' button-map hold references unknown bank '{control.Bank}' on '{control.DeviceId}'");
            }

            if (control.Button < 1)
            {
                errors.Add($"device '{target.Id}' button-map hold must use a one-based button number");
            }
            else if (selectorButtonsByDevice.TryGetValue(control.DeviceId, out var selectorButtons)
                && selectorButtons.Contains(control.Button))
            {
                errors.Add($"device '{target.Id}' button-map hold reuses bank selector button {control.Button}");
            }
            else if (!TryOccupyControl(
                occupiedControls,
                control.DeviceId,
                control.Bank,
                control.Button,
                "press"))
            {
                errors.Add($"device '{target.Id}' button-map hold conflicts with {control.DeviceId}/button {control.Button}");
            }
        }

        return errors;
    }

    private static void ValidatePromptPickers(
        CompanionConfig config,
        List<string> errors,
        List<ControlUse> occupiedControls,
        IReadOnlyDictionary<string, HashSet<int>> selectorButtonsByDevice)
    {
        if (config.PromptPickers.Count is < 1 or > 3)
        {
            errors.Add("promptPickers must contain between one and three pickers");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var picker in config.PromptPickers)
        {
            if (string.IsNullOrWhiteSpace(picker.Id) || !ids.Add(picker.Id))
            {
                errors.Add($"prompt picker ID '{picker.Id}' is empty or duplicated");
            }

            if (string.IsNullOrWhiteSpace(picker.Name))
            {
                errors.Add($"prompt picker '{picker.Id}' needs a name");
            }

            if (picker.Prompts.Count == 0 || picker.Prompts.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"prompt picker '{picker.Id}' must contain nonblank prompts");
            }

            if (picker.DefaultPromptIndex < 0 || picker.DefaultPromptIndex >= picker.Prompts.Count)
            {
                errors.Add($"prompt picker '{picker.Id}' defaultPromptIndex is outside its prompt list");
            }

            ValidatePickerControl(config, picker, "up", picker.Controls.Up, occupiedControls, selectorButtonsByDevice, errors);
            ValidatePickerControl(config, picker, "down", picker.Controls.Down, occupiedControls, selectorButtonsByDevice, errors);
            ValidatePickerControl(config, picker, "insert", picker.Controls.Insert, occupiedControls, selectorButtonsByDevice, errors);
        }
    }

    private static void ValidatePickerControl(
        CompanionConfig config,
        PromptPickerConfig picker,
        string role,
        DeviceControlReference control,
        List<ControlUse> occupiedControls,
        IReadOnlyDictionary<string, HashSet<int>> selectorButtonsByDevice,
        List<string> errors)
    {
        var device = config.Devices.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, control.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            errors.Add($"prompt picker '{picker.Id}' {role} references unknown device '{control.DeviceId}'");
            return;
        }

        if (control.Button < 1)
        {
            errors.Add($"prompt picker '{picker.Id}' {role} needs a one-based button number");
        }

        if (!string.Equals(control.Bank, CompanionConfig.AlwaysBank, StringComparison.OrdinalIgnoreCase)
            && !device.BankSelectors.ContainsKey(control.Bank))
        {
            errors.Add($"prompt picker '{picker.Id}' {role} references unknown bank '{control.Bank}'");
        }

        if (control.Button > 0 && selectorButtonsByDevice[device.Id].Contains(control.Button))
        {
            errors.Add($"prompt picker '{picker.Id}' {role} reuses bank selector button {control.Button}");
        }

        if (control.Button > 0
            && !TryOccupyControl(occupiedControls, device.Id, control.Bank, control.Button, "press"))
        {
            errors.Add($"prompt picker '{picker.Id}' {role} conflicts with {device.DisplayName} button {control.Button}");
        }
    }

    private static bool TryOccupyControl(
        List<ControlUse> occupied,
        string deviceId,
        string bank,
        int button,
        string trigger)
    {
        var conflicts = occupied.Any(existing =>
            string.Equals(existing.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
            && existing.Button == button
            && string.Equals(existing.Trigger, trigger, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(existing.Bank, bank, StringComparison.OrdinalIgnoreCase)
                || string.Equals(existing.Bank, CompanionConfig.AlwaysBank, StringComparison.OrdinalIgnoreCase)
                || string.Equals(bank, CompanionConfig.AlwaysBank, StringComparison.OrdinalIgnoreCase)));
        if (conflicts)
        {
            return false;
        }

        occupied.Add(new ControlUse(deviceId, bank, button, trigger));
        return true;
    }

    private static bool TryOccupyOrdinaryBinding(
        List<ControlUse> occupied,
        string deviceId,
        string bank,
        int button,
        string trigger)
    {
        var conflicts = occupied.Any(existing =>
            string.Equals(existing.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
            && existing.Button == button
            && string.Equals(existing.Trigger, trigger, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Bank, bank, StringComparison.OrdinalIgnoreCase));
        if (conflicts)
        {
            return false;
        }

        occupied.Add(new ControlUse(deviceId, bank, button, trigger));
        return true;
    }

    private static string SelectorKey(DeviceSelector selector)
    {
        if (!string.IsNullOrWhiteSpace(selector.InstanceGuid))
        {
            return $"instance:{selector.InstanceGuid.Trim()}";
        }

        return string.IsNullOrWhiteSpace(selector.ProductNameContains)
            ? string.Empty
            : $"name:{selector.ProductNameContains.Trim()}|product:{selector.ProductGuid?.Trim()}";
    }

    private sealed record ControlUse(string DeviceId, string Bank, int Button, string Trigger);

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
