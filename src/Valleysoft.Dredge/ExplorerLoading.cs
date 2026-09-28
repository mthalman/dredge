using System.Text.RegularExpressions;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.Dredge.Commands;

namespace Valleysoft.Dredge;

internal sealed record ExplorerPlatform(string Os, string Architecture, string? Variant, string? OsVersion)
{
    public override string ToString()
    {
        string value = $"{Os}/{Architecture}";
        if (!string.IsNullOrEmpty(Variant))
        {
            value += "/" + Variant;
        }
        if (!string.IsNullOrEmpty(OsVersion))
        {
            value += " " + OsVersion;
        }
        return value;
    }
}

internal sealed record ExplorerBaseImage(string Name, int LayerCount);

// What the explorer knows before any layer is downloaded: two small requests
// for the manifest and config, plus base verification.
internal sealed class ExplorerSource
{
    public required ImageName Image { get; init; }
    public required ResolvedManifest Resolved { get; init; }
    public required Image Config { get; init; }
    public required IReadOnlyList<ExplorerPlatform> Platforms { get; init; }
    public ExplorerPlatform? Platform { get; init; }
    public IReadOnlyList<ExplorerBaseImage> BaseImages { get; init; } = [];
    public string? BaseWarning { get; init; }

    public int LayerCount => Resolved.Manifest.Layers.Length;

    public static async Task<ExplorerSource> OpenAsync(
        IDockerRegistryClient client, IDockerRegistryClientFactory factory,
        ImageName image, PlatformOptionsBase options, IReadOnlyList<string>? baseImages,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<ExplorerPlatform>, ExplorerPlatform?>? choosePlatform = null,
        IAppSettingsStore? settingsStore = null, ExplorerPlatform? exactPlatform = null,
        ResolvedManifest? resolvedManifest = null)
    {
        (ResolvedManifest resolved, IReadOnlyList<ExplorerPlatform> platforms, ExplorerPlatform? platform) =
            resolvedManifest is null
                ? await ResolveAsync(client, image, options, exactPlatform, cancellationToken, choosePlatform, settingsStore)
                : (resolvedManifest, [], exactPlatform);
        PlatformOptionsBase resolvedOptions = platform is null ? options : ForPlatform(platform);
        IImageManifest manifest = resolved.Manifest;
        string configDigest = manifest.Config?.Digest ??
            throw new InvalidDataException($"Image '{image}' has no config digest.");
        Image config = await client.Blobs.GetImageAsync(image.Repo, configDigest, cancellationToken);
        if (!string.Equals(config.Os, "linux", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"The image explorer supports Linux images only (found '{config.Os}').");
        }

        (IReadOnlyList<ExplorerBaseImage> verifiedBases, string? warning) = await ExplorerSession.VerifyBasesAsync(
            client, factory, image, resolved, resolvedOptions, baseImages, cancellationToken, platform);
        return new ExplorerSource
        {
            Image = image,
            Resolved = resolved,
            Config = config,
            Platforms = platforms,
            Platform = platform,
            BaseImages = verifiedBases,
            BaseWarning = warning
        };
    }

    // Resolves a tag or digest to one image manifest. An exact platform, which
    // includes the variant that platform options can't express, wins over options.
    internal static async Task<(ResolvedManifest Resolved, IReadOnlyList<ExplorerPlatform> Platforms, ExplorerPlatform? Platform)> ResolveAsync(
        IDockerRegistryClient client, ImageName image, PlatformOptionsBase options,
        ExplorerPlatform? exactPlatform, CancellationToken cancellationToken,
        Func<IReadOnlyList<ExplorerPlatform>, ExplorerPlatform?>? choosePlatform = null,
        IAppSettingsStore? settingsStore = null)
    {
        ManifestInfo info = await client.Manifests.GetAsync(
            image.Repo, (image.Tag ?? image.Digest)!, cancellationToken);
        IReadOnlyList<ExplorerPlatform> platforms = GetPlatforms(info.Manifest);
        ExplorerPlatform? platform = null;
        if (info.Manifest is IManifestList && platforms.Count == 0)
        {
            throw new NotSupportedException($"The image explorer supports Linux images only; '{image}' has no Linux platform.");
        }
        if (info.Manifest is IManifestList exactList && exactPlatform is not null)
        {
            platform = platforms.FirstOrDefault(candidate => candidate == exactPlatform) ??
                throw new InvalidOperationException(
                    $"'{image}' has no {exactPlatform} platform. Available platforms: " +
                    string.Join(", ", platforms.Select(item => item.ToString())));
            info = await GetPlatformManifestAsync(client, image, exactList, platform, cancellationToken);
        }
        else if (info.Manifest is IManifestList list)
        {
            PlatformSettings settings = (settingsStore ?? new AppSettingsStore()).Load().Platform;
            string? os = Coalesce(options.Os, settings.Os);
            string? architecture = Coalesce(options.Architecture, settings.Architecture);
            string? osVersion = Coalesce(options.OsVersion, settings.OsVersion);
            ExplorerPlatform[] matches = platforms.Where(candidate =>
                (os is null || candidate.Os == os) &&
                (architecture is null || candidate.Architecture == architecture) &&
                (osVersion is null || candidate.OsVersion == osVersion)).ToArray();
            if (matches.Length == 1)
            {
                platform = matches[0];
            }
            else if (matches.Length > 1 && choosePlatform is not null)
            {
                platform = choosePlatform(matches) ??
                    throw new OperationCanceledException("No platform was chosen.");
            }
            else
            {
                throw new InvalidOperationException(matches.Length == 0
                    ? $"No platform in '{image}' matches the requested platform. Available platforms: " +
                        string.Join(", ", platforms.Select(item => item.ToString()))
                    : $"'{image}' has {matches.Length} matching platforms. Use " +
                        $"{PlatformOptionsBase.OsOptionName} and {PlatformOptionsBase.ArchOptionName} to choose one: " +
                        string.Join(", ", matches.Select(item => item.ToString())));
            }
            info = await GetPlatformManifestAsync(client, image, list, platform, cancellationToken);
        }
        if (info.Manifest is not IImageManifest manifest)
        {
            throw new NotSupportedException(
                $"The image name '{image}' has a media type of '{info.MediaType}' which is not supported.");
        }
        return (new ResolvedManifest(info, manifest), platforms, platform);
    }

