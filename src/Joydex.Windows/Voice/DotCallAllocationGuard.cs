namespace Joydex.Windows.Voice;

/// <summary>Preserves unknown remote allocation across worker crashes and automatic restart.</summary>
internal sealed class DotCallAllocationGuard(string path)
{
    private bool _owned;

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Joydex", "native-dot-call.pending");

    public static void RequireClear(string path)
    {
        if (File.Exists(path)) throw new InvalidOperationException(
            "A previous native dot call has unconfirmed cleanup. End that call in Desktop before clearing Joydex's native-dot-call.pending marker.");
    }

    public void Acquire()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, FileOptions.WriteThrough);
        var content = System.Text.Encoding.UTF8.GetBytes($"Native dot allocation started at {DateTimeOffset.UtcNow:O}.\n");
        stream.Write(content);
        stream.Flush(flushToDisk: true);
        _owned = true;
    }

    public void ConfirmStopped()
    {
        if (!_owned) return;
        File.Delete(path);
        _owned = false;
    }
}
