namespace VirpilCodexPad.App;

internal sealed class FileLog
{
    private readonly object _sync = new();

    public FileLog(string path)
    {
        Path = path;
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public string Path { get; }

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}";
        lock (_sync)
        {
            File.AppendAllText(Path, line);
        }
    }
}
