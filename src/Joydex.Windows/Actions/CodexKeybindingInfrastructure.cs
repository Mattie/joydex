namespace Joydex.Windows.Actions;

internal sealed record CodexKeybindingFileStamp(bool Exists, DateTime LastWriteTimeUtc, long Length)
{
    public static CodexKeybindingFileStamp Missing { get; } = new(false, DateTime.MinValue, 0);
}

/// <summary>Provides the file operations and change notifications used by the resolver.</summary>
internal interface ICodexKeybindingFileSystem
{
    void CreateDirectory(string path);

    bool FileExists(string path);

    string ReadAllText(string path);

    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);

    Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken);

    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    void DeleteFile(string path);

    void CopyFile(string sourcePath, string destinationPath);

    CodexKeybindingFileStamp GetFileStamp(string path);

    IDisposable WatchFile(string path, Action changed);
}

/// <summary>Provides retry delays and debounced callbacks without tying tests to wall-clock sleeps.</summary>
internal interface ICodexKeybindingTiming
{
    Task DelayAsync(int milliseconds, CancellationToken cancellationToken);

    IDisposable ScheduleOnce(int milliseconds, Func<Task> callback);
}

internal sealed class SystemCodexKeybindingFileSystem : ICodexKeybindingFileSystem
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool FileExists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(path, cancellationToken);

    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, contents, cancellationToken);

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        File.Move(sourcePath, destinationPath, overwrite);

    public void DeleteFile(string path) => File.Delete(path);

    public void CopyFile(string sourcePath, string destinationPath) => File.Copy(sourcePath, destinationPath);

    public CodexKeybindingFileStamp GetFileStamp(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException("The Codex keybindings path refers to a directory.");
            }
        }
        catch (FileNotFoundException)
        {
            return CodexKeybindingFileStamp.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return CodexKeybindingFileStamp.Missing;
        }

        var info = new FileInfo(path);
        info.Refresh();
        return new CodexKeybindingFileStamp(true, info.LastWriteTimeUtc, info.Length);
    }

    public IDisposable WatchFile(string path, Action changed)
    {
        var watcher = new FileSystemWatcher(
            Path.GetDirectoryName(path)!,
            Path.GetFileName(path))
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.CreationTime
                | NotifyFilters.Size,
        };
        watcher.Changed += (_, _) => changed();
        watcher.Created += (_, _) => changed();
        watcher.Deleted += (_, _) => changed();
        watcher.Renamed += (_, _) => changed();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}

internal sealed class SystemCodexKeybindingTiming : ICodexKeybindingTiming
{
    public Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        Task.Delay(milliseconds, cancellationToken);

    public IDisposable ScheduleOnce(int milliseconds, Func<Task> callback) =>
        new ScheduledCallback(milliseconds, callback);

    private sealed class ScheduledCallback : IDisposable
    {
        private readonly System.Threading.Timer _timer;

        public ScheduledCallback(int milliseconds, Func<Task> callback)
        {
            _timer = new System.Threading.Timer(
                async _ =>
                {
                    try
                    {
                        await callback().ConfigureAwait(false);
                    }
                    catch
                    {
                        // The service callback records reload failures. A Timer callback cannot surface them.
                    }
                },
                null,
                milliseconds,
                Timeout.Infinite);
        }

        public void Dispose() => _timer.Dispose();
    }
}
