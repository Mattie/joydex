using System.Text.Json;
using System.Text.Json.Nodes;
using VirpilCodexPad.Core.Mapping;

namespace VirpilCodexPad.Windows.Actions;

/// <summary>
/// Resolves Codex commands from the user's keybindings file and keeps a live,
/// last-known-good snapshot for the tray application's lifetime.
/// </summary>
public sealed class CodexKeybindingService : ICodexKeybindingResolver, IAsyncDisposable
{
    private static readonly int[] RetryDelaysMs = [0, 100, 250, 500, 1000];
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _keybindingsPath;
    private readonly string _provisioningStatePath;
    private readonly Action<string> _log;
    private readonly bool _existingCompanionInstall;
    private readonly ICodexKeybindingFileSystem _fileSystem;
    private readonly ICodexKeybindingTiming _timing;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly SemaphoreSlim _provisioningLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _timerLock = new();
    private KeybindingSnapshot _snapshot = KeybindingSnapshot.Unavailable;
    private Dictionary<string, ProvisioningRecord> _provisioning = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _watcher;
    private IDisposable? _debounceTimer;
    private int _lastReloadFailed;
    private int _provisioningDeferred;
    private bool _disposed;

    public CodexKeybindingService(
        string keybindingsPath,
        string provisioningStatePath,
        Action<string> log,
        bool existingCompanionInstall)
        : this(
            keybindingsPath,
            provisioningStatePath,
            log,
            existingCompanionInstall,
            new SystemCodexKeybindingFileSystem(),
            new SystemCodexKeybindingTiming())
    {
    }

    internal CodexKeybindingService(
        string keybindingsPath,
        string provisioningStatePath,
        Action<string> log,
        bool existingCompanionInstall,
        ICodexKeybindingFileSystem fileSystem,
        ICodexKeybindingTiming timing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keybindingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(provisioningStatePath);
        ArgumentNullException.ThrowIfNull(log);

        _keybindingsPath = Path.GetFullPath(keybindingsPath);
        _provisioningStatePath = Path.GetFullPath(provisioningStatePath);
        _log = log;
        _existingCompanionInstall = existingCompanionInstall;
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
    }

