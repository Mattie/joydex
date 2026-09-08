using System.Text;
using System.Text.Json;
using Joydex.App;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class VoiceSessionArchiveTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        "joydex-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void WritesUtf8ConversationAndSessionLifecycleMetadata()
    {
        Directory.CreateDirectory(_workspace);
        var started = new DateTimeOffset(2026, 8, 30, 14, 15, 16, 123, TimeSpan.FromHours(-5));
        var archive = VoiceSessionArchive.Create(
            _workspace,
            Guid.NewGuid().ToString("D"),
            "project-1",
            "Joydex Voice",
            _ => { },
            started,
            "0123456789abcdef");

        archive.UpdateTranscript(CodexVoiceConversationKind.User, "What’s the time", final: false);
        archive.UpdateTranscript(CodexVoiceConversationKind.User, "What’s the time?", final: true);
        archive.UpdateTranscript(
            CodexVoiceConversationKind.Activity,
            "<realtime_delegation>hidden wrapper</realtime_delegation>",
            final: true);
        archive.UpdateTranscript(CodexVoiceConversationKind.Assistant, "It’s 2:15 — café.", final: true);
        archive.MarkConnected();
        archive.Complete("ended", "spoken hangup");

        var sessionDirectory = Path.Combine(
            VoiceSessionArchive.GetSessionsRoot(_workspace),
            "2026-08-30",
            "141516-123-01234567");
        var transcriptPath = Path.Combine(sessionDirectory, "transcript.md");
        var bytes = File.ReadAllBytes(transcriptPath);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var transcript = Encoding.UTF8.GetString(bytes);
        Assert.Contains("### You", transcript, StringComparison.Ordinal);
        Assert.Contains("What’s the time?", transcript, StringComparison.Ordinal);
        Assert.Contains("### Computer", transcript, StringComparison.Ordinal);
        Assert.Contains("It’s 2:15 — café.", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("realtime_delegation", transcript, StringComparison.Ordinal);

        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(sessionDirectory, "session.json")));
        Assert.Equal("project-1", metadata.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(Path.GetFullPath(_workspace), metadata.RootElement.GetProperty("workspacePath").GetString());
        Assert.Equal("ended", metadata.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("spoken hangup", metadata.RootElement.GetProperty("reason").GetString());
        Assert.Equal(Path.Combine(sessionDirectory, "audio"), archive.AudioDirectory);
    }

    [Fact]
    public void ArchiveFailureIsLoggedWithoutThrowingIntoVoiceSession()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_workspace)!);
        File.WriteAllText(_workspace, "not a directory");
        var messages = new List<string>();

        var archive = VoiceSessionArchive.Create(
            _workspace,
            Guid.NewGuid().ToString("D"),
            string.Empty,
            string.Empty,
            messages.Add);

        archive.UpdateTranscript(CodexVoiceConversationKind.User, "still live", final: true);
        archive.Complete("ended");
        Assert.Null(archive.AudioDirectory);
        Assert.Contains(messages, message => message.Contains("Could not create", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
        else if (File.Exists(_workspace))
        {
            File.Delete(_workspace);
        }
    }
}
