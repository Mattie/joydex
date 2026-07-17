using VirpilCodexPad.Core.Mapping;

namespace VirpilCodexPad.Windows.Actions;

internal static class RawInputCatalog
{
    private static readonly IReadOnlyDictionary<CodexAction, KeySequence> KeySequences =
        new Dictionary<CodexAction, KeySequence>
        {
            [CodexAction.Home] = Parse("Home"),
            [CodexAction.End] = Parse("End"),
        };

    public static bool TryGetKeySequence(CodexAction action, out KeySequence sequence) =>
        KeySequences.TryGetValue(action, out sequence!);

    public static int? GetMouseWheelDelta(CodexAction action, int wheelNotches) => action switch
    {
        CodexAction.ScrollUp => checked(120 * wheelNotches),
        CodexAction.ScrollDown => checked(-120 * wheelNotches),
        _ => null,
    };

    private static KeySequence Parse(string value)
    {
        if (!KeySequenceParser.TryParse(value, allowBareModifiers: false, out var sequence, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return sequence!;
    }
}
