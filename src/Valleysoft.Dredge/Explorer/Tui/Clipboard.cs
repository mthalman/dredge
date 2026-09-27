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

    private static bool TryWriteWindows(string text)
    {
        // Another program can hold the clipboard open briefly, so retry a few times.
        bool opened = false;
        for (int attempt = 0; attempt < 10 && !(opened = OpenClipboard(IntPtr.Zero)); attempt++)
        {
            Thread.Sleep(10);
        }
        if (!opened)
        {
            return false;
        }
        IntPtr memory = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }
            int bytes = (text.Length + 1) * sizeof(char);
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
            CloseClipboard();
        }
    }

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
