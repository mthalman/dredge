using ICSharpCode.SharpZipLib.Tar;
using Newtonsoft.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge;

public class ImageCommand : Command
{
    // See https://github.com/opencontainers/image-spec/blob/main/layer.md#whiteouts
    private const string WhiteoutMarkerPrefix = ".wh.";
    private const string OpaqueWhiteoutMarker = ".wh..wh..opq";

    private static readonly string DredgeTempPath = Path.Combine(Path.GetTempPath(), "Valleysoft.Dredge");
    private static readonly string LayersTempPath = Path.Combine(DredgeTempPath, "layers");

    public ImageCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("image", "Commands related to container images")
    {
        AddCommand(new CompareCommand(dockerRegistryClientFactory));
        AddCommand(new InspectCommand(dockerRegistryClientFactory));
        AddCommand(new OsCommand(dockerRegistryClientFactory));
        AddCommand(new SaveLayersCommand(dockerRegistryClientFactory));
    }

    private static DockerManifestV2 GetManifest(string image, ManifestInfo manifestInfo)
    {
        if (manifestInfo.Manifest is ManifestList)
        {
            throw new NotSupportedException(
                $"The name '{image}' is a manifest list and doesn't directly refer to an image. Resolve the manifest name to an image first by using the \"dredge manifest resolve\" command.");
        }

        if (manifestInfo.Manifest is not DockerManifestV2 manifest)
        {
            throw new NotSupportedException(
                $"The image name '{image}' has a media type of '{manifestInfo.MediaType}' which is not supported.");
        }

        return manifest;
    }

    private static async Task SaveImageLayersToDiskAsync(
        IDockerRegistryClientFactory dockerRegistryClientFactory, string image, string destPath, int? layerIndex,
        string layerIndexOptionName, bool noSquash)
    {
        // Spec for OCI image layer filesystem changeset: https://github.com/opencontainers/image-spec/blob/main/layer.md

        Console.Error.WriteLine($"Getting layers for {image}");

        ImageName imageName = ImageName.Parse(image);
        IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(imageName.Registry);
        ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

        DockerManifestV2 manifest = GetManifest(imageName.ToString(), manifestInfo);
        
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
            string layerDir = Path.Combine(LayersTempPath, layerName);
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

    public class InspectCommand : Command
    {
        private readonly IDockerRegistryClientFactory dockerRegistryClientFactory;

        public InspectCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("inspect", "Return low-level information on a container image")
        {
            Argument<string> imageArg = new("image", "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)");
            AddArgument(imageArg);

            this.SetHandler(ExecuteAsync, imageArg);
            this.dockerRegistryClientFactory = dockerRegistryClientFactory;
        }

        private Task ExecuteAsync(string image)
        {
            ImageName imageName = ImageName.Parse(image);
            return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
            {
                using IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(imageName.Registry);
                ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

                DockerManifestV2 manifest = GetManifest(image, manifestInfo);
                string? digest = manifest.Config?.Digest;
                if (digest is null)
                {
                    throw new NotSupportedException($"Could not resolve the image config digest of '{image}'.");
                }

                Stream blob = await client.Blobs.GetAsync(imageName.Repo, digest);
                using StreamReader reader = new(blob);
                string content = await reader.ReadToEndAsync();
                object json = JsonConvert.DeserializeObject(content);
                string output = JsonConvert.SerializeObject(json, Formatting.Indented);
                Console.Out.WriteLine(output);
            });
        }
    }

    public class OsCommand : Command
    {
        private readonly IDockerRegistryClientFactory dockerRegistryClientFactory;

        public OsCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("os", "Gets OS info about the container image")
        {
            Argument<string> imageArg = new("image", "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)");
            AddArgument(imageArg);
            this.SetHandler(ExecuteAsync, imageArg);
            this.dockerRegistryClientFactory = dockerRegistryClientFactory;
        }

