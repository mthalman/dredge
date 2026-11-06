using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using ImageConfig = Valleysoft.DockerRegistryClient.Models.Images.Image;

namespace Valleysoft.Dredge.Commands.Image;

public class OsCommand : RegistryCommandBase<OsOptions>
{
    private static readonly WindowsImageDefinition[] windowsImageDefinitions =
    [
        new(WindowsType.NanoServer, "windows/nanoserver"),
        new(WindowsType.ServerCore, "windows/servercore"),
        new(WindowsType.Server, "windows/server"),
        new(WindowsType.Windows, "windows")
    ];

    private readonly IDredgePathProvider paths;

    public OsCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : this(dockerRegistryClientFactory, output, new DredgePathProvider())
    {
    }

    internal OsCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output,
        IDredgePathProvider paths)
        : base("os", "Gets OS info about the container image", dockerRegistryClientFactory, output)
    {
        this.paths = paths;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageSourceReference reference = ImageSourceReference.Parse(Options.Image);
        return ExecuteCommandAsync(reference.Registry, cancellationToken, async ct =>
        {
            await using ResolvedImage source = await OpenImageAsync(Options.Image, Options, ct,
                requirements: ImageSourceRequirements.Metadata);
            source.Report(Error);
            IImageManifest manifest = source.Resolved.Manifest;
            ImageConfig imageConfig = source.Configuration;

            object? osInfo;
            if (imageConfig.Os.Equals("windows", StringComparison.OrdinalIgnoreCase))
            {
                if (source.IsDaemon)
                {
                    await using LayerStore store = LayerStore.Create(paths);
                    await ExplorerContentIdentity.VerifyAsync(
                        source.Content, imageConfig, source.Resolved, store, ct);
                }
                var windowsOsInfo =
                    await GetWindowsOsInfoAsync(imageConfig, manifest, DockerRegistryClientFactory, ct,
                        useDistributionDigest: !source.IsDaemon, selectedPlatform: source.SelectedPlatform);
                osInfo = windowsOsInfo?.Info;
            }
            else
            {
                if (manifest.Layers.Length == 0)
                {
                    throw new InvalidOperationException("The image has no layers from which to derive OS information.");
                }

                await using LayerStore store = LayerStore.Create(paths);
                if (source.IsDaemon)
                {
                    await ExplorerContentIdentity.VerifyAsync(source.Content, imageConfig, source.Resolved,
                        store, ct, indexLayers: true);
                }
                await using ImageFileSystem fileSystem = await ImageFileSystem.CreateAsync(source, ct, store);
                osInfo = await GetLinuxOsInfoAsync(fileSystem, ct);
            }

            if (osInfo is null)
            {
                throw new Exception("Unable to derive OS information from the image.");
            }

            string output = JsonHelper.Serialize(osInfo, JsonHelper.SettingsNoCamelCase);
            Output.WriteLine(output);
        });
    }

    private static async Task<LinuxOsInfo?> GetLinuxOsInfoAsync(
        ImageFileSystem fileSystem,
        CancellationToken cancellationToken)
    {
        foreach (string path in new[] { "etc/os-release", "usr/lib/os-release" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageFileSystemEntry entry;
            try
            {
                entry = fileSystem.ResolveContentEntry(path);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            if (entry.Size == 0)
            {
                continue;
            }
            using MemoryStream memStream = new();
            await fileSystem.CopyFileToAsync(path, memStream, cancellationToken);
            memStream.Position = 0;
            using StreamReader reader = new(memStream);
            string content = await reader.ReadToEndAsync(cancellationToken);
            return LinuxOsInfo.Parse(content);
        }

        return null;
    }

    internal static async Task<WindowsImageInfo?> GetWindowsOsInfoAsync(
        ImageConfig imageConfig,
        IImageManifest manifest,
        IDockerRegistryClientFactory dockerRegistryClientFactory, CancellationToken cancellationToken,
        bool useDistributionDigest = true, ExplorerPlatform? selectedPlatform = null)
    {
        ExplorerPlatform platform = ExplorerPlatform.FromConfig(imageConfig, selectedPlatform);
        string? baseLayerDigest = manifest.Layers.FirstOrDefault()?.Digest;
        if (useDistributionDigest && string.IsNullOrEmpty(baseLayerDigest))
        {
            throw new Exception("No digest was found for the base layer of the Windows image.");
        }

        using IDockerRegistryClient mcrClient =
            await dockerRegistryClientFactory.GetClientAsync(RegistryHelper.McrRegistry, cancellationToken);

        foreach (WindowsImageDefinition definition in windowsImageDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (useDistributionDigest &&
                await mcrClient.Blobs.ExistsAsync(definition.Repo, baseLayerDigest!, cancellationToken))
            {
                int baseHistoryCount = await GetLegacyBaseHistoryCountAsync(
                    mcrClient, definition.Repo, manifest, cancellationToken);
                return new(new(definition.Type, platform.OsVersion), definition.Repo, baseHistoryCount);
            }
        }

        if (string.IsNullOrEmpty(platform.OsVersion) ||
            string.IsNullOrEmpty(platform.Architecture) ||
            imageConfig.RootFilesystem?.DiffIds is not { Length: > 0 } targetDiffIds)
        {
            return null;
        }

        string baseImageTag = $"{platform.OsVersion}-{platform.Architecture}";
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
                new(definition.Type, platform.OsVersion),
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
            await dockerRegistryClientFactory.GetClientAsync(RegistryHelper.McrRegistry, cancellationToken);

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

    private record WindowsImageDefinition(WindowsType Type, string Repo);
}

internal record WindowsImageInfo(WindowsOsInfo Info, string Repo, int BaseHistoryCount);
