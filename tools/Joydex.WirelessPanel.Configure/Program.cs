using System.Text;
using Joydex.WirelessPanel;

namespace Joydex.WirelessPanel.Configure;

public static class Program
{
    private const string SuggestedEndpoint = "http://joydex-panel.local/";
    private const string SuggestedUsername = "joydex";

    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            Console.Error.WriteLine(
                "This provisioning tool accepts no command-line options. "
                + "Run it interactively so credentials stay out of arguments.");
            return 64;
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine(
                "Provisioning requires an interactive console so the password can stay hidden.");
            return 64;
        }

        var store = new WirelessPanelConfigurationStore();
        WirelessPanelConfiguration? existing;
        try
        {
            existing = store.Load();
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(
                $"The existing panel settings are unusable and will be replaced: {exception.Message}");
            existing = null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Unable to load the existing panel settings: {exception.Message}");
            return 2;
        }

        Console.WriteLine("Joydex wireless-panel provisioning");
        Console.WriteLine($"Settings file: {store.ConfigurationPath}");
        if (existing is not null)
        {
            Console.WriteLine($"Current endpoint: {existing.Endpoint.AbsoluteUri}");
            Console.WriteLine($"Current username: {existing.Username}");
            Console.WriteLine($"Currently enabled: {existing.Enabled}");
        }

        try
        {
            while (true)
            {
                var endpoint = PromptWithDefault(
                    "Panel endpoint",
                    existing?.Endpoint.AbsoluteUri ?? SuggestedEndpoint);
                var username = PromptWithDefault(
                    "Digest username",
                    existing?.Username ?? SuggestedUsername);
                var enabled = PromptBoolean(
                    "Enable wireless panel",
                    existing?.Enabled ?? true);
                var enteredPassword = ReadHiddenPassword(existing is not null);
                var password = enteredPassword.Length == 0 && existing is not null
                    ? existing.Password
                    : enteredPassword;

                try
                {
                    var configuration = WirelessPanelConfiguration.Create(
                        endpoint,
                        username,
                        password,
                        enabled);
                    store.Save(configuration);

                    Console.WriteLine();
                    Console.WriteLine("Wireless-panel settings saved.");
                    Console.WriteLine($"Endpoint: {configuration.Endpoint.AbsoluteUri}");
                    Console.WriteLine($"Username: {configuration.Username}");
                    Console.WriteLine($"Enabled: {configuration.Enabled}");
                    Console.WriteLine($"File: {store.ConfigurationPath}");
                    return 0;
                }
                catch (ArgumentException exception)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Settings were not saved: {exception.Message}");
                    Console.Error.WriteLine("Please enter the settings again.");
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or System.Security.Cryptography.CryptographicException)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Unable to save the panel settings: {exception.Message}");
                    return 2;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Provisioning cancelled.");
            return 130;
        }
    }

    private static string PromptWithDefault(string label, string defaultValue)
    {
        Console.Write($"{label} [{defaultValue}]: ");
        var value = Console.ReadLine();
        if (value is null)
        {
            throw new OperationCanceledException();
        }

        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static bool PromptBoolean(string label, bool defaultValue)
    {
        while (true)
        {
            Console.Write($"{label} [{(defaultValue ? "Y/n" : "y/N")}]: ");
            var value = Console.ReadLine();
            if (value is null)
            {
                throw new OperationCanceledException();
            }

            value = value.Trim();
            if (value.Length == 0)
            {
                return defaultValue;
            }

            if (value.Equals("y", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("n", StringComparison.OrdinalIgnoreCase)
                || value.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Console.Error.WriteLine("Enter yes or no.");
        }
    }

    private static string ReadHiddenPassword(bool canKeepExisting)
    {
        Console.Write(
            canKeepExisting
                ? "Digest password (hidden; Enter keeps current): "
                : "Digest password (hidden): ");

        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return password.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                }

                continue;
            }

            if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C)
            {
                throw new OperationCanceledException();
            }

            if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }
    }
}
