using System.Formats.Tar;
using Newtonsoft.Json;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using ImageConfig = Valleysoft.DockerRegistryClient.Models.Images.Image;

namespace Valleysoft.Dredge.Commands.Image;

public partial class OsCommand : RegistryCommandBase<OsOptions>
{
    private static readonly WindowsImageDefinition[] windowsImageDefinitions =
    [
        new(WindowsType.NanoServer, "windows/nanoserver"),
        new(WindowsType.ServerCore, "windows/servercore"),
        new(WindowsType.Server, "windows/server"),
        new(WindowsType.Windows, "windows")
    ];

    private static readonly Regex osReleaseRegex = OsReleaseRegex();

    public OsCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : base("os", "Gets OS info about the container image", dockerRegistryClientFactory, output)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(imageName.Registry, cancellationToken, async ct =>
        {
            using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
            IImageManifest manifest =
                (await ManifestHelper.GetResolvedManifestAsync(client, imageName, Options, ct)).Manifest;

            string? configDigest = (manifest.Config?.Digest) ?? throw new NotSupportedException($"Could not resolve the image config digest of '{Options.Image}'.");
            ImageConfig imageConfig = await client.Blobs.GetImageAsync(imageName.Repo, configDigest, ct);

            object? osInfo;
            if (imageConfig.Os.Equals("windows", StringComparison.OrdinalIgnoreCase))
            {
                var windowsOsInfo =
                    await GetWindowsOsInfoAsync(imageConfig, manifest, DockerRegistryClientFactory, ct);
                osInfo = windowsOsInfo?.Info;
            }
            else
            {
                IDescriptor baseLayer = manifest.Layers.First();
                if (baseLayer.Digest is null)
                {
                    throw new Exception($"No digest was found for the base layer of '{Options.Image}'.");
                }

                osInfo = await GetLinuxOsInfoAsync(client, imageName, baseLayer.Digest, ct);
            }

            if (osInfo is null)
            {
                throw new Exception("Unable to derive OS information from the image.");
            }

            string output = JsonConvert.SerializeObject(osInfo, JsonHelper.SettingsNoCamelCase);
            Output.WriteLine(output);
        });
    }

    private static async Task<LinuxOsInfo?> GetLinuxOsInfoAsync(
        IDockerRegistryClient client,
        ImageName imageName,
        string baseLayerDigest,
        CancellationToken cancellationToken)
    {
        using Stream blobStream =
            await client.Blobs.GetAsync(imageName.Repo, baseLayerDigest, cancellationToken);
        using GZipStream gZipStream = new(blobStream, CompressionMode.Decompress);

        using TarReader tarReader = new(gZipStream, leaveOpen: true);
        TarEntry? entry = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            entry = await tarReader.GetNextEntryAsync(
                copyData: false,
                cancellationToken);

            // Look for the os-release file (skip symlinks)
            if (entry is not null &&
                entry.EntryType is not TarEntryType.SymbolicLink and not TarEntryType.HardLink &&
                entry.Length > 0 &&
                (osReleaseRegex.IsMatch(entry.Name)))
            {
                using MemoryStream memStream = new();
                await entry.DataStream!.CopyToAsync(memStream, cancellationToken);
                memStream.Position = 0;
                using StreamReader reader = new(memStream);
                string content = await reader.ReadToEndAsync(cancellationToken);
                return LinuxOsInfo.Parse(content);
            }
            if (entry?.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(Stream.Null, cancellationToken);
            }
        } while (entry is not null);

        return null;
    }

    internal static async Task<WindowsImageInfo?> GetWindowsOsInfoAsync(
        ImageConfig imageConfig,
        IImageManifest manifest,
        IDockerRegistryClientFactory dockerRegistryClientFactory, CancellationToken cancellationToken)
    {
        string? baseLayerDigest = manifest.Layers.FirstOrDefault()?.Digest;
        if (string.IsNullOrEmpty(baseLayerDigest))
        {
            throw new Exception("No digest was found for the base layer of the Windows image.");
        }

        using IDockerRegistryClient mcrClient =
            await dockerRegistryClientFactory.GetClientAsync(RegistryHelper.McrRegistry);

        foreach (WindowsImageDefinition definition in windowsImageDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await mcrClient.Blobs.ExistsAsync(definition.Repo, baseLayerDigest, cancellationToken))
            {
                int baseHistoryCount = await GetLegacyBaseHistoryCountAsync(
                    mcrClient, definition.Repo, manifest, cancellationToken);
                return new(new(definition.Type, imageConfig.OsVersion), definition.Repo, baseHistoryCount);
            }
        }

        if (string.IsNullOrEmpty(imageConfig.OsVersion) ||
            string.IsNullOrEmpty(imageConfig.Architecture) ||
            imageConfig.RootFilesystem?.DiffIds is not { Length: > 0 } targetDiffIds)
        {
            return null;
        }

        string baseImageTag = $"{imageConfig.OsVersion}-{imageConfig.Architecture}";
        WindowsImageInfo? bestMatch = null;
        int bestMatchLayerCount = 0;

        foreach (WindowsImageDefinition definition in windowsImageDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await mcrClient.Manifests.ExistsAsync(definition.Repo, baseImageTag, cancellationToken))
            {
                continue;
            }

            ManifestInfo manifestInfo =
                await mcrClient.Manifests.GetAsync(definition.Repo, baseImageTag, cancellationToken);
            if (manifestInfo.Manifest is not IImageManifest baseManifest ||
                string.IsNullOrEmpty(baseManifest.Config?.Digest))
            {
                continue;
            }

            ImageConfig baseImageConfig =
                await mcrClient.Blobs.GetImageAsync(
                    definition.Repo, baseManifest.Config.Digest, cancellationToken);
            string[]? baseDiffIds = baseImageConfig.RootFilesystem?.DiffIds;
            if (baseDiffIds is not { Length: > 0 } ||
                baseDiffIds.Length > targetDiffIds.Length ||
                !baseDiffIds.SequenceEqual(targetDiffIds.Take(baseDiffIds.Length)) ||
                baseDiffIds.Length <= bestMatchLayerCount)
            {
                continue;
            }

            bestMatchLayerCount = baseDiffIds.Length;
            bestMatch = new(
                new(definition.Type, imageConfig.OsVersion),
                definition.Repo,
                baseImageConfig.History.Length);
        }

        return bestMatch;
    }

    internal static async Task<(WindowsOsInfo Info, string Repo)?> GetWindowsOsInfoAsync(
        ImageConfig imageConfig,
        string baseLayerDigest,
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        CancellationToken cancellationToken = default)
    {
        using IDockerRegistryClient mcrClient =
            await dockerRegistryClientFactory.GetClientAsync(RegistryHelper.McrRegistry);

        foreach (WindowsImageDefinition definition in windowsImageDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await mcrClient.Blobs.ExistsAsync(
                definition.Repo, baseLayerDigest, cancellationToken))
            {
                return (new(definition.Type, imageConfig.OsVersion), definition.Repo);
            }
        }

        return null;
    }

    private static async Task<int> GetLegacyBaseHistoryCountAsync(
        IDockerRegistryClient mcrClient,
        string repo,
        IImageManifest manifest,
        CancellationToken cancellationToken)
    {
        int baseLayerCount = 0;
        foreach (IDescriptor layer in manifest.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(layer.Digest))
            {
                throw new Exception($"No digest information defined for layer index {baseLayerCount} of the Windows image.");
            }

            if (!await mcrClient.Blobs.ExistsAsync(repo, layer.Digest, cancellationToken))
            {
                break;
            }

            baseLayerCount++;
        }

        return baseLayerCount;
    }

    [GeneratedRegex(@"(\./)?(etc|usr/lib)/os-release")]
    private static partial Regex OsReleaseRegex();

    private record WindowsImageDefinition(WindowsType Type, string Repo);
}

internal record WindowsImageInfo(WindowsOsInfo Info, string Repo, int BaseHistoryCount);
