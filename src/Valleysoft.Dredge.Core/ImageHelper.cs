using ICSharpCode.SharpZipLib.Tar;
using System.IO.Compression;
using System.Text;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using ImageConfig = Valleysoft.DockerRegistryClient.Models.Image;

namespace Valleysoft.Dredge.Core;

public static class ImageHelper
{
    // See https://github.com/opencontainers/image-spec/blob/main/layer.md#whiteouts
    private const string WhiteoutMarkerPrefix = ".wh.";
    private const string OpaqueWhiteoutMarker = ".wh..wh..opq";

    public static async Task SaveImageLayersToDiskAsync(
        IDockerRegistryClientFactory dockerRegistryClientFactory, string image, string destPath, int? layerIndex,
        string layerIndexOptionName, bool noSquash, string layersCachePath, AppSettings appSettings, PlatformOptions options)
    {
        // Spec for OCI image layer filesystem changeset: https://github.com/opencontainers/image-spec/blob/main/layer.md

        Console.Error.WriteLine($"Getting layers for {image}");

        ImageName imageName = ImageName.Parse(image);
        IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(imageName.Registry);
        DockerManifestV2 manifest = (await ManifestHelper.GetResolvedManifestAsync(client, imageName, appSettings, options)).Manifest;

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
            ManifestLayer layer = manifest.Layers[i];
            if (string.IsNullOrEmpty(layer.Digest))
            {
                throw new Exception($"Layer digest not set for image '{imageName}'");
            }

            Console.Error.WriteLine($"Layer {layer.Digest}");

            string layerName = layer.Digest[(layer.Digest.IndexOf(':') + 1)..];
            string layerDir = Path.Combine(layersCachePath, layerName);
            if (Directory.Exists(layerDir))
            {
                Console.Error.WriteLine($"\tUsing cached layer on disk...");
            }
            else
            {
                Console.Error.WriteLine($"\tDownloading layer...");
                using Stream layerStream = await client.Blobs.GetAsync(imageName.Repo, layer.Digest);

                await ExtractLayerAsync(layerStream, layerDir);
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
            await ExtractTarEntry(layerDir, tarStream, entry, entryName);
        }
    }

    private static void ApplyLayer(string layerDir, string workingDir)
    {
        Console.Error.WriteLine($"\tApplying layer...");

        FileInfo[] layerFiles = new DirectoryInfo(layerDir).GetFiles("*", SearchOption.AllDirectories);

        foreach (FileInfo layerFile in layerFiles)
        {
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

                    File.CreateSymbolicLink(dest, layerFile.LinkTarget);
                }
                else
                {
                    File.Copy(layerFile.FullName, dest, overwrite: true);
                }
            }
        }
    }

    private static async Task ExtractTarEntry(string workingDir, TarInputStream tarStream, TarEntry entry, string entryName)
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

            File.CreateSymbolicLink(filePath, entry.TarHeader.LinkName);
        }
        else
        {
            using FileStream outputStream = File.Create(filePath);
            await tarStream.CopyEntryContentsAsync(outputStream, CancellationToken.None);
        }
    }
}
