using Joydex.Core.Mapping;

namespace Joydex.Windows.Actions;

internal sealed record CodexCommandDescriptor(
    CodexAction Action,
    string CommandId,
    IReadOnlyList<string> DefaultBindings,
    string? ProvisionedBinding = null,
    bool AllowsBareModifiers = false);

internal static class CodexCommandCatalog
{
    private static readonly IReadOnlyDictionary<CodexAction, CodexCommandDescriptor> Commands =
        CreateCommands().ToDictionary(descriptor => descriptor.Action);

    public static IReadOnlyList<CodexCommandDescriptor> All { get; } = Commands.Values.ToArray();

    public static bool TryGet(CodexAction action, out CodexCommandDescriptor descriptor) =>
        Commands.TryGetValue(action, out descriptor!);

    public static string NormalizeCommandId(string commandId) => commandId switch
    {
        "newThread" => "newTask",
        "toggleSidePanelFullWidth" => "toggleMaximizeSidePanel",
        _ => commandId,
    };

    public static IReadOnlyDictionary<string, (string Current, string Legacy)> HistoricalBindings { get; } =
        new Dictionary<string, (string Current, string Legacy)>(StringComparer.OrdinalIgnoreCase)
        {
            ["thread1"] = ("Ctrl+Alt+Shift+F1", "F13"),
            ["thread2"] = ("Ctrl+Alt+Shift+F2", "F14"),
            ["thread3"] = ("Ctrl+Alt+Shift+F3", "F15"),
            ["thread4"] = ("Ctrl+Alt+Shift+F4", "F16"),
            ["thread5"] = ("Ctrl+Alt+Shift+F5", "F17"),
            ["thread6"] = ("Ctrl+Alt+Shift+F6", "F18"),
            ["composer.toggleFastMode"] = ("Ctrl+Alt+Shift+F7", "F19"),
            ["approval.approve"] = ("Ctrl+Alt+Shift+F8", "F20"),
            ["approval.decline"] = ("Ctrl+Alt+Shift+F9", "F21"),
            ["forkThread"] = ("Ctrl+Alt+Shift+F10", "F22"),
            ["composer.submit"] = ("Ctrl+Alt+Shift+F11", "F23"),
            ["composer.togglePlanMode"] = ("Ctrl+Alt+Shift+F12", "F24"),
            ["composer.increaseReasoningEffort"] = ("Ctrl+Alt+PageUp", "Ctrl+Alt+PageUp"),
            ["composer.decreaseReasoningEffort"] = ("Ctrl+Alt+PageDown", "Ctrl+Alt+PageDown"),
        };

    private static IEnumerable<CodexCommandDescriptor> CreateCommands()
    {
        yield return new(CodexAction.Agent1, "thread1", ["Ctrl+1"]);
        yield return new(CodexAction.Agent2, "thread2", ["Ctrl+2"]);
        yield return new(CodexAction.Agent3, "thread3", ["Ctrl+3"]);
        yield return new(CodexAction.Agent4, "thread4", ["Ctrl+4"]);
        yield return new(CodexAction.Agent5, "thread5", ["Ctrl+5"]);
        yield return new(CodexAction.Agent6, "thread6", ["Ctrl+6"]);
        yield return new(CodexAction.ToggleFastMode, "composer.toggleFastMode", [], "Ctrl+Alt+Shift+F7");
        yield return new(CodexAction.Approve, "approval.approve", ["Enter"]);
        yield return new(CodexAction.Reject, "approval.decline", ["Escape"]);
        yield return new(CodexAction.ForkTask, "forkThread", [], "Ctrl+Alt+Shift+F10");
        yield return new(
            CodexAction.PushToTalk,
            "globalDictationHold",
            [],
            "Ctrl+CapsLock",
            AllowsBareModifiers: true);
        yield return new(CodexAction.Submit, "composer.submit", [], "Ctrl+Alt+Shift+F11");
        yield return new(CodexAction.TogglePlanMode, "composer.togglePlanMode", [], "Ctrl+Alt+Shift+F12");
        yield return new(
            CodexAction.IncreaseReasoning,
            "composer.increaseReasoningEffort",
            [],
            "Ctrl+Alt+PageUp");
        yield return new(
            CodexAction.DecreaseReasoning,
            "composer.decreaseReasoningEffort",
            [],
            "Ctrl+Alt+PageDown");
        yield return new(CodexAction.NewTask, "newTask", ["Ctrl+N", "Ctrl+Shift+O"]);
        yield return new(CodexAction.SideConversation, "openSideChat", []);
        yield return new(CodexAction.PreviousTask, "previousThread", ["Ctrl+Shift+[", "Ctrl+PageUp"]);
        yield return new(CodexAction.NextTask, "nextThread", ["Ctrl+Shift+]", "Ctrl+PageDown"]);
        yield return new(CodexAction.NavigateBack, "navigateBack", ["Ctrl+[", "MouseBack"]);
        yield return new(CodexAction.NavigateForward, "navigateForward", ["Ctrl+]", "MouseForward"]);
        yield return new(CodexAction.ToggleSidebar, "toggleSidebar", ["Ctrl+B"]);
        yield return new(CodexAction.OpenSkills, "openSkills", [], "Ctrl+Alt+Shift+S");
        yield return new(CodexAction.ToggleVoiceChat, "composer.startVoiceMode", ["Ctrl+Shift+V"]);
        yield return new(CodexAction.Dictation, "composer.startDictation", ["Ctrl+Shift+D"]);
        yield return new(CodexAction.OpenWorkingDirectory, "copyWorkingDirectory", ["Ctrl+Shift+C"]);
    }
}
