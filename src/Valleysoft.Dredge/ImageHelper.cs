using ICSharpCode.SharpZipLib.Tar;
using System.IO.Compression;
using System.Text;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge;

internal static class ImageHelper
{
    // See https://github.com/opencontainers/image-spec/blob/main/layer.md#whiteouts
    private const string WhiteoutMarkerPrefix = ".wh.";
    private const string OpaqueWhiteoutMarker = ".wh..wh..opq";

    private static readonly string LayersTempPath = Path.Combine(DredgeState.DredgeTempPath, "layers");

    public static async Task SaveImageLayersToDiskAsync(
        IDockerRegistryClientFactory dockerRegistryClientFactory, string image, string destPath, int? layerIndex,
        string layerIndexOptionName, bool noSquash, PlatformOptionsBase options)
    {
        // Spec for OCI image layer filesystem changeset: https://github.com/opencontainers/image-spec/blob/main/layer.md

        Console.Error.WriteLine($"Getting layers for {image}");

        ImageName imageName = ImageName.Parse(image);
        IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(imageName.Registry);
        IImageManifest manifest = (await ManifestHelper.GetResolvedManifestAsync(client, imageName, options)).Manifest;

        int startIndex = 0;
        int layerCount = manifest.Layers.Length;
        if (layerIndex is not null)
        {
            if (layerIndex < 0 || layerIndex >= manifest.Layers.Length)
            {
                throw new Exception($"Value is out of range for the '{layerIndexOptionName}' option.");
            }
            layerCount = layerIndex.Value + 1;

            if (noSquash)
            {
                startIndex = layerIndex.Value;
            }
        }

        for (int i = startIndex; i < layerCount; i++)
        {
            IDescriptor layer = manifest.Layers[i];
            if (string.IsNullOrEmpty(layer.Digest))
            {
                throw new Exception($"Layer digest not set for image '{imageName}'");
            }

            Console.Error.WriteLine($"Layer {layer.Digest}");

            string layerName = layer.Digest[(layer.Digest.IndexOf(':') + 1)..];
            ValidateLayerDigest(layerName);
            string layerDir = GetContainedPath(LayersTempPath, layerName);
            if (Directory.Exists(layerDir))
            {
                Console.Error.WriteLine($"\tUsing cached layer on disk...");
            }
            else
            {
                Console.Error.WriteLine($"\tDownloading layer...");
                using Stream layerStream = await client.Blobs.GetAsync(imageName.Repo, layer.Digest);

                try
                {
                    await ExtractLayerAsync(layerStream, layerDir);
                }
                catch
                {
                    if (Directory.Exists(layerDir))
                    {
                        Directory.Delete(layerDir, recursive: true);
                    }
                    throw;
                }
            }

            if (noSquash)
            {
                FileHelper.CopyDirectory(layerDir, Path.Combine(destPath, $"layer{i}-{layerName}"));
            }
            else
            {
                ApplyLayer(layerDir, destPath);
            }
        }
    }

    private static async Task ExtractLayerAsync(Stream layerStream, string layerDir)
    {
        Console.Error.WriteLine($"\tExtracting layer...");

        Directory.CreateDirectory(layerDir);
        List<(string Path, string Target)> hardLinks = [];

        using GZipStream gZipStream = new(layerStream, CompressionMode.Decompress);

        // Can't use System.Formats.Tar.TarReader because it fails to read certain types of tarballs:
        // https://github.com/dotnet/runtime/issues/74316#issuecomment-1312227247
        using TarInputStream tarStream = new(gZipStream, Encoding.UTF8);

        while (true)
        {
            TarEntry? entry = tarStream.GetNextEntry();

            if (entry is null)
            {
                break;
            }

            if (entry.IsDirectory)
            {
                string directoryPath = GetContainedPath(layerDir, entry.Name);
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                }
                continue;
            }

            string entryName = entry.Name;
            string entryDirName = Path.GetDirectoryName(entryName) ?? string.Empty;
            string entryFileName = Path.GetFileName(entryName);

            foreach (char invalidChar in Path.GetInvalidPathChars())
            {
                entryDirName = entryDirName.Replace(invalidChar, '-');
            }

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                entryFileName = entryFileName.Replace(invalidChar, '-');
            }

