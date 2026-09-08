using System.Text.Json;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class CodexVoiceConversationTests
{
    [Fact]
    public void ParsesDialogueAndGenericActivityWithoutToolPayloads()
    {
        var result = JsonSerializer.SerializeToElement(new
        {
            thread = new
            {
                turns = new object[]
                {
                    new
                    {
                        id = "turn-1",
                        startedAt = 1_700_000_000L,
                        completedAt = 1_700_000_005L,
                        items = new object[]
                        {
                            new
                            {
                                id = "user-1",
                                type = "userMessage",
                                content = new object[]
                                {
                                    new { type = "text", text = "What is the weather?" },
                                    new { type = "image", url = "data:secret" },
                                },
                            },
                            new
                            {
                                id = "tool-1",
                                type = "mcpToolCall",
                                status = "completed",
                                arguments = new { location = "private" },
                                result = "private output",
                            },
                            new
                            {
                                id = "assistant-1",
                                type = "agentMessage",
                                text = "It is sunny.",
                            },
                            new
                            {
                                id = "reasoning-1",
                                type = "reasoning",
                                content = new[] { "private reasoning" },
                            },
                        },
                    },
                },
            },
        });

        var entries = CodexVoiceConversationParser.ParseThreadRead(result);

        Assert.Collection(
            entries,
            entry =>
            {
                Assert.Equal(CodexVoiceConversationKind.User, entry.Kind);
                Assert.Equal("What is the weather?", entry.Text);
            },
            entry =>
            {
                Assert.Equal(CodexVoiceConversationKind.Activity, entry.Kind);
                Assert.Equal("Used a tool", entry.Text);
            },
            entry =>
            {
                Assert.Equal(CodexVoiceConversationKind.Assistant, entry.Kind);
                Assert.Equal("It is sunny.", entry.Text);
            });
        Assert.DoesNotContain(entries, entry => entry.Text.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreservesSteeredUserMessagesAndCoalescesAdjacentActivity()
    {
        var result = JsonSerializer.SerializeToElement(new
        {
            thread = new
            {
                id = "owned-task",
                turns = new object[]
                {
                    new
                    {
                        startedAt = 1_700_000_000L,
                        items = new object[]
                        {
                            User("user-1", "First request"),
                            new { id = "search-1", type = "webSearch", query = "private one" },
                            new { id = "search-2", type = "webSearch", query = "private two" },
                            User("user-2", "Changed request"),
                        },
                    },
                },
            },
        });

        var entries = CodexVoiceConversationParser.ParseThreadRead(result, "owned-task");

        Assert.Equal(
            ["First request", "Searched the web", "Changed request"],
            entries.Select(entry => entry.Text));
    }

    [Fact]
    public void RejectsHistoryForAnotherOwnedTask()
    {
        var result = JsonSerializer.SerializeToElement(new
        {
            thread = new
            {
                id = "another-task",
                turns = Array.Empty<object>(),
            },
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => CodexVoiceConversationParser.ParseThreadRead(result, "expected-task"));

        Assert.Contains("different task", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisplaysOnlyTheUtteranceFromRealtimeDelegationMetadata()
    {
        const string envelope = """
            <realtime_delegation>
              <input>What is the test value?</input>
              <transcript_delta>user: What is the test value?</transcript_delta>
            </realtime_delegation>
            """;
        var result = JsonSerializer.SerializeToElement(new
        {
            thread = new
            {
                turns = new[]
                {
                    new
                    {
                        items = new[] { User("user-1", envelope) },
                    },
                },
            },
        });

        var entry = Assert.Single(CodexVoiceConversationParser.ParseThreadRead(result));

        Assert.Equal("What is the test value?", entry.Text);
        Assert.Equal(envelope.Trim(), entry.RawText);
        Assert.DoesNotContain("transcript_delta", entry.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesOrdinaryUserTextContainingAngleBrackets()
    {
        var result = JsonSerializer.SerializeToElement(new
        {
            thread = new
            {
                turns = new[]
                {
                    new
                    {
                        items = new[] { User("user-1", "Explain <input> in HTML") },
                    },
                },
            },
        });

        var entry = Assert.Single(CodexVoiceConversationParser.ParseThreadRead(result));

        Assert.Equal("Explain <input> in HTML", entry.Text);
    }

    private static object User(string id, string text) => new
    {
        id,
        type = "userMessage",
        content = new[] { new { type = "text", text } },
    };
}
