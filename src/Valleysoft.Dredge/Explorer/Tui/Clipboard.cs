using System.Runtime.InteropServices;
using System.Text;

namespace Valleysoft.Dredge.Explorer.Tui;

// How the explorer actually copies.
internal enum ClipboardMode { Off, Native, Osc52 }

internal static partial class Clipboard
{
    // Use the Windows clipboard when dredge runs on the same machine as
    // the terminal, because it works in every terminal. Elsewhere, including
    // over SSH, OSC 52 asks the terminal to set the clipboard on the user's
    // machine.
    public static ClipboardMode Resolve(bool isWindows, bool remote) =>
        isWindows && !remote ? ClipboardMode.Native : ClipboardMode.Osc52;

    public static bool IsRemoteSession(Func<string, string?> environment) =>
        !string.IsNullOrEmpty(environment("SSH_CONNECTION"))
        || !string.IsNullOrEmpty(environment("SSH_CLIENT"))
        || !string.IsNullOrEmpty(environment("SSH_TTY"));

    public static bool Write(ClipboardMode mode, string text)
    {
        switch (mode)
        {
            case ClipboardMode.Native:
                return OperatingSystem.IsWindows() && TryWriteWindows(text);
            case ClipboardMode.Osc52:
                using (Stream stdout = Console.OpenStandardOutput())
                {
                    stdout.Write(Encoding.ASCII.GetBytes(Osc52(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)))));
                    stdout.Flush();
                }
                return true;
            default:
                return false;
        }
    }

    internal static string Osc52(string base64) => $"\u001b]52;c;{base64}\a";

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int HWND_MESSAGE = -3;

    private static bool TryWriteWindows(string text) => TryWriteWindows(text, OpenClipboard);

    internal static bool TryWriteWindows(string text, Func<IntPtr, bool> openClipboard)
    {
        // EmptyClipboard requires an owned HWND for SetClipboardData to succeed.
        IntPtr owner = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new IntPtr(HWND_MESSAGE),
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (owner == IntPtr.Zero)
        {
            return false;
        }
        bool opened = false;
        IntPtr memory = IntPtr.Zero;
        try
        {
            int bytes = checked((text.Length + 1) * sizeof(char));
            memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
            if (memory == IntPtr.Zero)
            {
                return false;
            }
            IntPtr target = GlobalLock(memory);
            if (target == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(memory);
            }
            // Prepare the replacement before clearing the user's clipboard.
            // Another program can hold the clipboard open briefly, so retry a few times.
            for (int attempt = 0; attempt < 10 && !(opened = openClipboard(owner)); attempt++)
            {
                Thread.Sleep(10);
            }
            if (!opened || !EmptyClipboard())
            {
                return false;
            }
            if (SetClipboardData(CF_UNICODETEXT, memory) == IntPtr.Zero)
            {
                return false;
            }
            // The clipboard owns the memory now.
            memory = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (memory != IntPtr.Zero)
            {
                GlobalFree(memory);
            }
            if (opened)
            {
                CloseClipboard();
            }
            DestroyWindow(owner);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetClipboardData(uint format, IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalFree(IntPtr memory);
}