    private static Task<ManifestInfo> GetPlatformManifestAsync(IDockerRegistryClient client, ImageName image,
        IManifestList list, ExplorerPlatform platform, CancellationToken cancellationToken)
    {
        IManifestReference reference = list.Manifests.First(manifest =>
            manifest.Platform is { } value && ToPlatform(value) == platform);
        return client.Manifests.GetAsync(image.Repo,
            reference.Digest ?? throw new InvalidDataException("Manifest digest is missing."),
            cancellationToken);
    }

    public static PlatformOptionsBase ForPlatform(ExplorerPlatform platform) => new()
    {
        Os = platform.Os,
        Architecture = platform.Architecture,
        OsVersion = platform.OsVersion
    };

    internal static IReadOnlyList<ExplorerPlatform> GetPlatforms(IManifest manifest) =>
        manifest is IManifestList list
            ? list.Manifests
                .Where(item => item.Platform is { } platform &&
                    string.Equals(platform.Os, "linux", StringComparison.OrdinalIgnoreCase))
                .Select(item => ToPlatform(item.Platform!))
                .Distinct()
                .ToArray()
            : [];

    private static ExplorerPlatform ToPlatform(ManifestPlatform platform) =>
        new(platform.Os ?? "", platform.Architecture ?? "",
            string.IsNullOrEmpty(platform.Variant) ? null : platform.Variant,
            string.IsNullOrEmpty(platform.OsVersion) ? null : platform.OsVersion);

    private static string? Coalesce(string? option, string? setting) =>
        !string.IsNullOrEmpty(option) ? option : string.IsNullOrEmpty(setting) ? null : setting;
}

internal enum ExplorerLayerState { Waiting, Indexing, Ready, Failed }

// Indexes layers in parallel. Waiting layers are taken in order, except that a
// prioritized layer jumps the queue. Failures stay failed until retried.
internal sealed class ExplorerLayerIndexer
{
    private readonly object sync = new();
    private readonly Func<int, IProgress<long>, CancellationToken, Task<StoredLayerIndex>> indexAsync;
    private readonly int layerCount;
    private readonly int concurrency;
    private readonly List<int> pending;
    private readonly ExplorerLayerState[] states;
    private readonly Dictionary<int, StoredLayerIndex> indexes = [];
    private readonly List<Task> workers = [];
    private readonly CancellationTokenSource stopSource = new();
    private CancellationTokenSource? linkedSource;
    private Task? stoppingTask;
    private bool stopping;
    private int running;
    private CancellationToken cancellationToken;
    private bool started;

