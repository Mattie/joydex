namespace Joydex.Tests;

public sealed class HookRelayTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("agent-session", true)]
    public void IdentifiesSubagentsFromCodexAgentId(string? agentId, bool expected)
    {
        var input = new HookInput(
            "UserPromptSubmit",
            "session",
            "turn",
            agentId,
            @"C:\transcripts\session.jsonl",
            null,
            default);

        Assert.Equal(expected, HookRelay.IsSubagent(input));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "", false)]
    [InlineData(null, @"C:\transcripts\session.jsonl", true)]
    [InlineData("agent-session", @"C:\transcripts\agent.jsonl", false)]
    public void TracksOnlyPersistentTopLevelSessions(
        string? agentId,
        string? transcriptPath,
        bool expected)
    {
        var input = new HookInput(
            "UserPromptSubmit",
            "session",
            "turn",
            agentId,
            transcriptPath,
            null,
            default);

        Assert.Equal(expected, HookRelay.IsTrackableSession(input));
    }
}
