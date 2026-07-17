using VirpilCodexPad.Core.Mapping;

namespace VirpilCodexPad.Windows.Actions;

public enum CodexBindingSource
{
    None,
    User,
    Default,
    Provisioned,
}

public enum CodexBindingSnapshotState
{
    Current,
    LastKnownGood,
    Unavailable,
}

public sealed record CodexBindingResolution(
    CodexAction Action,
    string CommandId,
    KeySequence? Sequence,
    CodexBindingSource Source,
    CodexBindingSnapshotState SnapshotState,
    string? Error)
{
    public bool Resolved => Sequence is not null && Error is null;
}

public interface ICodexKeybindingResolver
{
    /// <summary>Returns the current safe keyboard sequence for a command-backed action.</summary>
    Task<CodexBindingResolution> ResolveAsync(CodexAction action, CancellationToken cancellationToken);
}