    public static string DefaultKeybindingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "keybindings.json");

    public static string DefaultProvisioningStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VirpilCodexPad",
        "codex-keybinding-provisioning.json");

    internal string KeybindingsPath => _keybindingsPath;

    internal string ProvisioningStatePath => _provisioningStatePath;

    public static CodexKeybindingService CreateDefault(
        Action<string> log,
        bool existingCompanionInstall) => new(
            DefaultKeybindingsPath,
            DefaultProvisioningStatePath,
            log,
            existingCompanionInstall);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _fileSystem.CreateDirectory(
            Path.GetDirectoryName(_keybindingsPath)
                ?? throw new InvalidOperationException("The Codex keybindings path has no parent directory."));

        await ReloadWithRetriesAsync(cancellationToken).ConfigureAwait(false);
        await InitializeProvisioningAsync(cancellationToken).ConfigureAwait(false);
        StartWatcher();
    }

    public async Task<CodexBindingResolution> ResolveAsync(
        CodexAction action,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!CodexCommandCatalog.TryGet(action, out var descriptor))
        {
            return Failure(
                action,
                string.Empty,
                CodexBindingSnapshotState.Unavailable,
                "This action is not backed by a Codex command.");
        }

        await RefreshIfChangedAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _provisioningDeferred) != 0)
        {
            await InitializeProvisioningAsync(cancellationToken).ConfigureAwait(false);
        }

        var snapshot = Volatile.Read(ref _snapshot);
        var snapshotState = GetSnapshotState(snapshot);
        if (!snapshot.Valid)
        {
            return Failure(
                action,
                descriptor.CommandId,
                snapshotState,
                snapshot.Error
                    ?? "Codex keybindings are unavailable. Open Settings > Keyboard Shortcuts and save the file again.");
        }

        var explicitEntries = snapshot.Entries
            .Where(entry => string.Equals(entry.CommandId, descriptor.CommandId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (explicitEntries.Any(entry => entry.Conditional))
        {
            return Failure(
                action,
                descriptor.CommandId,
                snapshotState,
                "The command has a context-dependent binding, so the companion cannot choose it safely.");
        }

        if (explicitEntries.Any(entry => entry.Key is null))
        {
            return Failure(
                action,
                descriptor.CommandId,
                snapshotState,
                $"The command is explicitly unbound. Assign '{descriptor.CommandId}' in Settings > Keyboard Shortcuts.");
        }

        var candidates = explicitEntries.Length > 0
            ? explicitEntries.Select(entry => entry.Key!).ToArray()
            : descriptor.DefaultBindings;
        string? firstParseError = null;
        string? firstCollisionError = null;
        foreach (var candidate in candidates)
        {
            if (!KeySequenceParser.TryParse(
                    candidate,
                    descriptor.AllowsBareModifiers,
                    out var sequence,
                    out var parseError))
            {
                firstParseError ??= parseError;
                continue;
            }

            var sameCommandCollision = FindSameCommandPrefixCollision(
                explicitEntries,
                candidate,
                sequence!,
                descriptor.AllowsBareModifiers);
            if (sameCommandCollision is not null)
            {
                firstCollisionError ??=
                    $"The binding '{sequence!.NormalizedText}' prefix-conflicts with another binding "
                    + $"for '{descriptor.CommandId}' ('{sameCommandCollision}'). Resolve it in Settings > Keyboard Shortcuts.";
                continue;
            }

            var collision = FindCollision(snapshot, descriptor.CommandId, sequence!);
            if (collision is not null)
            {
                firstCollisionError ??=
                    $"The binding '{sequence!.NormalizedText}' conflicts with '{collision}'. Resolve it in Settings > Keyboard Shortcuts.";
                continue;
            }

            var source = explicitEntries.Length > 0
                ? GetExplicitSource(descriptor.CommandId, candidate)
                : CodexBindingSource.Default;
            return new CodexBindingResolution(
                action,
                descriptor.CommandId,
                sequence,
                source,
                snapshotState,
                null);
        }

        var error = candidates.Count == 0
            ? $"No reliable binding is available. Assign '{descriptor.CommandId}' in Settings > Keyboard Shortcuts."
            : firstCollisionError
                ?? firstParseError
                ?? $"No supported binding is available. Assign '{descriptor.CommandId}' in Settings > Keyboard Shortcuts.";
        return Failure(action, descriptor.CommandId, snapshotState, error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCancellation.Cancel();
        _watcher?.Dispose();
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        await _reloadLock.WaitAsync().ConfigureAwait(false);
        _reloadLock.Release();
        await _provisioningLock.WaitAsync().ConfigureAwait(false);
        _provisioningLock.Release();
        _reloadLock.Dispose();
        _provisioningLock.Dispose();
        _disposeCancellation.Dispose();
    }

    private async Task InitializeProvisioningAsync(CancellationToken cancellationToken)
    {
        await _provisioningLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryLoadProvisioningState(out var stateError))
            {
                Interlocked.Exchange(ref _provisioningDeferred, 0);
                _log($"Codex shortcut provisioning is disabled: {stateError}");
                return;
            }

            var snapshot = Volatile.Read(ref _snapshot);
            if (!snapshot.Valid)
            {
                Interlocked.Exchange(ref _provisioningDeferred, 1);
                _log("Codex shortcut provisioning is deferred because keybindings.json has no valid snapshot.");
                return;
            }

            Interlocked.Exchange(ref _provisioningDeferred, 0);

            var stateChanged = RecordHistoricalBindings(snapshot);
            var pending = new List<(CodexCommandDescriptor Descriptor, KeySequence Sequence)>();
            foreach (var descriptor in CodexCommandCatalog.All.Where(candidate => candidate.ProvisionedBinding is not null))
            {
                if (_provisioning.ContainsKey(descriptor.CommandId))
                {
                    continue;
                }

                var explicitEntries = snapshot.Entries
                    .Where(entry => string.Equals(entry.CommandId, descriptor.CommandId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (explicitEntries.Length > 0)
                {
                    _provisioning[descriptor.CommandId] = new ProvisioningRecord
                    {
                        Status = explicitEntries.Any(entry => entry.Key is null) ? "removed" : "user",
                        Key = explicitEntries.FirstOrDefault(entry => entry.Key is not null)?.Key,
                    };
                    stateChanged = true;
                    continue;
                }

                if (!KeySequenceParser.TryParse(
                        descriptor.ProvisionedBinding,
                        descriptor.AllowsBareModifiers,
                        out var sequence,
                        out var error))
                {
                    _provisioning[descriptor.CommandId] = new ProvisioningRecord
                    {
                        Status = "failed",
                        Error = error,
                    };
                    stateChanged = true;
                    continue;
                }

                var collision = FindCollision(snapshot, descriptor.CommandId, sequence!);
                if (collision is not null || pending.Any(item => SequencesConflict(item.Sequence, sequence!)))
                {
                    _provisioning[descriptor.CommandId] = new ProvisioningRecord
                    {
                        Status = "conflict",
                        Key = descriptor.ProvisionedBinding,
                        Error = collision is null ? "The seed conflicts with another pending seed." : $"Conflicts with '{collision}'.",
                    };
                    stateChanged = true;
                    _log(
                        $"Codex command '{descriptor.CommandId}' was not provisioned because '{descriptor.ProvisionedBinding}' conflicts. "
                        + "Assign it in Settings > Keyboard Shortcuts.");
                    continue;
                }

                pending.Add((descriptor, sequence!));
            }

            if (pending.Count == 0)
            {
                if (stateChanged)
                {
                    TrySaveProvisioningState();
                }

                return;
            }

            var provisioned = await TryWriteProvisionedBindingsAsync(
                    pending,
                    snapshot.Stamp,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var (descriptor, _) in pending)
            {
                _provisioning[descriptor.CommandId] = new ProvisioningRecord
                {
                    Status = provisioned ? "provisioned" : "failed",
                    Key = descriptor.ProvisionedBinding,
                    Error = provisioned ? null : "The keybindings file changed or could not be written.",
                };
            }

            TrySaveProvisioningState();
            if (provisioned)
            {
                await ReloadWithRetriesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _provisioningLock.Release();
        }
    }

    private bool RecordHistoricalBindings(KeybindingSnapshot snapshot)
    {
        var changed = false;
        var hasHistoricalInstallEvidence = snapshot.Entries.Any(entry =>
            entry.Key is not null
            && CodexCommandCatalog.HistoricalBindings.TryGetValue(entry.CommandId, out var historical)
            && (string.Equals(entry.Key, historical.Current, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Key, historical.Legacy, StringComparison.OrdinalIgnoreCase)));
        var treatAsExistingInstall = _existingCompanionInstall || hasHistoricalInstallEvidence;
        foreach (var (commandId, historical) in CodexCommandCatalog.HistoricalBindings)
        {
            if (_provisioning.ContainsKey(commandId))
            {
                continue;
            }

            var keys = snapshot.Entries
                .Where(entry => string.Equals(entry.CommandId, commandId, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Key)
                .Where(key => key is not null)
                .ToArray();
            var knownBinding = keys.FirstOrDefault(key =>
                string.Equals(key, historical.Current, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, historical.Legacy, StringComparison.OrdinalIgnoreCase));
            if (knownBinding is null && !treatAsExistingInstall)
            {
                continue;
            }

            _provisioning[commandId] = new ProvisioningRecord
            {
                Status = knownBinding is null ? "historical" : "provisioned",
                Key = knownBinding,
            };
            changed = true;
        }

        return changed;
    }

    private async Task<bool> TryWriteProvisionedBindingsAsync(
        IReadOnlyList<(CodexCommandDescriptor Descriptor, KeySequence Sequence)> pending,
        CodexKeybindingFileStamp sourceStamp,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CodexKeybindingFileStamp expectedStamp;
            try
            {
                expectedStamp = GetFileStamp(_keybindingsPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log($"Could not inspect Codex keybindings before one-time provisioning: {exception.Message}");
                return false;
            }

            if (expectedStamp != sourceStamp)
            {
                await ReloadWithRetriesAsync(cancellationToken).ConfigureAwait(false);
                _log("Codex keybindings changed before provisioning; no bindings were written.");
                return false;
            }

            JsonArray bindings;
            try
            {
                if (expectedStamp.Exists)
                {
                    var currentJson = await _fileSystem
                        .ReadAllTextAsync(_keybindingsPath, cancellationToken)
                        .ConfigureAwait(false);
                    _ = ParseEntries(currentJson);
                    bindings = JsonNode.Parse(currentJson) as JsonArray
                        ?? throw new InvalidDataException("Codex keybindings.json must contain a JSON array.");
                }
                else
                {
                    bindings = [];
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                _log($"Could not prepare one-time Codex shortcut provisioning: {exception.Message}");
                return false;
            }

            foreach (var (descriptor, _) in pending)
            {
                bindings.Add(new JsonObject
                {
                    ["command"] = descriptor.CommandId,
                    ["key"] = descriptor.ProvisionedBinding,
                });
            }

            try
            {
                if (GetFileStamp(_keybindingsPath) != expectedStamp)
                {
                    await ReloadWithRetriesAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log($"Could not recheck Codex keybindings before one-time provisioning: {exception.Message}");
                return false;
            }

            try
            {
                BackupKeybindings(expectedStamp.Exists);
                await WriteJsonAtomicallyAsync(
                        _keybindingsPath,
                        bindings.ToJsonString(StateJsonOptions),
                        cancellationToken,
                        expectedStamp)
                    .ConfigureAwait(false);
                _log(
                    "Provisioned one-time Codex bindings: "
                    + string.Join(", ", pending.Select(item => item.Descriptor.CommandId))
                    + ". They are now user-editable in Codex.");
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log($"Could not write one-time Codex shortcut provisioning: {exception.Message}");
                return false;
            }
        }

        _log("Codex keybindings changed during provisioning; no bindings were written.");
        return false;
    }

    private void BackupKeybindings(bool exists)
    {
        if (!exists)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_keybindingsPath)!;
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = Path.Combine(directory, $"keybindings.json.virpil-codex-pad-{timestamp}.bak");
        var suffix = 1;
        while (_fileSystem.FileExists(backupPath))
        {
            backupPath = Path.Combine(directory, $"keybindings.json.virpil-codex-pad-{timestamp}-{suffix++}.bak");
        }

        _fileSystem.CopyFile(_keybindingsPath, backupPath);
        _log($"Backed up Codex keybindings to '{backupPath}'.");
    }

    private bool TryLoadProvisioningState(out string? error)
    {
        error = null;
        if (!_fileSystem.FileExists(_provisioningStatePath))
        {
            _provisioning = new Dictionary<string, ProvisioningRecord>(StringComparer.OrdinalIgnoreCase);
            return true;
        }

        try
        {
            var state = JsonSerializer.Deserialize<ProvisioningState>(
                _fileSystem.ReadAllText(_provisioningStatePath),
                StateJsonOptions) ?? new ProvisioningState();
            _provisioning = new Dictionary<string, ProvisioningRecord>(
                state.Commands ?? new Dictionary<string, ProvisioningRecord>(),
                StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"The provisioning state is unreadable ({exception.Message}). No Codex keybindings were changed.";
            return false;
        }
    }

    private bool TrySaveProvisioningState()
    {
        try
        {
            var state = new ProvisioningState { Commands = _provisioning };
            var json = JsonSerializer.Serialize(state, StateJsonOptions);
            WriteJsonAtomicallyAsync(_provisioningStatePath, json, CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"Could not save Codex shortcut provisioning state: {exception.Message}");
            return false;
        }
    }

    private async Task WriteJsonAtomicallyAsync(
        string path,
        string json,
        CancellationToken cancellationToken,
        CodexKeybindingFileStamp? expectedStamp = null)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The JSON path has no parent directory.");
        _fileSystem.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await _fileSystem.WriteAllTextAsync(temporaryPath, json + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
            if (expectedStamp is not null && GetFileStamp(path) != expectedStamp)
            {
                throw new IOException("Codex changed keybindings.json before the atomic replacement.");
            }

            _fileSystem.MoveFile(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _fileSystem.DeleteFile(temporaryPath);
        }
    }

    private void StartWatcher()
    {
        _watcher = _fileSystem.WatchFile(_keybindingsPath, ScheduleReload);
    }

    private void ScheduleReload()
    {
        if (_disposed)
        {
            return;
        }

        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = _timing.ScheduleOnce(250, ReloadFromWatcherAsync);
        }
    }

    private async Task ReloadFromWatcherAsync()
    {
        try
        {
            await ReloadWithRetriesAsync(_disposeCancellation.Token).ConfigureAwait(false);
            if (Volatile.Read(ref _provisioningDeferred) != 0)
            {
                await InitializeProvisioningAsync(_disposeCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _lastReloadFailed, 1);
            _log($"Could not reload Codex keybindings: {exception.Message}");
        }
    }

    private async Task RefreshIfChangedAsync(CancellationToken cancellationToken)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        try
        {
            var currentStamp = GetFileStamp(_keybindingsPath);
            if (currentStamp == snapshot.Stamp && Volatile.Read(ref _lastReloadFailed) == 0)
            {
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Reload handles retries and keeps the last known-good snapshot when metadata is temporarily unavailable.
        }

        await ReloadWithRetriesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReloadWithRetriesAsync(CancellationToken cancellationToken)
    {
        await _reloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Exception? lastError = null;
            for (var attempt = 0; attempt < RetryDelaysMs.Length; attempt++)
            {
                var delay = RetryDelaysMs[attempt];
                if (delay > 0)
                {
                    await _timing.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    var stampBefore = GetFileStamp(_keybindingsPath);
                    if (!stampBefore.Exists)
                    {
                        if (attempt == RetryDelaysMs.Length - 1)
                        {
                            Volatile.Write(ref _snapshot, new KeybindingSnapshot(true, [], stampBefore, null));
                            Interlocked.Exchange(ref _lastReloadFailed, 0);
                            return;
                        }

                        continue;
                    }

                    var json = await _fileSystem.ReadAllTextAsync(_keybindingsPath, cancellationToken).ConfigureAwait(false);
                    var stampAfter = GetFileStamp(_keybindingsPath);
                    if (stampAfter != stampBefore)
                    {
                        throw new IOException("Codex changed keybindings.json while it was being read.");
                    }

                    var entries = ParseEntries(json);
                    Volatile.Write(ref _snapshot, new KeybindingSnapshot(true, entries, stampAfter, null));
                    Interlocked.Exchange(ref _lastReloadFailed, 0);
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    lastError = exception;
                }
            }

            Interlocked.Exchange(ref _lastReloadFailed, 1);
            var current = Volatile.Read(ref _snapshot);
            var operation = lastError is JsonException or InvalidDataException ? "parsed" : "read";
            var snapshotDetail = current.Valid
                ? "The last known-good snapshot was retained."
                : "No valid snapshot is available; command actions are blocked until the file can be loaded.";
            var error = $"Codex keybindings could not be {operation} ({lastError?.Message}). {snapshotDetail}";
            if (!current.Valid)
            {
                Volatile.Write(
                    ref _snapshot,
                    new KeybindingSnapshot(false, [], current.Stamp, error));
            }

            _log(error);
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private static BindingEntry[] ParseEntries(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Codex keybindings.json must contain a JSON array.");
        }

        var entries = new List<BindingEntry>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("command", out var commandProperty)
                || commandProperty.ValueKind != JsonValueKind.String
                || !element.TryGetProperty("key", out var keyProperty)
                || keyProperty.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new InvalidDataException("Every Codex keybinding must contain a string command and a string-or-null key.");
            }

            var commandId = CodexCommandCatalog.NormalizeCommandId(commandProperty.GetString()!);
            var key = keyProperty.ValueKind == JsonValueKind.Null ? null : keyProperty.GetString();
            var conditional = element.TryGetProperty("when", out var whenProperty)
                && whenProperty.ValueKind != JsonValueKind.Null
                && (whenProperty.ValueKind != JsonValueKind.String
                    || !string.IsNullOrWhiteSpace(whenProperty.GetString()));
            entries.Add(new BindingEntry(commandId, key, conditional));
        }

        return entries.ToArray();
    }

    private CodexBindingSource GetExplicitSource(string commandId, string? key)
    {
        if (key is null
            || !_provisioning.TryGetValue(commandId, out var record)
            || !string.Equals(record.Status, "provisioned", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(record.Key, key, StringComparison.OrdinalIgnoreCase))
        {
            return CodexBindingSource.User;
        }

        return CodexBindingSource.Provisioned;
    }

    private static string? FindCollision(
        KeybindingSnapshot snapshot,
        string commandId,
        KeySequence sequence)
    {
        var descriptors = CodexCommandCatalog.All.ToDictionary(
            descriptor => descriptor.CommandId,
            StringComparer.OrdinalIgnoreCase);
        var commandIds = snapshot.Entries
            .Select(entry => entry.CommandId)
            .Concat(descriptors.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var otherCommandId in commandIds)
        {
            if (string.Equals(otherCommandId, commandId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var explicitEntries = snapshot.Entries
                .Where(entry => string.Equals(entry.CommandId, otherCommandId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var hasConditionalEntries = explicitEntries.Any(entry => entry.Conditional);
            if (!hasConditionalEntries && explicitEntries.Any(entry => entry.Key is null))
            {
                continue;
            }

            var explicitCandidates = explicitEntries
                .Where(entry => entry.Key is not null)
                .Select(entry => entry.Key!)
                .ToArray();
            var candidates = explicitCandidates.Length > 0
                ? explicitCandidates
                : hasConditionalEntries && descriptors.TryGetValue(otherCommandId, out var conditionalDescriptor)
                    ? conditionalDescriptor.DefaultBindings
                : explicitEntries.Length > 0
                    ? []
                : descriptors.TryGetValue(otherCommandId, out var descriptor)
                    ? descriptor.DefaultBindings
                    : [];
            foreach (var candidate in candidates)
            {
                if (KeySequenceParser.TryParse(candidate, allowBareModifiers: true, out var other, out _)
                    && SequencesConflict(sequence, other!))
                {
                    return otherCommandId;
                }
            }
        }

        return null;
    }

    private static string? FindSameCommandPrefixCollision(
        IReadOnlyList<BindingEntry> entries,
        string candidate,
        KeySequence sequence,
        bool allowBareModifiers)
    {
        foreach (var entry in entries)
        {
            if (entry.Key is null
                || string.Equals(entry.Key, candidate, StringComparison.OrdinalIgnoreCase)
                || !KeySequenceParser.TryParse(entry.Key, allowBareModifiers, out var other, out _)
                || other!.Chords.Count == sequence.Chords.Count)
            {
                continue;
            }

            if (SequencesConflict(sequence, other))
            {
                return other.NormalizedText;
            }
        }

        return null;
    }

    private static bool SequencesConflict(KeySequence first, KeySequence second)
    {
        var sharedSteps = Math.Min(first.Chords.Count, second.Chords.Count);
        for (var index = 0; index < sharedSteps; index++)
        {
            if (!string.Equals(
                    first.Chords[index].NormalizedText,
                    second.Chords[index].NormalizedText,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private CodexBindingSnapshotState GetSnapshotState(KeybindingSnapshot snapshot)
    {
        if (!snapshot.Valid)
        {
            return CodexBindingSnapshotState.Unavailable;
        }

        return Volatile.Read(ref _lastReloadFailed) == 0
            ? CodexBindingSnapshotState.Current
            : CodexBindingSnapshotState.LastKnownGood;
    }

    private static CodexBindingResolution Failure(
        CodexAction action,
        string commandId,
        CodexBindingSnapshotState snapshotState,
        string error)
    {
        if (!string.IsNullOrWhiteSpace(commandId)
            && !error.Contains("Settings > Keyboard Shortcuts", StringComparison.OrdinalIgnoreCase))
        {
            error += $" Assign '{commandId}' in Settings > Keyboard Shortcuts.";
        }

        return new(action, commandId, null, CodexBindingSource.None, snapshotState, error);
    }

    private CodexKeybindingFileStamp GetFileStamp(string path) => _fileSystem.GetFileStamp(path);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record BindingEntry(string CommandId, string? Key, bool Conditional);

    private sealed record KeybindingSnapshot(
        bool Valid,
        BindingEntry[] Entries,
        CodexKeybindingFileStamp Stamp,
        string? Error)
    {
        public static KeybindingSnapshot Unavailable { get; } =
            new(false, [], CodexKeybindingFileStamp.Missing, "Codex keybindings have not been loaded yet.");
    }

    private sealed class ProvisioningState
    {
        public int Version { get; init; } = 1;

        public Dictionary<string, ProvisioningRecord>? Commands { get; init; } = [];
    }

    private sealed class ProvisioningRecord
    {
        public string Status { get; init; } = string.Empty;

        public string? Key { get; init; }

        public string? Error { get; init; }
    }
}