    public ExplorerLayerIndexer(
        int layerCount,
        Func<int, IProgress<long>, CancellationToken, Task<StoredLayerIndex>> indexAsync,
        int concurrency = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(layerCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        this.layerCount = layerCount;
        this.indexAsync = indexAsync;
        this.concurrency = concurrency;
        pending = Enumerable.Range(0, layerCount).ToList();
        states = new ExplorerLayerState[layerCount];
    }

    public static ExplorerLayerIndexer Create(
        IDockerRegistryClient client, ExplorerSource source, LayerStore store, int concurrency = 3)
    {
        IImageManifest manifest = source.Resolved.Manifest;
        return new(manifest.Layers.Length, (layer, progress, token) =>
        {
            IDescriptor descriptor = manifest.Layers[layer];
            string digest = descriptor.Digest ??
                throw new InvalidDataException($"Layer {layer} has no digest.");
            return store.GetIndexAsync(client, source.Image, new(layer, digest), descriptor.Size, token, progress);
        }, concurrency);
    }

    public event Action<int, long>? Progress;
    public event Action<int>? Started;
    public event Action<int, StoredLayerIndex>? Indexed;
    public event Action<int, Exception>? Failed;
    public event Action<IReadOnlyDictionary<int, StoredLayerIndex>>? Completed;

    public IReadOnlyDictionary<int, StoredLayerIndex> Snapshot()
    {
        lock (sync)
        {
            return new Dictionary<int, StoredLayerIndex>(indexes);
        }
    }

    public void Start(CancellationToken token)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (started)
            {
                throw new InvalidOperationException("The indexer has already started.");
            }
            started = true;
            linkedSource = CancellationTokenSource.CreateLinkedTokenSource(token, stopSource.Token);
            cancellationToken = linkedSource.Token;
            if (layerCount == 0)
            {
                workers.Add(Task.Run(() =>
                {
                    if (!Volatile.Read(ref stopping) && !cancellationToken.IsCancellationRequested)
                    {
                        Completed?.Invoke(new Dictionary<int, StoredLayerIndex>());
                    }
                }));
                return;
            }
            FillWorkers();
        }
    }

    public void Prioritize(int layer)
    {
        lock (sync)
        {
            if (stopping)
            {
                return;
            }
            if (pending.Remove(layer))
            {
                pending.Insert(0, layer);
            }
        }
    }

    public bool Retry(int layer)
    {
        lock (sync)
        {
            if (stopping || states[layer] != ExplorerLayerState.Failed)
            {
                return false;
            }
            states[layer] = ExplorerLayerState.Waiting;
            pending.Insert(0, layer);
            if (started)
            {
                FillWorkers();
            }
            return true;
        }
    }

    private void FillWorkers()
    {
        while (!stopping && !cancellationToken.IsCancellationRequested && running < concurrency && pending.Count > 0)
        {
            running++;
            workers.Add(Task.Run(WorkAsync, CancellationToken.None));
        }
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            int layer;
            lock (sync)
            {
                if (pending.Count == 0 || cancellationToken.IsCancellationRequested)
                {
                    running--;
                    return;
                }
                layer = pending[0];
                pending.RemoveAt(0);
                states[layer] = ExplorerLayerState.Indexing;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Started?.Invoke(layer);
                StoredLayerIndex index = await indexAsync(layer,
                    new InlineProgress<long>(bytes =>
                    {
                        if (!Volatile.Read(ref stopping) && !cancellationToken.IsCancellationRequested)
                        {
                            Progress?.Invoke(layer, bytes);
                        }
                    }), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyDictionary<int, StoredLayerIndex>? complete = null;
                lock (sync)
                {
                    if (stopping)
                    {
                        states[layer] = ExplorerLayerState.Waiting;
                        running--;
                        return;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    indexes[layer] = index;
                    states[layer] = ExplorerLayerState.Ready;
                    if (indexes.Count == layerCount)
                    {
                        complete = new Dictionary<int, StoredLayerIndex>(indexes);
                    }
                }
                Indexed?.Invoke(layer, index);
                if (complete is not null)
                {
                    Completed?.Invoke(complete);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (sync)
                {
                    states[layer] = ExplorerLayerState.Waiting;
                    running--;
                }
                return;
            }
            catch (Exception exception)
            {
                lock (sync)
                {
                    if (stopping)
                    {
                        states[layer] = ExplorerLayerState.Waiting;
                        running--;
                        return;
                    }
                    states[layer] = ExplorerLayerState.Failed;
                }
                if (!cancellationToken.IsCancellationRequested)
                {
                    Failed?.Invoke(layer, exception);
                }
            }
        }
    }

    public Task StopAsync()
    {
        lock (sync)
        {
            stopping = true;
            pending.Clear();
            return stoppingTask ??= StopCoreAsync(workers.ToArray());
        }
    }

    private async Task StopCoreAsync(Task[] activeWorkers)
    {
        try
        {
            await stopSource.CancelAsync();
        }
        finally
        {
            try
            {
                await Task.WhenAll(activeWorkers);
            }
            finally
            {
                linkedSource?.Dispose();
                stopSource.Dispose();
            }
        }
    }
}

internal static partial class InstructionText
{
    public static string Normalize(string? createdBy)
    {
        if (string.IsNullOrWhiteSpace(createdBy))
        {
            return "(no history)";
        }
        string line = createdBy.Trim();
        if (line.EndsWith("# buildkit", StringComparison.Ordinal))
        {
            line = line[..^"# buildkit".Length].TrimEnd();
        }
        int nop = line.IndexOf("#(nop)", StringComparison.Ordinal);
        if (nop >= 0)
        {
            line = line[(nop + "#(nop)".Length)..].Trim();
            if (line.StartsWith("ADD ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("COPY ", StringComparison.OrdinalIgnoreCase))
            {
                int at = line.IndexOf(" in ", StringComparison.Ordinal);
                if (at >= 0)
                {
                    line = line[..at] + line[(at + 3)..];
                }
            }
        }
        else
        {
            Match shell = ShellPrefix().Match(line);
            if (shell.Success)
            {
                line = "RUN " + line[shell.Length..];
            }
        }
        return Whitespace().Replace(line, " ").Trim();
    }

    [GeneratedRegex(@"^(RUN\s+)?(\|\d+\s.*?\s)?/bin/(ba)?sh -c\s+")]
    private static partial Regex ShellPrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