        private Task ExecuteAsync(string image)
        {
            ImageName imageName = ImageName.Parse(image);
            return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
            {
                using IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(imageName.Registry);
                ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

                DockerManifestV2 manifest = GetManifest(image, manifestInfo);

                string? configDigest = manifest.Config?.Digest;
                if (configDigest is null)
                {
                    throw new NotSupportedException($"Could not resolve the image config digest of '{image}'.");
                }

                Image imageConfig = await client.Blobs.GetImageAsync(imageName.Repo, configDigest);

                ManifestLayer baseLayer = manifest.Layers.First();
                if (baseLayer.Digest is null)
                {
                    throw new Exception($"No digest was found for the base layer of '{image}'.");
                }

                object? osInfo;
                if (imageConfig.Os.Equals("windows", StringComparison.OrdinalIgnoreCase))
                {
                    osInfo = await GetWindowsOsInfoAsync(imageConfig, baseLayer.Digest);
                }
                else
                {
                    osInfo = await GetLinuxOsInfoAsync(client, imageName, baseLayer.Digest);
                }

                if (osInfo is null)
                {
                    throw new Exception("Unable to derive OS information from the image.");
                }

                string output = JsonConvert.SerializeObject(osInfo, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.Indented
                });
                Console.Out.WriteLine(output);
            });
        }

        private static async Task<LinuxOsInfo?> GetLinuxOsInfoAsync(IDockerRegistryClient client, ImageName imageName, string baseLayerDigest)
        {
            using Stream blobStream = await client.Blobs.GetAsync(imageName.Repo, baseLayerDigest);
            using GZipStream gZipStream = new(blobStream, CompressionMode.Decompress);

            // Can't use System.Formats.Tar.TarReader because it fails to read certain types of tarballs:
            // https://github.com/dotnet/runtime/issues/74316#issuecomment-1312227247

            using TarInputStream tarStream = new(gZipStream, Encoding.UTF8);
            TarEntry? entry = null;
            do
            {
                entry = tarStream.GetNextEntry();

                // Look for the os-release file (skip symlinks)
                if (entry is not null &&
                    entry.Size > 0 &&
                    (entry.Name == "etc/os-release" || entry.Name == "usr/lib/os-release"))
                {
                    using MemoryStream memStream = new();
                    tarStream.CopyEntryContents(memStream);
                    memStream.Position = 0;
                    using StreamReader reader = new(memStream);
                    string content = await reader.ReadToEndAsync();
                    return LinuxOsInfo.Parse(content);
                }
            } while (entry is not null);

            return null;
        }

        private async Task<WindowsOsInfo?> GetWindowsOsInfoAsync(Image imageConfig, string baseLayerDigest)
        {
            using IDockerRegistryClient mcrClient =
                await dockerRegistryClientFactory.GetClientAsync("mcr.microsoft.com");

            if (await mcrClient.Blobs.ExistsAsync("windows/nanoserver", baseLayerDigest))
            {
                return new(WindowsType.NanoServer, imageConfig.OsVersion);
            }
            else if (await mcrClient.Blobs.ExistsAsync("windows/servercore", baseLayerDigest))
            {
                return new(WindowsType.ServerCore, imageConfig.OsVersion);
            }
            else if (await mcrClient.Blobs.ExistsAsync("windows/server", baseLayerDigest))
            {
                return new(WindowsType.Server, imageConfig.OsVersion);
            }
            else if (await mcrClient.Blobs.ExistsAsync("windows", baseLayerDigest))
            {
                return new(WindowsType.Windows, imageConfig.OsVersion);
            }
            else
            {
                return null;
            }
        }
    }

    public class CompareCommand : Command
    {
        public CompareCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("compare", "Compares two images")
        {
            AddCommand(new LayersCommand(dockerRegistryClientFactory));
            AddCommand(new FilesCommand(dockerRegistryClientFactory));
        }

        public class LayersCommand : Command
        {
            private readonly IDockerRegistryClientFactory dockerRegistryClientFactory;

            public LayersCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("layers", "Compares two images by layers")
            {
                Argument<string> baseImageArg = new("base", "Name of the base container image (<image>, <image>:<tag>, or <image>@<digest>)");
                AddArgument(baseImageArg);

                Argument<string> targetImageArg = new("target", "Name of the target container image (<image>, <image>:<tag>, or <image>@<digest>)");
                AddArgument(targetImageArg);

                Option<CompareLayersOutput> outputOption = new("--output", () => CompareLayersOutput.SideBySide, "Output format");
                AddOption(outputOption);

                Option<bool> noColorOption = new("--no-color", "Disables dependency on color in comparison results");
                AddOption(noColorOption);

                Option<bool> historyOption = new("--history", "Include layer history as part of the comparison");
                AddOption(historyOption);

                this.SetHandler(ExecuteAsync, baseImageArg, targetImageArg, outputOption, noColorOption, historyOption);
                this.dockerRegistryClientFactory = dockerRegistryClientFactory;
            }

            private Task ExecuteAsync(string baseImage, string targetImage, CompareLayersOutput outputFormat, bool isColorDisabled, bool includeHistory)
            {
                return CommandHelper.ExecuteCommandAsync(registry: null, async () =>
                {
                    IRenderable output = await GetOutputAsync(baseImage, targetImage, outputFormat, isColorDisabled, includeHistory);
                    AnsiConsole.Write(output);
                });
            }

            public async Task<IRenderable> GetOutputAsync(
                string baseImage, string targetImage, CompareLayersOutput outputFormat, bool isColorDisabled, bool includeHistory)
            {
                CompareLayersResult result = await GetCompareLayersResult(baseImage, targetImage, includeHistory);
                OutputFormatter formatter = OutputFormatter.Create(outputFormat);
                IRenderable output = formatter.GetOutput(result, baseImage, targetImage, isColorDisabled, includeHistory);
                return output;
            }

            private async Task<CompareLayersResult> GetCompareLayersResult(string baseImage, string targetImage, bool includeHistory)
            {
                IList<LayerInfo> baseLayers = await GetLayersAsync(baseImage, includeHistory);
                IList<LayerInfo> targetLayers = await GetLayersAsync(targetImage, includeHistory);
                List<LayerComparison> layerComparisons = GetLayerComparisons(baseLayers, targetLayers);
                CompareLayersSummary summary = GetSummary(layerComparisons);

                return new CompareLayersResult(
                    summary,
                    layerComparisons);
            }

            private static CompareLayersSummary GetSummary(List<LayerComparison> layerComparisons)
            {
                bool areEqual = layerComparisons.All(comparison => comparison.LayerDiff == LayerDiff.Equal);
                bool targetIncludesAllBaseLayers =
                    areEqual ||
                    !layerComparisons
                        .Any(comparison => comparison.LayerDiff == LayerDiff.NotEqual || comparison.LayerDiff == LayerDiff.Removed);
                int lastCommonLayerIndex = -1;
                if (areEqual)
                {
                    lastCommonLayerIndex = layerComparisons.Count - 1;
                }
                else
                {
                    int equalLayerCount = layerComparisons
                        .TakeWhile(comparison => comparison.LayerDiff == LayerDiff.Equal)
                        .Count();
                    if (equalLayerCount >= 0)
                    {
                        lastCommonLayerIndex = equalLayerCount - 1;
                    }
                }

                CompareLayersSummary summary = new(areEqual, targetIncludesAllBaseLayers, lastCommonLayerIndex);
                return summary;
            }

            private static List<LayerComparison> GetLayerComparisons(IList<LayerInfo> baseLayers, IList<LayerInfo> targetLayers)
            {
                List<LayerComparison> layerComparisons = new();
                int max = Math.Max(baseLayers.Count, targetLayers.Count);
                for (int i = 0; i < max; i++)
                {
                    LayerInfo? baseLayer = null;
                    LayerInfo? targetLayer = null;
                    if (i < baseLayers.Count)
                    {
                        baseLayer = baseLayers[i];
                    }
                    if (i < targetLayers.Count)
                    {
                        targetLayer = targetLayers[i];
                    }

                    LayerDiff diff = GetLayerDiff(baseLayer, targetLayer);
                    layerComparisons.Add(new LayerComparison(baseLayer, targetLayer, diff));
                }

                return layerComparisons;
            }

            private static LayerDiff GetLayerDiff(LayerInfo? baseLayer, LayerInfo? targetLayer)
            {
                if (baseLayer is null)
                {
                    if (targetLayer is null)
                    {
                        throw new Exception("Unexpected layer result: two null layers");
                    }

                    return LayerDiff.Added;
                }
                else
                {
                    if (targetLayer is null)
                    {
                        return LayerDiff.Removed;
                    }
                    else
                    {
                        if (string.Equals(baseLayer.Digest, targetLayer.Digest, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(baseLayer.History, targetLayer.History, StringComparison.Ordinal))
                        {
                            return LayerDiff.Equal;
                        }
                        else
                        {
                            return LayerDiff.NotEqual;
                        }
                    }
                }
            }

            private static Color GetLayerDiffColor(LayerDiff diff, bool isBaseLayer, bool isColorDisabled) =>
                isColorDisabled ? Color.Default : diff switch
                {
                    LayerDiff.Removed => Color.Red,
                    LayerDiff.Added => Color.Green,
                    LayerDiff.NotEqual => isBaseLayer ? Color.Red : Color.Green,
                    LayerDiff.Equal => Color.Default,
                    _ => throw new NotImplementedException()
                };

            private static Markup GetHistoryMarkup(LayerInfo? layer, LayerDiff diff, bool isBase, bool isColorDisabled,
                bool isInline) =>
                new(
                    Markup.Escape($"{GetTextOffset(diff, isInline, isBase)}{layer?.History ?? string.Empty}"),
                    new Style(GetLayerDiffColor(diff, isBaseLayer: isBase, isColorDisabled)));

            private static Markup GetDigestMarkup(LayerInfo? layer, LayerDiff diff, bool isBase, bool isColorDisabled,
                bool includeHistory, bool isInline)
            {
                if (layer is null)
                {
                    return new Markup(string.Empty);
                }

                string digestMarkup = string.Empty;
                if (includeHistory)
                {
                    digestMarkup += $"[{(includeHistory ? Decoration.Invert : Decoration.None).ToString().ToLower()}]";
                }

                digestMarkup += $"{Markup.Escape(layer?.Digest ?? "<empty layer>")}";

                if (includeHistory)
                {
                    // Close the markup tag
                    digestMarkup += "[/]";
                }

                return new(
                    $"{GetTextOffset(diff, isInline, isBase)}{digestMarkup}",
                    new Style(GetLayerDiffColor(diff, isBase, isColorDisabled)));
            }

            private static string GetTextOffset(LayerDiff diff, bool isInline, bool isBase) =>
                !isInline ? string.Empty : diff switch
                {
                    LayerDiff.Added => "+ ",
                    LayerDiff.Equal => "  ",
                    LayerDiff.NotEqual => isBase ? "- " : "+ ",
                    LayerDiff.Removed => "- ",
                    _ => throw new NotImplementedException()
                };

            private async Task<IList<LayerInfo>> GetLayersAsync(string image, bool includeHistory)
            {
                ImageName imageName = ImageName.Parse(image);
                using IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(imageName.Registry);
                ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

                DockerManifestV2 manifest = GetManifest(image, manifestInfo);

                string? digest = manifest.Config?.Digest;
                if (digest is null)
                {
                    throw new NotSupportedException($"Could not resolve the image config digest of '{image}'.");
                }

                Image imageConfig = await client.Blobs.GetImageAsync(imageName.Repo, digest);

                List<LayerInfo> layerInfos = new();
                int layerIndex = 0;
                foreach (LayerHistory history in imageConfig.History)
                {
                    if (includeHistory || !history.IsEmptyLayer)
                    {
                        string? layerDigest = !history.IsEmptyLayer ? manifest.Layers[layerIndex++].Digest : null;
                        layerInfos.Add(new LayerInfo(layerDigest, includeHistory ? history.CreatedBy : null));
                    }
                }

                return layerInfos;
            }

            private abstract class OutputFormatter
            {
                public static OutputFormatter Create(CompareLayersOutput outputFormat) =>
                    outputFormat switch
                    {
                        CompareLayersOutput.Inline => new InlineFormatter(),
                        CompareLayersOutput.Json => new JsonFormatter(),
                        CompareLayersOutput.SideBySide => new SideBySideFormatter(),
                        _ => throw new NotImplementedException()
                    };


                public abstract IRenderable GetOutput(CompareLayersResult result, string baseImage, string targetImage, bool isColorDisabled,
                    bool includeHistory);

                private class SideBySideFormatter : OutputFormatter
                {
                    public override IRenderable GetOutput(CompareLayersResult result, string baseImage, string targetImage, bool isColorDisabled, bool includeHistory)
                    {
                        Table table = new Table()
                            .AddColumn(baseImage);

                        if (isColorDisabled)
                        {
                            // Use a comparison column to indicate the diff result with text instead of color
                            table.AddColumn(new TableColumn("Compare") { Alignment = Justify.Center });
                        }

                        table.AddColumn(targetImage);

                        for (int i = 0; i < result.LayerComparisons.Count(); i++)
                        {
                            AddTableRows(result, isColorDisabled, includeHistory, table, i);
                        }

                        return table;
                    }

                    private static void AddTableRows(CompareLayersResult result, bool isColorDisabled, bool includeHistory, Table table, int i)
                    {
                        LayerComparison layerComparison = result.LayerComparisons.ElementAt(i);
                        IEnumerable<IRenderable> digestRowCells =
                            GetDigestRowCells(isColorDisabled, includeHistory, layerComparison);
                        table.AddRow(digestRowCells);

                        if (includeHistory)
                        {
                            List<IRenderable> historyRowCells = GetHistoryRowCells(isColorDisabled, layerComparison);
                            table.AddRow(historyRowCells);

                            if (i + 1 != result.LayerComparisons.Count())
                            {
                                table.AddEmptyRow();
                            }
                        }
                    }

                    private static List<IRenderable> GetHistoryRowCells(bool isColorDisabled, LayerComparison layerComparison)
                    {
                        List<IRenderable> historyCells = new()
                        {
                            GetHistoryMarkup(layerComparison.Base, layerComparison.LayerDiff, isBase: true, isColorDisabled, isInline: false)
                        };

                        if (isColorDisabled)
                        {
                            historyCells.Add(new Markup(string.Empty));
                        }

                        historyCells.Add(GetHistoryMarkup(layerComparison.Target, layerComparison.LayerDiff, isBase: false, isColorDisabled, isInline: false));
                        return historyCells;
                    }

                    private static IEnumerable<IRenderable> GetDigestRowCells(bool isColorDisabled, bool includeHistory, LayerComparison layerComparison)
                    {
                        List<IRenderable> shaCells = new()
                        {
                            GetDigestMarkup(
                                layerComparison.Base, layerComparison.LayerDiff, isBase : true, isColorDisabled, includeHistory, isInline: false)
                        };
                        if (isColorDisabled)
                        {
                            shaCells.Add(new Markup(GetLayerDiffDisplayName(layerComparison.LayerDiff)));
                        }

                        shaCells.Add(
                            GetDigestMarkup(
                                layerComparison.Target, layerComparison.LayerDiff, isBase: false, isColorDisabled, includeHistory, isInline: false));
                        return shaCells;
                    }

                    private static string GetLayerDiffDisplayName(LayerDiff diff) =>
                        typeof(LayerDiff).GetMember(diff.ToString()).Single().GetCustomAttribute<EnumMemberAttribute>()?.Value ??
                            throw new Exception($"Enum member not set for {diff}.");
                }

                private class InlineFormatter : OutputFormatter
                {
                    public override IRenderable GetOutput(CompareLayersResult result, string baseImage, string targetImage, bool isColorDisabled, bool includeHistory)
                    {
                        List<IRenderable> rows = new();

                        for (int i = 0; i < result.LayerComparisons.Count(); i++)
                        {
                            LayerComparison layerComparison = result.LayerComparisons.ElementAt(i);

                            if (layerComparison.Base is not null)
                            {
                                AddInlineLayerInfo(rows, layerComparison.Base, layerComparison.LayerDiff, isBase: true, isColorDisabled, includeHistory);
                            }

                            if (layerComparison.LayerDiff != LayerDiff.Equal && layerComparison.Target is not null)
                            {
                                AddInlineLayerInfo(rows, layerComparison.Target, layerComparison.LayerDiff, isBase: false, isColorDisabled, includeHistory);
                            }

                            if (includeHistory && i + 1 != result.LayerComparisons.Count())
                            {
                                rows.Add(new Text(string.Empty));
                            }
                        }

                        return new Rows(rows);
                    }

                    private static void AddInlineLayerInfo(List<IRenderable> rows, LayerInfo layer, LayerDiff diff, bool isBase,
                        bool isColorDisabled, bool includeHistory)
                    {
                        rows.Add(GetDigestMarkup(layer, diff, isBase, isColorDisabled, includeHistory, isInline: true));
                        if (includeHistory)
                        {
                            rows.Add(GetHistoryMarkup(layer, diff, isBase, isColorDisabled, isInline: true));
                        }
                    }
                }

                private class JsonFormatter : OutputFormatter
                {
                    public override IRenderable GetOutput(CompareLayersResult result, string baseImage, string targetImage, bool isColorDisabled, bool includeHistory)
                    {
                        string output = JsonConvert.SerializeObject(result, new JsonSerializerSettings
                        {
                            NullValueHandling = NullValueHandling.Ignore,
                            Formatting = Formatting.Indented
                        });

                        return new Text(output);
                    }
                }
            }
        }

        public class FilesCommand : Command
        {           
            private const string BaseArg = "base";
            private const string TargetArg = "target";
            private const string LayerIndexSuffix = "-layer-index";

            private readonly IDockerRegistryClientFactory dockerRegistryClientFactory;

            private const string BaseOutputDirName = "base";
            private const string TargetOutputDirName = "target";

            private static readonly string CompareTempPath = Path.Combine(DredgeTempPath, "compare");

            public FilesCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
                : base("files", "Compares two images by their files")
            {
                Argument<string> baseImageArg = new(BaseArg, "Name of the base container image (<image>, <image>:<tag>, or <image>@<digest>)");
                AddArgument(baseImageArg);

                Argument<string> targetImageArg = new(TargetArg, "Name of the target container image (<image>, <image>:<tag>, or <image>@<digest>)");
                AddArgument(targetImageArg);

                Option<int?> baseLayerIndex = new($"--{BaseArg}{LayerIndexSuffix}", "Non-empty layer index of the base container image to compare with");
                AddOption(baseLayerIndex);

                Option<int?> targetLayerIndex = new($"--{TargetArg}{LayerIndexSuffix}", "Non-empty layer index of the target container image to compare against");
                AddOption(targetLayerIndex);

                Option<CompareFilesOutput> outputOption = new("--output", () => CompareFilesOutput.ExternalTool, "Output type");
                AddOption(outputOption);

                this.SetHandler(ExecuteAsync, baseImageArg, targetImageArg, baseLayerIndex, targetLayerIndex, outputOption);
                this.dockerRegistryClientFactory = dockerRegistryClientFactory;
            }

            private Task ExecuteAsync(
                string baseImage, string targetImage, int? baseLayerIndex, int? targetLayerIndex, CompareFilesOutput outputType)
            {
                return CommandHelper.ExecuteCommandAsync(registry: null, async () =>
                {
                    Settings settings = Settings.Load();
                    if (settings.FileCompareTool is null ||
                        settings.FileCompareTool.ExePath == string.Empty ||
                        settings.FileCompareTool.Args == string.Empty)
                    {
                        throw new Exception(
                            $"This command requires additional configuration.{Environment.NewLine}In order to compare files, you must first set the '{Settings.FileCompareToolName}' setting in {Settings.SettingsPath}. This is an external tool of your choosing that will be executed to compare two directories containing files of the specified images. Use '{{0}}' and '{{1}}' placeholders in the args to indicate the base and target path locations that will be the inputs to the compare tool.");
                    }

                    await SaveImageLayersToDiskAsync(baseImage, BaseOutputDirName, baseLayerIndex, BaseArg);
                    Console.Error.WriteLine();
                    await SaveImageLayersToDiskAsync(targetImage, TargetOutputDirName, targetLayerIndex, TargetArg);

                    string args = settings.FileCompareTool.Args
                        .Replace("{0}", Path.Combine(CompareTempPath, BaseOutputDirName))
                        .Replace("{1}", Path.Combine(CompareTempPath, TargetOutputDirName));
                    Process.Start(settings.FileCompareTool.ExePath, args);
                });
            }

            private Task SaveImageLayersToDiskAsync(string image, string outputDirName, int? layerIndex, string layerIndexArg)
            {
                string workingDir = Path.Combine(CompareTempPath, outputDirName);
                if (Directory.Exists(workingDir))
                {
                    Directory.Delete(workingDir, recursive: true);
                }

                return ImageCommand.SaveImageLayersToDiskAsync(
                    dockerRegistryClientFactory,
                    image,
                    workingDir,
                    layerIndex,
                    layerIndexArg + LayerIndexSuffix,
                    noSquash: false);
            }
        }
    }

    public class SaveLayersCommand : Command
    {
        private const string LayerIndexOptionName = "--layer-index";
        private readonly IDockerRegistryClientFactory dockerRegistryClientFactory;

        public SaveLayersCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
            : base("save-layers", "Saves an image's extracted layers to disk")
        {
            this.dockerRegistryClientFactory = dockerRegistryClientFactory;

            Argument<string> imageArg = new("image", "Name of the container image (<image>, <image>:<tag>, or <image>@<digest>)");
            AddArgument(imageArg);

            Argument<string> outputPathArg = new("output-path", "Path to the output location");
            AddArgument(outputPathArg);

            Option<bool> noSquashOption = new("--no-squash", "Do not squash the image layers");
            AddOption(noSquashOption);

            Option<int?> layerIndexOption = new(LayerIndexOptionName, "Index of the image layer to target");
            AddOption(layerIndexOption);

            this.SetHandler(ExecuteAsync, imageArg, outputPathArg, noSquashOption, layerIndexOption);
        }

        private Task ExecuteAsync(string image, string outputPath, bool noSquash, int? layerIndex)
        {
            ImageName imageName = ImageName.Parse(image);
            return CommandHelper.ExecuteCommandAsync(imageName.Registry, async () =>
            {
                using IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(imageName.Registry);
                ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

                DockerManifestV2 manifest = GetManifest(image, manifestInfo);
                string? digest = manifest.Config?.Digest;
                if (digest is null)
                {
                    throw new NotSupportedException($"Could not resolve the image config digest of '{image}'.");
                }

                await SaveImageLayersToDiskAsync(
                    dockerRegistryClientFactory,
                    image,
                    outputPath,
                    layerIndex,
                    LayerIndexOptionName,
                    noSquash);
            });
        }
    }
}
