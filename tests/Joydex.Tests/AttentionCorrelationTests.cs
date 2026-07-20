using System.Text.Json;
using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class AttentionCorrelationTests
{
    [Fact]
    public void CanonicalizesObjectPropertyOrder()
    {
        var first = Input("""{"command":"build","options":{"b":2,"a":1}}""");
        var second = Input("""{"options":{"a":1,"b":2},"command":"build"}""");

        Assert.Equal(
            AttentionCorrelation.Create("session", "turn", "Bash", first),
            AttentionCorrelation.Create("session", "turn", "Bash", second));
    }

    [Fact]
    public void BashDescriptionDoesNotPreventPermissionAndCompletionMatch()
    {
        var permission = Input("""{"command":"build","description":"Run the build"}""");
        var completion = Input("""{"command":"build"}""");

        Assert.Equal(
            AttentionCorrelation.Create("session", "turn", "Bash", permission),
            AttentionCorrelation.Create("session", "turn", "Bash", completion));
    }

    [Fact]
    public void DifferentTurnOrInputProducesDifferentKey()
    {
        var first = Input("""{"command":"build"}""");
        var second = Input("""{"command":"test"}""");

        var key = AttentionCorrelation.Create("session", "turn-1", "Bash", first);
        Assert.NotEqual(key, AttentionCorrelation.Create("session", "turn-2", "Bash", first));
        Assert.NotEqual(key, AttentionCorrelation.Create("session", "turn-1", "Bash", second));
    }

    [Fact]
    public void EmitsOnlyAHashAndRejectsMissingIdentity()
    {
        var input = Input("""{"command":"private command text"}""");

        var key = AttentionCorrelation.Create("session", "turn", "Bash", input);

        Assert.NotNull(key);
        Assert.Equal(64, key.Length);
        Assert.DoesNotContain("private", key, StringComparison.OrdinalIgnoreCase);
        Assert.Null(AttentionCorrelation.Create(null, "turn", "Bash", input));
        Assert.Null(AttentionCorrelation.Create("session", "turn", null, input));
        Assert.Null(AttentionCorrelation.Create("session", "turn", "Bash", default));
    }

    private static JsonElement Input(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
