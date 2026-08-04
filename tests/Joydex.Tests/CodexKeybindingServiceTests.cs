using System.Text.Json;
using Joydex.Core.Mapping;
using Joydex.Windows.Actions;

namespace Joydex.Tests;

public sealed class CodexKeybindingServiceTests
{
    [Fact]
    public async Task DefaultServiceConstructionUsesOneSystemWideUserPath()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Joydex",
            "codex-keybinding-provisioning.json");

        Assert.Equal(expected, CodexKeybindingService.DefaultProvisioningStatePath);
        await using var service = CodexKeybindingService.CreateDefault(_ => { }, existingCompanionInstall: true);
        Assert.Equal(CodexKeybindingService.DefaultKeybindingsPath, service.KeybindingsPath);
        Assert.Equal(expected, service.ProvisioningStatePath);
    }

    [Fact]
    public async Task UserBindingsReplaceDefaultsAndPreserveOrder()
    {
        var fixture = CreateFixture(
            Entry("approval.approve", "Ctrl+K"),
            Entry("approval.approve", "Ctrl+L"));
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.True(resolution.Resolved, resolution.Error);
        Assert.Equal("Ctrl+K", resolution.Sequence!.NormalizedText);
        Assert.Equal(CodexBindingSource.User, resolution.Source);
    }

    [Fact]
    public async Task ExplicitRemovalSuppressesTheDefault()
    {
        var fixture = CreateFixture(Entry("approval.approve", null));
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.Contains("explicitly unbound", resolution.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallsBackToTheVerifiedWindowsDefault()
    {
        var fixture = CreateFixture();
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.Equal("Enter", resolution.Sequence!.NormalizedText);
        Assert.Equal(CodexBindingSource.Default, resolution.Source);
    }

    [Fact]
    public async Task VoiceChatUsesTheVerifiedInWindowToggle()
    {
        var fixture = CreateFixture(
            Entry("realtimeVoice", "Ctrl+Alt+Shift+V"));
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.ToggleVoiceChat, CancellationToken.None);

        Assert.Equal("composer.startVoiceMode", resolution.CommandId);
        Assert.Equal("Ctrl+Shift+V", resolution.Sequence!.NormalizedText);
        Assert.Equal(CodexBindingSource.Default, resolution.Source);
    }

    [Fact]
    public async Task EndVoiceChatUsesTheConfiguredShortcut()
    {
        var fixture = CreateFixture(
            Entry("realtimeVoice.endCall", "Ctrl+Alt+Shift+G"));
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.EndVoiceChat, CancellationToken.None);

        Assert.Equal("realtimeVoice.endCall", resolution.CommandId);
        Assert.Equal("Ctrl+Alt+Shift+G", resolution.Sequence!.NormalizedText);
        Assert.Equal(CodexBindingSource.User, resolution.Source);
    }

    [Fact]
    public async Task NormalizesLegacyCommandAliases()
    {
        var fixture = CreateFixture(Entry("newThread", "Ctrl+Alt+N"));
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.NewTask, CancellationToken.None);

        Assert.Equal("Ctrl+Alt+N", resolution.Sequence!.NormalizedText);
        Assert.Equal("newTask", resolution.CommandId);
    }

    [Fact]
    public async Task SelectsTheFirstSupportedUserSequence()
    {
        var fixture = CreateFixture(
            Entry("approval.approve", "MouseBack"),
            Entry("approval.approve", "Ctrl+K Ctrl+Enter"));
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.Equal("Ctrl+K Ctrl+Enter", resolution.Sequence!.NormalizedText);
        Assert.Equal(2, resolution.Sequence.Chords.Count);
    }

    [Theory]
    [InlineData("when", "editorFocus")]
    [InlineData("when", "")]
    public async Task ConditionalBindingsAreNeverGuessed(string property, string value)
    {
        var conditional = new Dictionary<string, object?>
        {
            ["command"] = "approval.approve",
            ["key"] = "Ctrl+K",
            [property] = value,
        };
        var fixture = CreateFixture(conditional);
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        if (string.IsNullOrEmpty(value))
        {
            Assert.True(resolution.Resolved, resolution.Error);
        }
        else
        {
            Assert.False(resolution.Resolved);
            Assert.Contains("context-dependent", resolution.Error, StringComparison.Ordinal);
            Assert.Contains("Settings > Keyboard Shortcuts", resolution.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UnsupportedBindingHasActionableCodexSettingsGuidance()
    {
        var fixture = CreateFixture(Entry("approval.approve", "MouseBack"));
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.Contains("approval.approve", resolution.Error, StringComparison.Ordinal);
        Assert.Contains("Settings > Keyboard Shortcuts", resolution.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactAndPrefixCollisionsFailSafely()
    {
        var fixture = CreateFixture(
            Entry("approval.approve", "Ctrl+K Ctrl+Enter"),
            Entry("someOtherCommand", "Ctrl+K"));
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.Contains("conflicts", resolution.Error, StringComparison.Ordinal);
        Assert.Contains("someOtherCommand", resolution.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrefixCollisionsWithinOneCommandFailSafely()
    {
        var fixture = CreateFixture(
            Entry("approval.approve", "Ctrl+K Ctrl+Enter"),
            Entry("approval.approve", "Ctrl+K"));
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.Contains("prefix-conflicts", resolution.Error, StringComparison.Ordinal);
        Assert.Contains("approval.approve", resolution.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaterSafeBindingIsUsedWhenAnEarlierBindingCollides()
    {
        var fixture = CreateFixture(
            Entry("approval.approve", "Ctrl+K"),
            Entry("approval.approve", "Ctrl+L"),
            Entry("someOtherCommand", "Ctrl+K"));
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.True(resolution.Resolved, resolution.Error);
        Assert.Equal("Ctrl+L", resolution.Sequence!.NormalizedText);
    }

    [Fact]
    public async Task ConditionalCollisionsAlsoFailSafely()
    {
        var other = Entry("someOtherCommand", "Ctrl+K");
        other["when"] = "editorFocus";
        var fixture = CreateFixture(Entry("approval.approve", "Ctrl+K"), other);
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.Contains("conflicts", resolution.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserBindingCollidesWithAnotherCommandsEffectiveDefault()
    {
        var fixture = CreateFixture(Entry("approval.approve", "Ctrl+B"));
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.Contains("toggleSidebar", resolution.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovedOtherCommandDoesNotCreateAPhantomCollision()
    {
        var fixture = CreateFixture(
            Entry("approval.approve", "Ctrl+K"),
            Entry("someOtherCommand", "Ctrl+K"),
            Entry("someOtherCommand", null));
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.True(resolution.Resolved, resolution.Error);
        Assert.Equal("Ctrl+K", resolution.Sequence!.NormalizedText);
    }

    [Fact]
    public async Task ConditionalRemovalDoesNotHideAPossibleCollision()
    {
        var conditionalRemoval = Entry("someOtherCommand", null);
        conditionalRemoval["when"] = "editorFocus";
        var fixture = CreateFixture(
            Entry("approval.approve", "Ctrl+K"),
            Entry("someOtherCommand", "Ctrl+K"),
            conditionalRemoval);
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.Contains("someOtherCommand", resolution.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedStartupBlocksCommandsWithoutAValidSnapshot()
    {
        var fixture = CreateFixtureWithRawKeybindings("[");
        await using var service = await fixture.StartAsync();
        Assert.Equal([100, 250, 500, 1000], fixture.Timing.RequestedDelays);

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.Equal(CodexBindingSnapshotState.Unavailable, resolution.SnapshotState);
        Assert.Contains("could not be parsed", resolution.Error, StringComparison.Ordinal);
        Assert.Contains("No valid snapshot is available", resolution.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("last known-good snapshot was retained", resolution.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MetadataFailureAtStartupBlocksInsteadOfUsingDefaults()
    {
        var fixture = CreateFixture(Entry("approval.approve", "Ctrl+K"));
        fixture.FileSystem.StampException = new UnauthorizedAccessException("metadata unavailable");
        await using var service = await fixture.StartAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.Equal(CodexBindingSnapshotState.Unavailable, resolution.SnapshotState);
        Assert.Contains("could not be read", resolution.Error, StringComparison.Ordinal);
        Assert.Contains("No valid snapshot is available", resolution.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("last known-good snapshot was retained", resolution.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MetadataFailureRetainsTheLastKnownGoodSnapshot()
    {
        var fixture = CreateFixture(Entry("approval.approve", "Ctrl+K"));
        await using var service = await fixture.StartAsync();
        fixture.FileSystem.StampException = new IOException("metadata unavailable");

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.True(resolution.Resolved, resolution.Error);
        Assert.Equal("Ctrl+K", resolution.Sequence!.NormalizedText);
        Assert.Equal(CodexBindingSnapshotState.LastKnownGood, resolution.SnapshotState);
        Assert.Contains(
            fixture.Logs,
            message => message.Contains("last known-good snapshot was retained", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PartialWriteCanRecoverDuringTheSpecifiedRetrySchedule()
    {
        var fixture = CreateFixtureWithRawKeybindings("[");
        var reads = 0;
        fixture.FileSystem.BeforeRead = path =>
        {
            if (string.Equals(path, fixture.KeybindingsPath, StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref reads) == 2)
            {
                fixture.FileSystem.SetFile(
                    fixture.KeybindingsPath,
                    SerializeBindings(Scaffolding().Append(Entry("approval.approve", "Ctrl+K"))),
                    notify: false);
            }
        };

        await using var service = await fixture.StartAsync();
        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.True(resolution.Resolved, resolution.Error);
        Assert.Equal("Ctrl+K", resolution.Sequence!.NormalizedText);
        Assert.Equal([100, 250], fixture.Timing.RequestedDelays);
    }

    [Fact]
    public async Task MalformedWritesRetainLastKnownGoodAndRecoverOnNextDispatch()
    {
        var fixture = CreateFixture(Entry("approval.approve", "Ctrl+K"));
        await using var service = await fixture.StartAsync();
        fixture.FileSystem.SetFile(fixture.KeybindingsPath, "[", notify: false);

        var retained = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.Equal("Ctrl+K", retained.Sequence!.NormalizedText);
        Assert.Equal(CodexBindingSnapshotState.LastKnownGood, retained.SnapshotState);

        fixture.FileSystem.SetFile(
            fixture.KeybindingsPath,
            SerializeBindings(Scaffolding().Append(Entry("approval.approve", "Ctrl+L"))),
            notify: false);
        var recovered = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.Equal("Ctrl+L", recovered.Sequence!.NormalizedText);
        Assert.Equal(CodexBindingSnapshotState.Current, recovered.SnapshotState);
    }

    [Fact]
    public async Task EmptyPartialWriteAlsoRetainsLastKnownGood()
    {
        var fixture = CreateFixture(Entry("approval.approve", "Ctrl+K"));
        await using var service = await fixture.StartAsync();
        fixture.FileSystem.SetFile(fixture.KeybindingsPath, string.Empty, notify: false);

        var retained = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.Equal("Ctrl+K", retained.Sequence!.NormalizedText);
        Assert.Equal(CodexBindingSnapshotState.LastKnownGood, retained.SnapshotState);
    }

    [Fact]
    public async Task DeferredProvisioningRunsAfterMalformedStartupRecovers()
    {
        var fixture = CreateFixtureWithRawKeybindings("[");
        await using var service = await fixture.StartAsync(existingCompanionInstall: false);
        fixture.FileSystem.SetFile(fixture.KeybindingsPath, "[]");
        await fixture.Timing.RunScheduledAsync();

        var resolution = await service.ResolveAsync(CodexAction.OpenSkills, CancellationToken.None);

        Assert.True(resolution.Resolved, resolution.Error);
        Assert.Equal(CodexBindingSource.Provisioned, resolution.Source);
    }

    [Fact]
    public async Task FileEventsAreDebouncedAndReloadWithoutRestart()
    {
        var fixture = CreateFixture(Entry("approval.approve", "Ctrl+K"));
        await using var service = await fixture.StartAsync();

        fixture.FileSystem.SetFile(
            fixture.KeybindingsPath,
            SerializeBindings(Scaffolding().Append(Entry("approval.approve", "Ctrl+L"))));
        fixture.FileSystem.SetFile(
            fixture.KeybindingsPath,
            SerializeBindings(Scaffolding().Append(Entry("approval.approve", "Ctrl+M"))));

        Assert.Equal(1, fixture.Timing.ActiveScheduledCount);
        Assert.Equal(250, fixture.Timing.ActiveScheduledDelay);
        await fixture.Timing.RunScheduledAsync();
        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.Equal("Ctrl+M", resolution.Sequence!.NormalizedText);
    }

    [Fact]
    public async Task StableDeletionBecomesAnEmptyUserKeymap()
    {
        var fixture = CreateFixture(Entry("approval.approve", "Ctrl+K"));
        await using var service = await fixture.StartAsync();
        fixture.FileSystem.DeleteKeybindings(fixture.KeybindingsPath);
        await fixture.Timing.RunScheduledAsync();

        var resolution = await service.ResolveAsync(CodexAction.Approve, CancellationToken.None);

        Assert.Equal("Enter", resolution.Sequence!.NormalizedText);
        Assert.Equal(CodexBindingSource.Default, resolution.Source);
    }

    [Fact]
    public async Task ProvisioningPreservesEntriesAndRunsOnlyOnce()
    {
        var fixture = CreateFixtureForProvisioning(Entry("unrelated.command", "Ctrl+U"));
        await using (var first = await fixture.StartAsync(existingCompanionInstall: false))
        {
            var skills = await first.ResolveAsync(CodexAction.OpenSkills, CancellationToken.None);
            Assert.Equal(CodexBindingSource.Provisioned, skills.Source);
        }

        var firstContents = fixture.FileSystem.ReadAllText(fixture.KeybindingsPath);
        using (var document = JsonDocument.Parse(firstContents))
        {
            var entries = document.RootElement.EnumerateArray().ToArray();
            Assert.Equal("unrelated.command", entries[0].GetProperty("command").GetString());
            Assert.Single(entries, entry => entry.GetProperty("command").GetString() == "openSkills");
        }

        Assert.Contains(
            fixture.FileSystem.Paths,
            path => path.Contains("keybindings.json.joydex-", StringComparison.Ordinal));
        await using (var second = await fixture.StartAsync(existingCompanionInstall: false))
        {
            Assert.True((await second.ResolveAsync(CodexAction.OpenSkills, CancellationToken.None)).Resolved);
        }

        Assert.Equal(firstContents, fixture.FileSystem.ReadAllText(fixture.KeybindingsPath));
    }

    [Fact]
    public async Task DeletedProvisionedBindingIsNotRecreated()
    {
        var fixture = CreateFixtureForProvisioning();
        await using (var first = await fixture.StartAsync(existingCompanionInstall: false))
        {
        }

        var remaining = ParseBindings(fixture.FileSystem.ReadAllText(fixture.KeybindingsPath))
            .Where(entry => !string.Equals(Convert.ToString(entry["command"]), "openSkills", StringComparison.Ordinal))
            .ToArray();
        fixture.FileSystem.SetFile(fixture.KeybindingsPath, SerializeBindings(remaining), notify: false);

        await using var second = await fixture.StartAsync(existingCompanionInstall: false);
        var resolution = await second.ResolveAsync(CodexAction.OpenSkills, CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.DoesNotContain(
            ParseBindings(fixture.FileSystem.ReadAllText(fixture.KeybindingsPath)),
            entry => Convert.ToString(entry["command"]) == "openSkills");
    }

    [Fact]
    public async Task ChangedHistoricalBindingIsUserOwnedAndNeverRewritten()
    {
        var fixture = CreateFixture(Entry("composer.toggleFastMode", "Ctrl+Q"));
        var before = fixture.FileSystem.ReadAllText(fixture.KeybindingsPath);
        await using var service = await fixture.StartAsync(existingCompanionInstall: true);

        var resolution = await service.ResolveAsync(CodexAction.ToggleFastMode, CancellationToken.None);

        Assert.Equal("Ctrl+Q", resolution.Sequence!.NormalizedText);
        Assert.Equal(CodexBindingSource.User, resolution.Source);
        Assert.Equal(before, fixture.FileSystem.ReadAllText(fixture.KeybindingsPath));
    }

    [Fact]
    public async Task OneHistoricalInstallerBindingProtectsDeletedHistoricalBindingsDuringMigration()
    {
        var fixture = CreateFixtureForProvisioning(
            Entry("composer.toggleFastMode", "Ctrl+Alt+Shift+F7"));
        await using var service = await fixture.StartAsync(existingCompanionInstall: false);

        var saved = ParseBindings(fixture.FileSystem.ReadAllText(fixture.KeybindingsPath));

        Assert.DoesNotContain(saved, entry => Convert.ToString(entry["command"]) == "forkThread");
        Assert.DoesNotContain(saved, entry => Convert.ToString(entry["command"]) == "composer.submit");
        Assert.DoesNotContain(saved, entry => Convert.ToString(entry["command"]) == "composer.togglePlanMode");
        Assert.Contains(saved, entry =>
            Convert.ToString(entry["command"]) == "composer.toggleFastMode"
            && Convert.ToString(entry["key"]) == "Ctrl+Alt+Shift+F7");
    }

    [Fact]
    public async Task ProvisioningConflictIsTerminalAndDoesNotRemoveTheConflictingEntry()
    {
        var fixture = CreateFixtureForProvisioning(Entry("unrelated.command", "Ctrl+Alt+Shift+S"));
        await using (var first = await fixture.StartAsync(existingCompanionInstall: false))
        {
            Assert.False((await first.ResolveAsync(CodexAction.OpenSkills, CancellationToken.None)).Resolved);
        }

        var afterFirstRun = fixture.FileSystem.ReadAllText(fixture.KeybindingsPath);
        await using (var second = await fixture.StartAsync(existingCompanionInstall: false))
        {
            Assert.False((await second.ResolveAsync(CodexAction.OpenSkills, CancellationToken.None)).Resolved);
        }

        Assert.Equal(afterFirstRun, fixture.FileSystem.ReadAllText(fixture.KeybindingsPath));
        Assert.Contains("unrelated.command", afterFirstRun, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentCodexEditAbortsProvisioningWithoutReplacingTheEdit()
    {
        var fixture = CreateFixtureForProvisioning();
        var keybindingReads = 0;
        fixture.FileSystem.BeforeRead = path =>
        {
            if (!string.Equals(path, fixture.KeybindingsPath, StringComparison.OrdinalIgnoreCase)
                || Interlocked.Increment(ref keybindingReads) != 2)
            {
                return;
            }

            fixture.FileSystem.SetFile(
                fixture.KeybindingsPath,
                SerializeBindings([Entry("openSkills", null)]),
                notify: false);
        };

        await using var service = await fixture.StartAsync(existingCompanionInstall: false);
        var resolution = await service.ResolveAsync(CodexAction.OpenSkills, CancellationToken.None);

        Assert.False(resolution.Resolved);
        var saved = Assert.Single(ParseBindings(fixture.FileSystem.ReadAllText(fixture.KeybindingsPath)));
        Assert.Equal("openSkills", Convert.ToString(saved["command"]));
        Assert.Null(saved["key"]);
    }

    private static ServiceFixture CreateFixture(params Dictionary<string, object?>[] entries) =>
        CreateFixtureWithRawKeybindings(SerializeBindings(Scaffolding().Concat(entries)));

    private static ServiceFixture CreateFixtureForProvisioning(params Dictionary<string, object?>[] entries) =>
        CreateFixtureWithRawKeybindings(SerializeBindings(entries));

    private static ServiceFixture CreateFixtureWithRawKeybindings(string contents)
    {
        var root = Path.Combine(Path.GetTempPath(), "JoydexTests", Guid.NewGuid().ToString("N"));
        var keybindingsPath = Path.Combine(root, ".codex", "keybindings.json");
        var statePath = Path.Combine(root, "local", "provisioning.json");
        var fileSystem = new MemoryFileSystem();
        fileSystem.SetFile(keybindingsPath, contents, notify: false);
        return new ServiceFixture(keybindingsPath, statePath, fileSystem, new ManualTiming());
    }

    private static IEnumerable<Dictionary<string, object?>> Scaffolding()
    {
        yield return Entry("globalDictationHold", "Ctrl+CapsLock");
        yield return Entry("openSkills", "Ctrl+Alt+Shift+S");
    }

    private static Dictionary<string, object?> Entry(string command, string? key) => new()
    {
        ["command"] = command,
        ["key"] = key,
    };

    private static string SerializeBindings(IEnumerable<Dictionary<string, object?>> entries) =>
        JsonSerializer.Serialize(entries);

    private static Dictionary<string, object?>[] ParseBindings(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>[]>(json)!;

    private sealed record ServiceFixture(
        string KeybindingsPath,
        string StatePath,
        MemoryFileSystem FileSystem,
        ManualTiming Timing)
    {
        public List<string> Logs { get; } = [];

        public async Task<CodexKeybindingService> StartAsync(bool existingCompanionInstall = true)
        {
            var service = new CodexKeybindingService(
                KeybindingsPath,
                StatePath,
                Logs.Add,
                existingCompanionInstall,
                FileSystem,
                Timing);
            await service.InitializeAsync();
            return service;
        }
    }

    private sealed class ManualTiming : ICodexKeybindingTiming
    {
        private readonly List<Scheduled> _scheduled = [];

        public int ActiveScheduledCount => _scheduled.Count(item => !item.Disposed);

        public int? ActiveScheduledDelay => _scheduled.LastOrDefault(item => !item.Disposed)?.Milliseconds;

        public List<int> RequestedDelays { get; } = [];

        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedDelays.Add(milliseconds);
            return Task.CompletedTask;
        }

        public IDisposable ScheduleOnce(int milliseconds, Func<Task> callback)
        {
            var scheduled = new Scheduled(milliseconds, callback);
            _scheduled.Add(scheduled);
            return scheduled;
        }

        public async Task RunScheduledAsync()
        {
            var scheduled = _scheduled.LastOrDefault(item => !item.Disposed)
                ?? throw new InvalidOperationException("No reload is scheduled.");
            scheduled.Disposed = true;
            await scheduled.Callback();
        }

        private sealed class Scheduled(int milliseconds, Func<Task> callback) : IDisposable
        {
            public int Milliseconds { get; } = milliseconds;

            public Func<Task> Callback { get; } = callback;

            public bool Disposed { get; set; }

            public void Dispose() => Disposed = true;
        }
    }

    private sealed class MemoryFileSystem : ICodexKeybindingFileSystem
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, FileValue> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Action>> _watchers = new(StringComparer.OrdinalIgnoreCase);
        private long _version;

        public Action<string>? BeforeRead { get; set; }

        public Exception? StampException { get; set; }

        public IEnumerable<string> Paths
        {
            get
            {
                lock (_lock)
                {
                    return _files.Keys.ToArray();
                }
            }
        }

        public void SetFile(string path, string contents, bool notify = true)
        {
            Action[] callbacks;
            lock (_lock)
            {
                _files[Normalize(path)] = NewValue(contents);
                callbacks = GetCallbacks(path, notify);
            }

            foreach (var callback in callbacks)
            {
                callback();
            }
        }

        public void DeleteKeybindings(string path)
        {
            Action[] callbacks;
            lock (_lock)
            {
                _files.Remove(Normalize(path));
                _version++;
                callbacks = GetCallbacks(path, notify: true);
            }

            foreach (var callback in callbacks)
            {
                callback();
            }
        }

        public void CreateDirectory(string path)
        {
        }

        public bool FileExists(string path)
        {
            lock (_lock)
            {
                return _files.ContainsKey(Normalize(path));
            }
        }

        public string ReadAllText(string path)
        {
            BeforeRead?.Invoke(path);
            lock (_lock)
            {
                return Get(path).Contents;
            }
        }

        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadAllText(path));
        }

        public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetFile(path, contents, notify: false);
            return Task.CompletedTask;
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            Action[] callbacks;
            lock (_lock)
            {
                var source = Get(sourcePath);
                if (!overwrite && _files.ContainsKey(Normalize(destinationPath)))
                {
                    throw new IOException("The destination exists.");
                }

                _files.Remove(Normalize(sourcePath));
                _files[Normalize(destinationPath)] = NewValue(source.Contents);
                callbacks = GetCallbacks(destinationPath, notify: true);
            }

            foreach (var callback in callbacks)
            {
                callback();
            }
        }

        public void DeleteFile(string path)
        {
            lock (_lock)
            {
                _files.Remove(Normalize(path));
            }
        }

        public void CopyFile(string sourcePath, string destinationPath)
        {
            lock (_lock)
            {
                if (_files.ContainsKey(Normalize(destinationPath)))
                {
                    throw new IOException("The destination exists.");
                }

                _files[Normalize(destinationPath)] = NewValue(Get(sourcePath).Contents);
            }
        }

        public CodexKeybindingFileStamp GetFileStamp(string path)
        {
            if (StampException is not null)
            {
                throw StampException;
            }

            lock (_lock)
            {
                return _files.TryGetValue(Normalize(path), out var value)
                    ? value.Stamp
                    : CodexKeybindingFileStamp.Missing;
            }
        }

        public IDisposable WatchFile(string path, Action changed)
        {
            var normalized = Normalize(path);
            lock (_lock)
            {
                if (!_watchers.TryGetValue(normalized, out var callbacks))
                {
                    callbacks = [];
                    _watchers[normalized] = callbacks;
                }

                callbacks.Add(changed);
            }

            return new CallbackRegistration(() =>
            {
                lock (_lock)
                {
                    _watchers.GetValueOrDefault(normalized)?.Remove(changed);
                }
            });
        }

        private Action[] GetCallbacks(string path, bool notify) =>
            notify && _watchers.TryGetValue(Normalize(path), out var callbacks)
                ? callbacks.ToArray()
                : [];

        private FileValue Get(string path) =>
            _files.TryGetValue(Normalize(path), out var value)
                ? value
                : throw new FileNotFoundException("The in-memory file does not exist.", path);

        private FileValue NewValue(string contents)
        {
            var version = ++_version;
            return new FileValue(
                contents,
                new CodexKeybindingFileStamp(
                    true,
                    DateTime.UnixEpoch.AddTicks(version),
                    System.Text.Encoding.UTF8.GetByteCount(contents)));
        }

        private static string Normalize(string path) => Path.GetFullPath(path);

        private sealed record FileValue(string Contents, CodexKeybindingFileStamp Stamp);

        private sealed class CallbackRegistration(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