            if (entryFileName.StartsWith(WhiteoutMarkerPrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(entryFileName, OpaqueWhiteoutMarker, StringComparison.OrdinalIgnoreCase))
            {
                ValidatePathSegment(entryFileName[WhiteoutMarkerPrefix.Length..], "whiteout target");
            }

            entryName = Path.Combine(entryDirName, entryFileName);
            await ExtractTarEntry(layerDir, tarStream, entry, entryName, hardLinks);
        }

        while (hardLinks.Count > 0)
        {
            int copiedLinkCount = 0;
            for (int i = hardLinks.Count - 1; i >= 0; i--)
            {
                (string path, string target) = hardLinks[i];
                string targetPath = GetContainedPath(
                    layerDir,
                    target.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (File.Exists(targetPath))
                {
                    File.Copy(targetPath, path, overwrite: true);
                    hardLinks.RemoveAt(i);
                    copiedLinkCount++;
                }
            }

            if (copiedLinkCount == 0)
            {
                throw new InvalidDataException($"Unable to resolve hard link target '{hardLinks[0].Target}'.");
            }
        }
    }

    private static void ApplyLayer(string layerDir, string workingDir)
    {
        Console.Error.WriteLine($"\tApplying layer...");

        FileInfo[] layerFiles = GetLayerFiles(new DirectoryInfo(layerDir)).ToArray();

        foreach (FileInfo layerFile in layerFiles
            .Where(IsOpaqueWhiteout)
            .OrderBy(file => Path.GetRelativePath(layerDir, file.FullName).Count(c => c == Path.DirectorySeparatorChar)))
        {
            string? layerFileDirName = Path.GetDirectoryName(Path.GetRelativePath(layerDir, layerFile.FullName));
            if (string.IsNullOrEmpty(layerFileDirName))
            {
                throw new Exception("The opaque whiteout file marker should not exist in the root directory.");
            }

            string fullDirPath = GetContainedPath(workingDir, layerFileDirName);
            if (Directory.Exists(fullDirPath))
            {
                Directory.Delete(fullDirPath, recursive: true);
            }
        }

        foreach (FileInfo layerFile in layerFiles.Where(IsWhiteout))
        {
            string layerFileRelativePath = Path.GetRelativePath(layerDir, layerFile.FullName);
            string? layerFileDirName = Path.GetDirectoryName(layerFileRelativePath);
            string actualFileName = layerFile.Name[WhiteoutMarkerPrefix.Length..];
            ValidatePathSegment(actualFileName, "whiteout target");
            string fullPath = GetContainedPath(
                workingDir,
                Path.Combine(layerFileDirName ?? string.Empty, actualFileName));

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                bool isLink = File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint);
                Directory.Delete(fullPath, recursive: !isLink);
            }
        }

        foreach (FileInfo layerFile in layerFiles.Where(file => !IsOpaqueWhiteout(file) && !IsWhiteout(file)))
        {
            string layerFileRelativePath = Path.GetRelativePath(layerDir, layerFile.FullName);
            string dest = GetContainedPath(workingDir, layerFileRelativePath);
            string destDir = Path.GetDirectoryName(dest)!;
            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            if (layerFile.LinkTarget is not null)
            {
                if (File.Exists(dest) || Directory.Exists(dest))
                {
                    File.Delete(dest);
                }

                string sourceTarget = GetLinkTargetPath(layerDir, layerFile.FullName, layerFile.LinkTarget);
                string destTarget = GetContainedPath(
                    workingDir,
                    Path.GetRelativePath(layerDir, sourceTarget));
                FileHelper.CreateSymbolicLink(dest, Path.GetRelativePath(destDir, destTarget));
            }
            else
            {
                File.Copy(layerFile.FullName, dest, overwrite: true);
            }
        }
    }

    private static bool IsOpaqueWhiteout(FileInfo file) =>
        string.Equals(file.Name, OpaqueWhiteoutMarker, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<FileInfo> GetLayerFiles(DirectoryInfo directory)
    {
        foreach (FileInfo file in directory.GetFiles())
        {
            yield return file;
        }

        foreach (DirectoryInfo subdirectory in directory.GetDirectories())
        {
            if (IsReparsePoint(subdirectory.FullName))
            {
                throw new InvalidDataException($"Layer directory '{subdirectory.FullName}' is a symbolic link.");
            }

            foreach (FileInfo file in GetLayerFiles(subdirectory))
            {
                yield return file;
            }
        }
    }

    private static bool IsWhiteout(FileInfo file) =>
        !IsOpaqueWhiteout(file) &&
        file.Name.StartsWith(WhiteoutMarkerPrefix, StringComparison.OrdinalIgnoreCase);

    private static async Task ExtractTarEntry(
        string workingDir,
        TarInputStream tarStream,
        TarEntry entry,
        string entryName,
        List<(string Path, string Target)> hardLinks)
    {
        string filePath = GetContainedPath(workingDir, entryName);
        string? directoryPath = Path.GetDirectoryName(filePath);
        if (directoryPath is not null && !Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        if (entry.TarHeader.TypeFlag == TarHeader.LF_LINK && !string.IsNullOrEmpty(entry.TarHeader.LinkName))
        {
            hardLinks.Add((filePath, entry.TarHeader.LinkName));
        }
        else if (entry.TarHeader.TypeFlag == TarHeader.LF_SYMLINK && !string.IsNullOrEmpty(entry.TarHeader.LinkName))
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            string targetPath = GetLinkTargetPath(workingDir, filePath, entry.TarHeader.LinkName);
            string relativeTarget = Path.GetRelativePath(directoryPath!, targetPath);
            FileHelper.CreateSymbolicLink(filePath, relativeTarget);
        }
        else
        {
            using FileStream outputStream = File.Create(filePath);
            await tarStream.CopyEntryContentsAsync(outputStream, CancellationToken.None);
        }
    }

    private static string GetLinkTargetPath(string workingDir, string linkPath, string linkTarget)
    {
        string target = Path.IsPathRooted(linkTarget)
            ? linkTarget.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : Path.Combine(Path.GetDirectoryName(Path.GetRelativePath(workingDir, linkPath)) ?? string.Empty, linkTarget);

        return GetContainedPath(workingDir, target);
    }

    private static string GetContainedPath(string rootPath, string relativePath)
    {
        string fullRootPath = Path.GetFullPath(rootPath);
        string fullPath = Path.GetFullPath(Path.Combine(fullRootPath, relativePath));
        string pathFromRoot = Path.GetRelativePath(fullRootPath, fullPath);

        if (Path.IsPathRooted(pathFromRoot) ||
            pathFromRoot.Equals("..", StringComparison.Ordinal) ||
            pathFromRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            pathFromRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive entry path '{relativePath}' is outside the layer directory.");
        }

        ValidateNoLinkParents(fullRootPath, fullPath);
        if (IsReparsePoint(fullPath))
        {
            throw new InvalidDataException($"Path '{fullPath}' is a symbolic link.");
        }
        return fullPath;
    }

    private static void ValidateNoLinkParents(string rootPath, string fullPath)
    {
        string? path = Path.GetDirectoryName(fullPath);
        while (path is not null && !Path.GetFullPath(path).Equals(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            if (IsReparsePoint(path))
            {
                throw new InvalidDataException($"Path '{fullPath}' traverses a symbolic link.");
            }

            path = Path.GetDirectoryName(path);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void ValidatePathSegment(string value, string description)
    {
        if (string.IsNullOrEmpty(value) ||
            value is "." or ".." ||
            Path.IsPathRooted(value) ||
            value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidDataException($"Invalid {description} '{value}'.");
        }
    }

    private static void ValidateLayerDigest(string value)
    {
        ValidatePathSegment(value, "layer digest");

        if (value.Contains(':'))
        {
            throw new InvalidDataException($"Invalid layer digest '{value}'.");
        }
    }
}
