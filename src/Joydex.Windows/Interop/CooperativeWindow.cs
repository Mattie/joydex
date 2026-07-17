using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Joydex.Windows.Interop;

public sealed partial class CooperativeWindow : IDisposable
{
    public CooperativeWindow(string title)
    {
        Handle = CreateWindowEx(
            0,
            "STATIC",
            title,
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (Handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the hidden DirectInput window.");
        }
    }

    public IntPtr Handle { get; private set; }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            _ = DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);
}
