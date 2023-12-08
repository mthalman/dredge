using System.Runtime.InteropServices;

namespace Valleysoft.Dredge;

internal static class FileHelper
{
    public static void CopyDirectory(string sourceDir, string destinationDir)
    {
        DirectoryInfo dir = new(sourceDir);
        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");
        }

        Directory.CreateDirectory(destinationDir);

        DirectoryInfo[] dirs = dir.GetDirectories();
        foreach (FileInfo file in dir.GetFiles())
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);

            if (file.LinkTarget is not null)
            {
                CreateSymbolicLink(targetFilePath, file.LinkTarget);
            }
            else
            {
                file.CopyTo(targetFilePath);
            }
        }
        
        foreach (DirectoryInfo subDir in dirs)
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, newDestinationDir);
        }
    }

    public static void CreateSymbolicLink(string targetFilePath, string linkTarget)
    {
        try
        {
            File.CreateSymbolicLink(targetFilePath, linkTarget);
        }
        catch (IOException ex) when (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new Exception($"Unable to create symbolic link from '{targetFilePath}' to '{linkTarget}'. Ensure that Windows Developer mode is enabled.\n\nError:\n{ex.Message}", ex);
        }
    }
}
