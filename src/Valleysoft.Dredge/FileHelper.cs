using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Valleysoft.Dredge;

internal static class FileHelper
{
    public static async Task CopyDirectoryAsync(
        string sourceDir,
        string destinationDir,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DirectoryInfo dir = new(sourceDir);
        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");
        }

        Directory.CreateDirectory(destinationDir);

        foreach (FileInfo file in dir.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string targetFilePath = Path.Combine(destinationDir, file.Name);

            if (file.LinkTarget is not null)
            {
                CreateSymbolicLink(targetFilePath, file.LinkTarget);
            }
            else
            {
                await CopyFileAsync(file, targetFilePath, overwrite: false, cancellationToken);
            }
        }
        
        foreach (DirectoryInfo subDir in dir.EnumerateDirectories())
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            await CopyDirectoryAsync(subDir.FullName, newDestinationDir, cancellationToken);
        }
    }

    public static async Task CopyFileAsync(
        FileInfo sourceFile,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        DateTime creationTimeUtc = sourceFile.CreationTimeUtc;
        DateTime lastAccessTimeUtc = sourceFile.LastAccessTimeUtc;
        DateTime lastWriteTimeUtc = sourceFile.LastWriteTimeUtc;
        FileAttributes attributes = sourceFile.Attributes;
        UnixFileMode unixFileMode = default;
        if (!OperatingSystem.IsWindows())
        {
            unixFileMode = File.GetUnixFileMode(sourceFile.FullName);
        }

        await using (FileStream source = sourceFile.OpenRead())
        await using (FileStream destination = new(
            destinationPath,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        File.SetCreationTimeUtc(destinationPath, creationTimeUtc);
        File.SetLastAccessTimeUtc(destinationPath, lastAccessTimeUtc);
        File.SetLastWriteTimeUtc(destinationPath, lastWriteTimeUtc);
        File.SetAttributes(destinationPath, attributes);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destinationPath, unixFileMode);
        }
    }

    public static void CreateSymbolicLink(
        string targetFilePath,
        string linkTarget,
        bool targetIsDirectory = false)
    {
        try
        {
            if (targetIsDirectory)
            {
                Directory.CreateSymbolicLink(targetFilePath, linkTarget);
            }
            else
            {
                File.CreateSymbolicLink(targetFilePath, linkTarget);
            }
        }
        catch (IOException ex) when (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new Exception($"Unable to create symbolic link from '{targetFilePath}' to '{linkTarget}'. Ensure that Windows Developer mode is enabled.\n\nError:\n{ex.Message}", ex);
        }
    }

    public static void CreateHardLink(string linkPath, string targetPath)
    {
        bool success = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(linkPath, targetPath, IntPtr.Zero)
            : CreateHardLinkUnix(targetPath, linkPath) == 0;
        if (!success)
        {
            throw new IOException(
                $"Unable to create hard link from '{linkPath}' to '{targetPath}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
