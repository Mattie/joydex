using System.Text;
using Microsoft.Win32;

namespace Joydex.App;

/// <summary>
/// Manages one entry in the current user's Windows login startup list.
/// </summary>
internal sealed class LoginStartupRegistration
{
    private readonly ILoginStartupStore _store;

    public LoginStartupRegistration(
        string executablePath,
        string valueName,
        IReadOnlyList<string>? arguments = null,
        ILoginStartupStore? store = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        Command = BuildCommand(Path.GetFullPath(executablePath), arguments ?? []);
        _store = store ?? new RegistryLoginStartupStore(valueName);
    }

    /// <summary>The command Windows will run after the current user signs in.</summary>
    public string Command { get; }

    /// <summary>Whether Joydex currently has a user-level login startup entry.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_store.Read());

    /// <summary>Adds or removes the user-level login startup entry.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            _store.Write(Command);
        }
        else
        {
            _store.Delete();
        }
    }

    internal static string BuildCommand(string executablePath, IReadOnlyList<string> arguments) =>
        string.Join(" ", new[] { executablePath }.Concat(arguments).Select(QuoteArgument));

    private static string QuoteArgument(string value)
    {
        var quoted = new StringBuilder(value.Length + 2);
        quoted.Append('"');
        var backslashes = 0;

        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1);
                quoted.Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes);
                quoted.Append(character);
            }

            backslashes = 0;
        }

        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }
}

/// <summary>Provides the persisted command used by login startup registration.</summary>
internal interface ILoginStartupStore
{
    string? Read();

    void Write(string command);

    void Delete();
}

/// <summary>Stores Joydex in the standard per-user Windows Run key.</summary>
internal sealed class RegistryLoginStartupStore : ILoginStartupStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;

    public RegistryLoginStartupStore(string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        _valueName = valueName;
    }

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(
            _valueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The Windows login startup key could not be opened.");
        key.SetValue(_valueName, command, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
