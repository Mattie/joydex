namespace VirpilCodexPad.Core.Mapping;

public enum CodexAction
{
    Agent1,
    Agent2,
    Agent3,
    Agent4,
    Agent5,
    Agent6,
    ToggleFastMode,
    Approve,
    Reject,
    ForkTask,
    PushToTalk,
    Submit,
    TogglePlanMode,
    IncreaseReasoning,
    DecreaseReasoning,
    ScrollUp,
    ScrollDown,
    Home,
    End,
    ButtonMap,
    NewTask,
    PreviousTask,
    NextTask,
    NavigateBack,
    NavigateForward,
    ToggleSidebar,
    OpenSkills,
    Dictation,
    OpenWorkingDirectory,
}

public static class CodexActionCatalog
{
    private static readonly IReadOnlyDictionary<string, CodexAction> Actions =
        new Dictionary<string, CodexAction>(StringComparer.OrdinalIgnoreCase)
        {
            ["agent-1"] = CodexAction.Agent1,
            ["agent-2"] = CodexAction.Agent2,
            ["agent-3"] = CodexAction.Agent3,
            ["agent-4"] = CodexAction.Agent4,
            ["agent-5"] = CodexAction.Agent5,
            ["agent-6"] = CodexAction.Agent6,
            ["fast-mode"] = CodexAction.ToggleFastMode,
            ["approve"] = CodexAction.Approve,
            ["reject"] = CodexAction.Reject,
            ["fork-task"] = CodexAction.ForkTask,
            ["push-to-talk"] = CodexAction.PushToTalk,
            ["submit"] = CodexAction.Submit,
            ["plan-mode"] = CodexAction.TogglePlanMode,
            ["reasoning-up"] = CodexAction.IncreaseReasoning,
            ["reasoning-down"] = CodexAction.DecreaseReasoning,
            ["scroll-up"] = CodexAction.ScrollUp,
            ["scroll-down"] = CodexAction.ScrollDown,
            ["home"] = CodexAction.Home,
            ["end"] = CodexAction.End,
            ["button-map"] = CodexAction.ButtonMap,
            ["new-task"] = CodexAction.NewTask,
            ["previous-task"] = CodexAction.PreviousTask,
            ["next-task"] = CodexAction.NextTask,
            ["navigate-back"] = CodexAction.NavigateBack,
            ["navigate-forward"] = CodexAction.NavigateForward,
            ["toggle-sidebar"] = CodexAction.ToggleSidebar,
            ["open-skills"] = CodexAction.OpenSkills,
            ["dictation"] = CodexAction.Dictation,
            ["open"] = CodexAction.OpenWorkingDirectory,
        };

    public static IEnumerable<string> SupportedIds => Actions.Keys.Order(StringComparer.OrdinalIgnoreCase);

    public static bool TryParse(string? id, out CodexAction action) =>
        Actions.TryGetValue(id ?? string.Empty, out action);

    public static string GetId(CodexAction action) =>
        Actions.First(pair => pair.Value == action).Key;
}
