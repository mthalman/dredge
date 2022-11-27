using ICSharpCode.SharpZipLib.Tar;
using Newtonsoft.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;

namespace Valleysoft.Dredge;

public class ImageCommand : Command
{
    public ImageCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("image", "Commands related to container images")
    {
        AddCommand(new InspectCommand(dockerRegistryClientFactory));
        AddCommand(new OsCommand(dockerRegistryClientFactory));
        AddCommand(new CompareCommand(dockerRegistryClientFactory));
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
                bool areEqual = layerComparisons.All(comparison => comparison.LayerDiff == DiffResult.Equal);
                bool targetIncludesAllBaseLayers =
                    areEqual ||
                    !layerComparisons
                        .Any(comparison => comparison.LayerDiff == DiffResult.NotEqual || comparison.LayerDiff == DiffResult.Removed);
                int lastCommonLayerIndex = -1;
                if (areEqual)
                {
                    lastCommonLayerIndex = layerComparisons.Count - 1;
                }
                else
                {
                    int equalLayerCount = layerComparisons
                        .TakeWhile(comparison => comparison.LayerDiff == DiffResult.Equal)
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

                    DiffResult diff = GetLayerDiff(baseLayer, targetLayer);
                    layerComparisons.Add(new LayerComparison(baseLayer, targetLayer, diff));
                }

                return layerComparisons;
            }

            private static DiffResult GetLayerDiff(LayerInfo? baseLayer, LayerInfo? targetLayer)
            {
                if (baseLayer is null)
                {
                    if (targetLayer is null)
                    {
                        throw new Exception("Unexpected layer result: two null layers");
                    }

                    return DiffResult.Added;
                }
                else
                {
                    if (targetLayer is null)
                    {
                        return DiffResult.Removed;
                    }
                    else
                    {
                        if (string.Equals(baseLayer.Digest, targetLayer.Digest, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(baseLayer.History, targetLayer.History, StringComparison.Ordinal))
                        {
                            return DiffResult.Equal;
                        }
                        else
                        {
                            return DiffResult.NotEqual;
                        }
                    }
                }
            }

            private static Color GetLayerDiffColor(DiffResult diff, bool isBaseLayer, bool isColorDisabled) =>
                isColorDisabled ? Color.Default : diff switch
                {
                    DiffResult.Removed => Color.Red,
                    DiffResult.Added => Color.Green,
                    DiffResult.NotEqual => isBaseLayer ? Color.Red : Color.Green,
                    DiffResult.Equal => Color.Default,
                    _ => throw new NotImplementedException()
                };

            private static Markup GetHistoryMarkup(LayerInfo? layer, DiffResult diff, bool isBase, bool isColorDisabled,
                bool isInline) =>
                new(
                    Markup.Escape($"{GetTextOffset(diff, isInline, isBase)}{layer?.History ?? string.Empty}"),
                    new Style(GetLayerDiffColor(diff, isBaseLayer: true, isColorDisabled)));

            private static Markup GetDigestMarkup(LayerInfo? layer, DiffResult diff, bool isBase, bool isColorDisabled,
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
                    digestMarkup += "[/]";
                }

                return new(
                    $"{GetTextOffset(diff, isInline, isBase)}{digestMarkup}",
                    new Style(GetLayerDiffColor(diff, isBase, isColorDisabled)));
            }

