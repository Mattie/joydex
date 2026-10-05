using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class DesktopAppToolsEnvironmentResolver
{
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;
    private const int MaximumEnvironmentBytes = 128 * 1024;
    private const string PipeVariable = "CODEX_APP_TOOLS_PIPE_PATH";
    private const string NodeVariable = "CODEX_MCP_NODE_PATH";
    private const string AdapterRootVariable = "JOYDEX_CODEX_APP_TOOLS_ROOT";

    public static bool TryPrepareForCurrentProcess(Action<string>? log, out string error)
    {
        error = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            error = "The Codex Desktop task bridge currently requires Windows.";
            return false;
        }

        using var current = Process.GetCurrentProcess();
        if (!TryGetParentProcess(current, out var appServer, out error))
        {
            return false;
        }
        using (appServer)
        {
            return TryPrepareFromAppServer(appServer, log, allowInheritedDescriptor: true, out error);
        }
    }

    internal static bool TryPrepareFromAppServerProcess(
        int processId,
        Action<string>? log,
        out string error)
    {
        try
        {
            using var appServer = Process.GetProcessById(processId);
            return TryPrepareFromAppServer(appServer, log, allowInheritedDescriptor: false, out error);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            error = "The selected Codex Desktop App Server is no longer running.";
            return false;
        }
    }

    internal static bool TryFindDesktopAppServerProcessId(out int processId, out string error)
    {
        processId = 0;
        error = "Codex Desktop is not running a compatible App Server.";
        var matches = new List<(int ProcessId, DateTime StartedAt)>();
        foreach (var candidate in Process.GetProcessesByName("codex"))
        {
            using (candidate)
            {
                if (!TryGetParentProcess(candidate, out var desktop, out _))
                {
                    continue;
                }
                using (desktop)
                {
                    if (!string.Equals(desktop.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                var hasCommandLineDescriptor = TryReadCommandLine(candidate, out var commandLine, out _)
                    && TryExtractAppToolsEnvironment(commandLine, out _, out _, out _);
                var hasEnvironmentDescriptor = TryReadEnvironmentVariable(
                        candidate,
                        PipeVariable,
                        out var environmentPipe,
                        out _)
                    && IsLocalPipePath(environmentPipe);
                if (!hasCommandLineDescriptor && !hasEnvironmentDescriptor)
                {
                    continue;
                }

                DateTime startedAt;
                try
                {
                    startedAt = candidate.StartTime.ToUniversalTime();
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                matches.Add((candidate.Id, startedAt));
            }
        }

        if (matches.Count == 0)
        {
            return false;
        }

        processId = matches
            .OrderByDescending(match => match.StartedAt)
            .ThenByDescending(match => match.ProcessId)
            .First()
            .ProcessId;
        error = string.Empty;
        return true;
    }

    private static bool TryPrepareFromAppServer(
        Process appServer,
        Action<string>? log,
        bool allowInheritedDescriptor,
        out string error)
    {
        error = string.Empty;
        if (!string.Equals(appServer.ProcessName, "codex", StringComparison.OrdinalIgnoreCase))
        {
            error = "The bridge was not launched by a Codex App Server.";
            return false;
        }
        if (!TryGetParentProcess(appServer, out var desktop, out error))
        {
            return false;
        }
        string? packagedAdapterRoot = null;
        using (desktop)
        {
            if (!string.Equals(desktop.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase))
            {
                error = "The bridge was not launched by Codex Desktop.";
                return false;
            }
            packagedAdapterRoot = FindPackagedAdapterRoot(desktop);
        }

        if (TryReadCommandLine(appServer, out var commandLine, out var commandLineError)
            && TryExtractAppToolsEnvironment(
                commandLine,
                out var pipePath,
                out var nodePath,
                out var adapterRoot))
        {
            Environment.SetEnvironmentVariable(PipeVariable, pipePath);
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NodeVariable))
                && !string.IsNullOrWhiteSpace(nodePath)
                && File.Exists(nodePath))
            {
                Environment.SetEnvironmentVariable(NodeVariable, nodePath);
            }
            if (!string.IsNullOrWhiteSpace(adapterRoot)
                && File.Exists(Path.Combine(adapterRoot, "server.mjs")))
            {
                Environment.SetEnvironmentVariable(AdapterRootVariable, adapterRoot);
            }
            log?.Invoke("Recovered the Codex Desktop task broker from the owning App Server launch configuration.");
            return true;
        }

        if (TryReadEnvironmentVariable(appServer, PipeVariable, out var environmentPipe, out _)
            && IsLocalPipePath(environmentPipe))
        {
            Environment.SetEnvironmentVariable(PipeVariable, environmentPipe);
            if (TryReadEnvironmentVariable(appServer, NodeVariable, out var environmentNode, out _)
                && File.Exists(environmentNode))
            {
                Environment.SetEnvironmentVariable(NodeVariable, environmentNode);
            }
            else
            {
                Environment.SetEnvironmentVariable(NodeVariable, null);
            }
            Environment.SetEnvironmentVariable(AdapterRootVariable, packagedAdapterRoot);
            log?.Invoke("Recovered the Codex Desktop task broker from the owning App Server environment.");
            return true;
        }

        var inheritedPipe = Environment.GetEnvironmentVariable(PipeVariable)?.Trim();
        if (allowInheritedDescriptor && IsLocalPipePath(inheritedPipe))
        {
            log?.Invoke("Using the Codex Desktop task broker supplied to this bridge process.");
            return true;
        }

        error = string.IsNullOrWhiteSpace(commandLineError)
            ? "The owning Codex Desktop App Server did not expose a compatible task broker descriptor."
            : commandLineError;
        return false;
    }

    private static string? FindPackagedAdapterRoot(Process desktop)
    {
        try
        {
            var executable = desktop.MainModule?.FileName;
            var appDirectory = string.IsNullOrWhiteSpace(executable)
                ? null
                : Path.GetDirectoryName(executable);
            if (appDirectory is null)
            {
                return null;
            }

            var candidate = Path.Combine(
                appDirectory,
                "resources",
                "plugins",
                "openai-bundled",
                "plugins",
                "codex-app-tools");
            return File.Exists(Path.Combine(candidate, "server.mjs"))
                ? candidate
                : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return null;
        }
    }

    internal static bool TryExtractAppToolsEnvironment(
        string commandLine,
        out string pipePath,
        out string? nodePath,
        out string? adapterRoot)
    {
        pipePath = string.Empty;
        nodePath = null;
        adapterRoot = null;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        foreach (var argument in SplitCommandLine(commandLine))
        {
            if (!argument.StartsWith("mcp_servers.codex_app=", StringComparison.Ordinal))
            {
                continue;
            }
            if (!TryReadTomlString(argument, PipeVariable, out var candidate)
                || !IsLocalPipePath(candidate))
            {
                return false;
            }

            pipePath = candidate;
            if (TryReadTomlString(argument, NodeVariable, out var candidateNode))
            {
                nodePath = candidateNode;
            }
            if (TryReadTomlString(argument, "cwd", out var candidateRoot))
            {
                adapterRoot = candidateRoot;
            }
            return true;
        }
        return false;
    }

    private static bool TryReadTomlString(string source, string name, out string value)
    {
        value = string.Empty;
        var match = Regex.Match(
            source,
            $"\"{Regex.Escape(name)}\"\\s*=\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<string>($"\"{match.Groups["value"].Value}\"") ?? string.Empty;
            return value.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsLocalPipePath(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 512
        && value.StartsWith(@"\\.\pipe\codex-", StringComparison.OrdinalIgnoreCase)
        && value.IndexOfAny(['\0', '\r', '\n']) < 0;

    private static string[] SplitCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
        {
            return [];
        }
        try
        {
            var arguments = new string[count];
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                arguments[index] = Marshal.PtrToStringUni(item) ?? string.Empty;
            }
            return arguments;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static bool TryGetParentProcess(Process process, out Process parent, out string error)
    {
        parent = null!;
        error = string.Empty;
        var information = new ProcessBasicInformationRecord();
        var status = NtQueryInformationProcess(
            process.Handle,
            ProcessBasicInformation,
            ref information,
            Marshal.SizeOf<ProcessBasicInformationRecord>(),
            out _);
        if (status < 0 || information.InheritedFromUniqueProcessId == IntPtr.Zero)
        {
            error = "The bridge could not identify its owning process.";
            return false;
        }

        try
        {
            parent = Process.GetProcessById(checked((int)information.InheritedFromUniqueProcessId));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            error = "The bridge's owning process is no longer running.";
            return false;
        }
    }

    private static bool TryReadCommandLine(Process process, out string commandLine, out string error)
    {
        commandLine = string.Empty;
        error = string.Empty;
        var status = NtQueryInformationProcess(
            process.Handle,
            ProcessCommandLineInformation,
            IntPtr.Zero,
            0,
            out var requiredBytes);
        if (requiredBytes <= 0)
        {
            error = $"The owning Codex App Server command line was unavailable (NTSTATUS 0x{status:X8}).";
            return false;
        }

        var buffer = Marshal.AllocHGlobal(requiredBytes);
        try
        {
            status = NtQueryInformationProcess(
                process.Handle,
                ProcessCommandLineInformation,
                buffer,
                requiredBytes,
                out _);
            if (status < 0)
            {
                error = $"The owning Codex App Server command line was unavailable (NTSTATUS 0x{status:X8}).";
                return false;
            }

            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            var bufferStart = buffer.ToInt64();
            var bufferEnd = bufferStart + requiredBytes;
            var textStart = value.Buffer.ToInt64();
            if (value.Length == 0
                || textStart < bufferStart
                || textStart + value.Length > bufferEnd)
            {
                error = "The owning Codex App Server returned an invalid command line descriptor.";
                return false;
            }

            commandLine = Marshal.PtrToStringUni(value.Buffer, value.Length / sizeof(char)) ?? string.Empty;
            return commandLine.Length > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool TryReadEnvironmentVariable(
        Process process,
        string name,
        out string value,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        value = string.Empty;
        error = string.Empty;

        if (name.IndexOfAny(['=', '\0', '\r', '\n']) >= 0)
        {
            error = "The requested environment variable name is invalid.";
            return false;
        }

        try
        {
            // Codex 26.908 passes the app-tools descriptor through the App Server environment
            // instead of serializing it on the command line. The packaged processes are x64;
            // treat any unavailable or changed Windows-internal PEB layout as a retryable miss.
            var information = new ProcessBasicInformationRecord();
            var status = NtQueryInformationProcess(
                process.Handle,
                ProcessBasicInformation,
                ref information,
                Marshal.SizeOf<ProcessBasicInformationRecord>(),
                out _);
            if (status < 0 || information.PebBaseAddress == IntPtr.Zero)
            {
                error = $"The owning Codex App Server environment was unavailable (NTSTATUS 0x{status:X8}).";
                return false;
            }

            var processParametersOffset = IntPtr.Size == sizeof(long) ? 0x20 : 0x10;
            if (!TryReadPointer(
                    process.Handle,
                    IntPtr.Add(information.PebBaseAddress, processParametersOffset),
                    out var processParameters)
                || processParameters == IntPtr.Zero)
            {
                error = "The owning Codex App Server process parameters were unavailable.";
                return false;
            }

            var environmentOffset = IntPtr.Size == sizeof(long) ? 0x80 : 0x48;
            if (!TryReadPointer(
                    process.Handle,
                    IntPtr.Add(processParameters, environmentOffset),
                    out var environment)
                || environment == IntPtr.Zero)
            {
                error = "The owning Codex App Server environment block was unavailable.";
                return false;
            }

            if (!TryReadEnvironmentBlock(process.Handle, environment, out var environmentText))
            {
                error = "The owning Codex App Server environment block could not be read.";
                return false;
            }

            var prefix = name + "=";
            foreach (var entry in environmentText.Split('\0'))
            {
                if (!entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = entry[prefix.Length..];
                return value.Length > 0;
            }

            error = $"The owning Codex App Server did not provide {name}.";
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or OverflowException
            or System.ComponentModel.Win32Exception)
        {
            error = $"The owning Codex App Server environment was unavailable: {exception.Message}";
            return false;
        }
    }

    private static bool TryReadEnvironmentBlock(
        IntPtr processHandle,
        IntPtr environment,
        out string text)
    {
        const int chunkSize = 4096;
        text = string.Empty;
        using var buffer = new MemoryStream();
        while (buffer.Length < MaximumEnvironmentBytes)
        {
            var remaining = MaximumEnvironmentBytes - checked((int)buffer.Length);
            var requested = Math.Min(chunkSize, remaining);
            byte[]? chunk = null;
            var count = 0;
            while (requested >= sizeof(char) && count == 0)
            {
                chunk = new byte[requested];
                _ = ReadProcessMemory(
                    processHandle,
                    IntPtr.Add(environment, checked((int)buffer.Length)),
                    chunk,
                    chunk.Length,
                    out var bytesRead);
                count = checked((int)bytesRead);
                requested = (requested / 2) & ~1;
            }
            if (count <= 0)
            {
                return false;
            }

            buffer.Write(chunk!, 0, count);
            var bytes = buffer.GetBuffer();
            var end = FindEnvironmentBlockEnd(bytes, checked((int)buffer.Length));
            if (end >= 0)
            {
                text = System.Text.Encoding.Unicode.GetString(bytes, 0, end);
                return true;
            }
        }

        return false;
    }

    private static int FindEnvironmentBlockEnd(byte[] bytes, int count)
    {
        for (var index = 0; index + 3 < count; index += sizeof(char))
        {
            if (bytes[index] == 0
                && bytes[index + 1] == 0
                && bytes[index + 2] == 0
                && bytes[index + 3] == 0)
            {
                return index + 4;
            }
        }
        return -1;
    }

    private static bool TryReadPointer(IntPtr processHandle, IntPtr address, out IntPtr value)
    {
        value = IntPtr.Zero;
        var bytes = new byte[IntPtr.Size];
        if (!ReadProcessMemory(processHandle, address, bytes, bytes.Length, out var bytesRead)
            || bytesRead != (IntPtr)bytes.Length)
        {
            return false;
        }

        value = IntPtr.Size == sizeof(long)
            ? new IntPtr(BitConverter.ToInt64(bytes))
            : new IntPtr(BitConverter.ToInt32(bytes));
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformationRecord
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformationRecord processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out IntPtr bytesRead);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
