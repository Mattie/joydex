using Joydex.Core.Config;
using Joydex.Core.Input;

namespace Joydex.Core.Mapping;

public enum Cm3ModeDialProfile
{
    StandardButtons,
    FiveWayShift,
}

public sealed record StarterProfile(
    Cm3ModeDialProfile DialProfile,
    IReadOnlyDictionary<string, int> BankSelectors,
    IReadOnlyList<ButtonBinding> Bindings);

public static class CodexMicroStarterProfile
{
    private static readonly string[] AgentActions =
        ["agent-1", "agent-2", "agent-3", "agent-4", "agent-5", "agent-6"];

    private static readonly string[] CommandActions =
        ["fast-mode", "approve", "reject", "fork-task", "push-to-talk", "submit"];

    private static readonly string[] WorkflowActions =
        ["plan-mode", "navigate-back", "toggle-sidebar", "navigate-forward", "new-task", "open-skills"];

    public static Cm3ModeDialProfile DetectDialProfile(JoystickSnapshot? snapshot)
    {
        if (snapshot is not null
            && Enumerable.Range(60, 5).Any(button =>
                button <= snapshot.Buttons.Length && snapshot.Buttons[button - 1]))
        {
            return Cm3ModeDialProfile.StandardButtons;
        }

        return Cm3ModeDialProfile.FiveWayShift;
    }

    public static StarterProfile Create(Cm3ModeDialProfile dialProfile)
    {
        var selectors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bindings = new List<ButtonBinding>();

        if (dialProfile == Cm3ModeDialProfile.StandardButtons)
        {
            selectors["M2 Commands"] = 61;
            selectors["M3 Workflows"] = 62;
            selectors["M4 Agents"] = 63;
            AddBank(bindings, "M2 Commands", 42, CommandActions);
            AddBank(bindings, "M3 Workflows", 42, WorkflowActions);
            AddBank(bindings, "M4 Agents", 42, AgentActions);
        }
        else
        {
            AddBank(bindings, CompanionConfig.AlwaysBank, 56, CommandActions, "M2");
            AddBank(bindings, CompanionConfig.AlwaysBank, 62, WorkflowActions, "M3");
            AddBank(bindings, CompanionConfig.AlwaysBank, 68, AgentActions, "M4");
        }

        AddBinding(bindings, "Reasoning clockwise", CompanionConfig.AlwaysBank, 52, "reasoning-up");
        AddBinding(bindings, "Reasoning counter-clockwise", CompanionConfig.AlwaysBank, 51, "reasoning-down");
        AddBinding(bindings, "Hold-to-talk button", CompanionConfig.AlwaysBank, 53, "push-to-talk");
        AddBinding(bindings, "Hold-to-talk button release", CompanionConfig.AlwaysBank, 53, "push-to-talk", "release");
        AddBinding(bindings, "T4 - Hold-to-talk", CompanionConfig.AlwaysBank, 37, "push-to-talk");
        AddBinding(bindings, "T4 - Hold-to-talk release", CompanionConfig.AlwaysBank, 37, "push-to-talk", "release");
        AddBinding(bindings, "T3 - Show button map", CompanionConfig.AlwaysBank, 36, "button-map");
        AddBinding(bindings, "T3 - Hide button map", CompanionConfig.AlwaysBank, 36, "button-map", "release");
        AddBinding(bindings, "E2 right - Scroll down", CompanionConfig.AlwaysBank, 54, "scroll-down");
        AddBinding(bindings, "E2 left - Scroll up", CompanionConfig.AlwaysBank, 55, "scroll-up");
        AddBinding(bindings, "T7 up - Home", CompanionConfig.AlwaysBank, 48, "home");
        AddBinding(bindings, "T7 down - End", CompanionConfig.AlwaysBank, 49, "end");
        AddBinding(bindings, "Joystick up - Plan", CompanionConfig.AlwaysBank, 9, "plan-mode");
        AddBinding(bindings, "Joystick right - Forward", CompanionConfig.AlwaysBank, 10, "navigate-forward");
        AddBinding(bindings, "Joystick down - Sidebar", CompanionConfig.AlwaysBank, 11, "toggle-sidebar");
        AddBinding(bindings, "Joystick left - Back", CompanionConfig.AlwaysBank, 12, "navigate-back");

        return new StarterProfile(dialProfile, selectors, bindings);
    }

    private static void AddBank(
        List<ButtonBinding> bindings,
        string bank,
        int firstButton,
        IReadOnlyList<string> actions,
        string? labelPrefix = null)
    {
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            var label = labelPrefix is null
                ? $"{bank} B{index + 1} - {action}"
                : $"{labelPrefix} B{index + 1} - {action}";
            AddBinding(bindings, label, bank, firstButton + index, action);

            if (action == "push-to-talk")
            {
                AddBinding(
                    bindings,
                    $"{label} release",
                    bank,
                    firstButton + index,
                    action,
                    "release");
            }
        }
    }

    private static void AddBinding(
        List<ButtonBinding> bindings,
        string name,
        string bank,
        int button,
        string action,
        string trigger = "press")
    {
        bindings.Add(new ButtonBinding
        {
            Name = name,
            Bank = bank,
            Button = button,
            Trigger = trigger,
            Action = action,
        });
    }
}