            private static string GetTextOffset(DiffResult diff, bool isInline, bool isBase) =>
                !isInline ? string.Empty : diff switch
                {
                    DiffResult.Added => "+ ",
                    DiffResult.Equal => "  ",
                    DiffResult.NotEqual => isBase ? "- " : "+ ",
                    DiffResult.Removed => "- ",
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

                    private static string GetLayerDiffDisplayName(DiffResult diff) =>
                        typeof(DiffResult).GetMember(diff.ToString()).Single().GetCustomAttribute<EnumMemberAttribute>()?.Value ??
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

                            if (layerComparison.LayerDiff != DiffResult.Equal && layerComparison.Target is not null)
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

                    private static void AddInlineLayerInfo(List<IRenderable> rows, LayerInfo layer, DiffResult diff, bool isBase,
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

        // Spec for OCI image layer filesystem changeset: https://github.com/opencontainers/image-spec/blob/main/layer.md
        public class FilesCommand : Command
        {
            private readonly IDockerRegistryClientFactory dockerRegistryClientFactory;

            public FilesCommand(IDockerRegistryClientFactory dockerRegistryClientFactory) : base("files", "Compares the files of two images")
            {
                Argument<string> baseImageArg = new("base", "Name of the base container image (<image>, <image>:<tag>, or <image>@<digest>)");
                AddArgument(baseImageArg);

                Argument<string> targetImageArg = new("target", "Name of the target container image (<image>, <image>:<tag>, or <image>@<digest>)");
                AddArgument(targetImageArg);

                Option<CompareFilesOutput> outputOption = new("--output", () => CompareFilesOutput.Json, "Output format");
                AddOption(outputOption);

                this.SetHandler(ExecuteAsync, baseImageArg, targetImageArg, outputOption);
                this.dockerRegistryClientFactory = dockerRegistryClientFactory;
            }

            private Task ExecuteAsync(string baseImage, string targetImage, CompareFilesOutput output)
            {
                return CommandHelper.ExecuteCommandAsync(registry: null, async () =>
                {
                    switch (output)
                    {
                        case CompareFilesOutput.Json:
                            await OutputJsonAsync(baseImage, targetImage);
                            break;
                        case CompareFilesOutput.External:
                            break;
                        default:
                            throw new NotImplementedException();
                    }
                });
            }

            private class FileDiffResult
            {
                public string? BaseChecksum { get; set; }
                public string? TargetChecksum { get; set; }
                public DiffResult Diff { get; set; }
            }

            private async Task OutputJsonAsync(string baseImage, string targetImage)
            {
                Dictionary<string, FileDiffResult> diffResults = new();
                await GetFileDiffsAsync(baseImage, isBase: true, diffResults);
                await GetFileDiffsAsync(targetImage, isBase: false, diffResults);

                string output = JsonConvert.SerializeObject(diffResults, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = Formatting.Indented
                });
                Console.Out.WriteLine(output);
            }

            private async Task GetFileDiffsAsync(string image, bool isBase, Dictionary<string, FileDiffResult> diffResults)
            {
                ImageName imageName = ImageName.Parse(image);
                IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(imageName.Registry);
                ManifestInfo manifestInfo = await client.Manifests.GetAsync(imageName.Repo, (imageName.Tag ?? imageName.Digest)!);

                DockerManifestV2 manifest = GetManifest(imageName.ToString(), manifestInfo);
                foreach (ManifestLayer layer in manifest.Layers)
                {
                    if (string.IsNullOrEmpty(layer.Digest))
                    {
                        throw new Exception($"Layer digest not set for image '{imageName}'");
                    }

                    using Stream layerStream = await client.Blobs.GetAsync(imageName.Repo, layer.Digest);
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

                        if (diffResults.TryGetValue(entry.Name, out FileDiffResult? diffResult))
                        {
                            Debug.Assert(!isBase);

                            if (entry.IsDirectory)
                            {
                                diffResults.Remove(entry.Name);
                            }
                            else
                            {
                                diffResult.TargetChecksum = GetChecksum(tarStream);

                                if (diffResult.BaseChecksum is not null)
                                {
                                    diffResult.Diff = (diffResult.BaseChecksum == diffResult.TargetChecksum) ? DiffResult.Equal : DiffResult.NotEqual;
                                }
                                else
                                {
                                    diffResult.Diff = DiffResult.Added;
                                }
                            }
                        }
                        else
                        {
                            string checksum = GetChecksum(tarStream);
                            if (isBase)
                            {
                                diffResult = new FileDiffResult()
                                {
                                    BaseChecksum = entry.IsDirectory ? null : checksum,
                                    Diff = DiffResult.Removed
                                };
                            }
                            else
                            {
                                diffResult = new FileDiffResult()
                                {
                                    TargetChecksum = entry.IsDirectory ? null : checksum,
                                    Diff = DiffResult.Added
                                };
                            }
                            
                            diffResults.Add(entry.Name, diffResult);
                        }
                    }
                }
            }

            private static string GetChecksum(TarInputStream tarStream)
            {
                using MemoryStream memStream = new();
                using SHA256 sha256 = SHA256.Create();
                tarStream.CopyEntryContents(memStream);
                memStream.Position = 0;
                byte[] hash = sha256.ComputeHash(memStream);
                return BitConverter.ToString(hash).Replace("-", string.Empty);
            }
        }
    }
}
