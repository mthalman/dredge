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
        string layerIndexOptionName, bool noSquash, PlatformOptionsBase options, CancellationToken cancellationToken)
    {
        // Spec for OCI image layer filesystem changeset: https://github.com/opencontainers/image-spec/blob/main/layer.md

        Console.Error.WriteLine($"Getting layers for {image}");

        ImageName imageName = ImageName.Parse(image);
        IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(imageName.Registry);
        IImageManifest manifest =
            (await ManifestHelper.GetResolvedManifestAsync(client, imageName, options, cancellationToken)).Manifest;

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
            cancellationToken.ThrowIfCancellationRequested();
            IDescriptor layer = manifest.Layers[i];
            if (string.IsNullOrEmpty(layer.Digest))
            {
                throw new Exception($"Layer digest not set for image '{imageName}'");
            }

            Console.Error.WriteLine($"Layer {layer.Digest}");

            string layerName = layer.Digest[(layer.Digest.IndexOf(':') + 1)..];
            string layerDir = Path.Combine(LayersTempPath, layerName);
            if (Directory.Exists(layerDir))
            {
                Console.Error.WriteLine($"\tUsing cached layer on disk...");
            }
            else
            {
                Console.Error.WriteLine($"\tDownloading layer...");
                using Stream layerStream =
                    await client.Blobs.GetAsync(imageName.Repo, layer.Digest, cancellationToken);

                await ExtractLayerToCacheAsync(layerStream, layerDir, cancellationToken);
            }

            if (noSquash)
            {
                await FileHelper.CopyDirectoryAsync(
                    layerDir, Path.Combine(destPath, $"layer{i}-{layerName}"), cancellationToken);
            }
            else
            {
                await ApplyLayerAsync(layerDir, destPath, cancellationToken);
            }
        }
    }

    private static async Task ExtractLayerToCacheAsync(
        Stream layerStream,
        string layerDir,
        CancellationToken cancellationToken)
    {
        string tempLayerDir = $"{layerDir}.{Guid.NewGuid():N}.tmp";
        bool cachePublished = false;
        bool operationCompleted = false;

        try
        {
            await ExtractLayerAsync(layerStream, tempLayerDir, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Directory.Move(tempLayerDir, layerDir);
                cachePublished = true;
            }
            catch (IOException) when (Directory.Exists(layerDir))
            {
                // Another process published the same layer while this one was extracting it.
            }

            operationCompleted = true;
        }
        finally
        {
            if (!cachePublished && Directory.Exists(tempLayerDir))
            {
                try
                {
                    Directory.Delete(tempLayerDir, recursive: true);
                }
                catch (IOException e) when (!operationCompleted)
                {
                    ReportCleanupFailure(tempLayerDir, e);
                }
                catch (UnauthorizedAccessException e) when (!operationCompleted)
                {
                    ReportCleanupFailure(tempLayerDir, e);
                }
            }
        }
    }

    private static void ReportCleanupFailure(string tempLayerDir, Exception exception) =>
        Console.Error.WriteLine(
            $"Failed to delete incomplete layer cache directory '{tempLayerDir}': {exception.Message}");

    private static async Task ExtractLayerAsync(
        Stream layerStream,
        string layerDir,
        CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"\tExtracting layer...");

        Directory.CreateDirectory(layerDir);

        using GZipStream gZipStream = new(layerStream, CompressionMode.Decompress);

        // Can't use System.Formats.Tar.TarReader because it fails to read certain types of tarballs:
        // https://github.com/dotnet/runtime/issues/74316#issuecomment-1312227247
        using TarInputStream tarStream = new(gZipStream, Encoding.UTF8);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TarEntry? entry = tarStream.GetNextEntry();

            if (entry is null)
            {
                break;
            }

            if (entry.IsDirectory)
            {
                string directoryPath = Path.Combine(layerDir, entry.Name);
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

            entryName = Path.Combine(entryDirName, entryFileName);
            await ExtractTarEntry(layerDir, tarStream, entry, entryName, cancellationToken);
        }
    }

    private static async Task ApplyLayerAsync(
        string layerDir,
        string workingDir,
        CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"\tApplying layer...");

        foreach (FileInfo layerFile in new DirectoryInfo(layerDir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string layerFileRelativePath = Path.GetRelativePath(layerDir, layerFile.FullName);
            string? layerfileDirName = Path.GetDirectoryName(layerFileRelativePath);

            // If this an OCI opaque whiteout file marker, delete the directory where the file marker
            // is located.
            if (string.Equals(layerFile.Name, OpaqueWhiteoutMarker, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(layerfileDirName))
                {
                    throw new Exception("The opaque whiteout file marker should not exist in the root directory.");
                }
                string fullDirPath = Path.Combine(workingDir, layerfileDirName);

                if (Directory.Exists(fullDirPath))
                {
                    Directory.Delete(fullDirPath, recursive: true);
                }
            }
            // If this is an OCI whiteout file marker, delete the associated file
            else if (layerFile.Name.StartsWith(WhiteoutMarkerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string actualFileName = layerFile.Name[WhiteoutMarkerPrefix.Length..];
                string fullFilePath = Path.Combine(
                    workingDir,
                    Path.GetDirectoryName(layerfileDirName) ?? string.Empty,
                    actualFileName);

                if (File.Exists(fullFilePath))
                {
                    File.Delete(fullFilePath);
                }
            }
            else
            {
                string dest = Path.Combine(workingDir, layerFileRelativePath);
                string destDir = Path.GetDirectoryName(dest)!;
                if (!Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                if (layerFile.LinkTarget is not null)
                {
                    if (File.Exists(dest))
                    {
                        File.Delete(dest);
                    }

                    FileHelper.CreateSymbolicLink(dest, layerFile.LinkTarget);
                }
                else
                {
                    await FileHelper.CopyFileAsync(layerFile, dest, overwrite: true, cancellationToken);
                }
            }
        }
    }

    private static async Task ExtractTarEntry(
        string workingDir,
        TarInputStream tarStream,
        TarEntry entry,
        string entryName,
        CancellationToken cancellationToken)
    {
        string filePath = Path.Combine(workingDir, entryName);
        string? directoryPath = Path.GetDirectoryName(filePath);
        if (directoryPath is not null && !Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        if ((entry.TarHeader.TypeFlag == TarHeader.LF_LINK || entry.TarHeader.TypeFlag == TarHeader.LF_SYMLINK) &&
            !string.IsNullOrEmpty(entry.TarHeader.LinkName))
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            FileHelper.CreateSymbolicLink(filePath, entry.TarHeader.LinkName);
        }
        else
        {
            using FileStream outputStream = File.Create(filePath);
            await tarStream.CopyEntryContentsAsync(outputStream, cancellationToken);
        }
    }
}
