using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.Dredge.Commands;
using Image = Valleysoft.DockerRegistryClient.Models.Images.Image;

namespace Valleysoft.Dredge;

internal interface IImageContentSource
{
    Task<BlobDownloadResult> GetRangeAsync(string digest, long start, long? length, CancellationToken cancellationToken);
}

internal sealed class RegistryImageContentSource : IImageContentSource
{
    private readonly IDockerRegistryClient client;
    private readonly ImageName image;

    public RegistryImageContentSource(IDockerRegistryClient client, ImageName image)
    {
        this.client = client;
        this.image = image;
    }

    public Task<BlobDownloadResult> GetRangeAsync(string digest, long start, long? length, CancellationToken cancellationToken) =>
        client.Blobs.GetRangeAsync(image.Repo, digest, start, length, cancellationToken);
}

internal sealed class ResolvedImage : IAsyncDisposable
{
    private readonly IAsyncDisposable? owner;

    internal ResolvedImage(ImageSourceReference reference, ImageName name, ResolvedManifest resolved,
        ManifestInfo? initialManifest, Image configuration, byte[] configurationJson,
        IImageContentSource content, string identity, string platform, IAsyncDisposable? owner = null,
        DockerConnection? connection = null, ExplorerPlatform? selectedPlatform = null)
    {
        Reference = reference;
        Name = name;
        Resolved = resolved;
        InitialManifest = initialManifest;
        Configuration = configuration;
        ConfigurationJson = configurationJson;
        Content = content;
        Identity = identity;
        Platform = platform;
        Connection = connection;
        SelectedPlatform = selectedPlatform;
        this.owner = owner;
    }

    public ImageSourceReference Reference { get; }
    public ImageName Name { get; }
    public ResolvedManifest Resolved { get; }
    public ManifestInfo? InitialManifest { get; }
    public Image Configuration { get; }
    public byte[] ConfigurationJson { get; }
    public IImageContentSource Content { get; }
    public string Identity { get; }
    public string Platform { get; }
    public DockerConnection? Connection { get; }
    public ExplorerPlatform? SelectedPlatform { get; }
    public bool IsDaemon => Reference.IsDaemon;

    public void Report(TextWriter writer)
    {
        if (IsDaemon)
        {
            writer.WriteLine($"Source: {Reference}; platform: {Platform}; Docker image ID: {Identity}");
        }
    }

    public ValueTask DisposeAsync() => owner?.DisposeAsync() ?? ValueTask.CompletedTask;
}

internal enum ImageSourceRequirements
{
    Metadata,
    LayerContent,
    LinuxFileSystem,
    LinuxLayerContent
}

internal interface IImageSourceFactory
{
    Task<ResolvedImage> OpenAsync(string reference, PlatformOptionsBase options, CancellationToken cancellationToken,
        ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent);
}

internal sealed class ImageSourceFactory : IImageSourceFactory
{
    private readonly IDockerRegistryClientFactory registryFactory;
    private readonly IAppSettingsStore settingsStore;
    private readonly Func<DockerConnection, IDockerImageEngine> engineFactory;
    private readonly string? scratchRoot;
    private readonly Func<PlatformOptionsBase, DockerConnection> resolveConnection;
    private readonly Func<LayerStore> indexStoreFactory;
    private readonly object connectionLock = new();
    private readonly object archiveLock = new();
    private readonly Dictionary<ArchiveKey, LiveArchive> archives = [];
    private DockerConnection? connection;
    private string? daemonIdentity;

    public ImageSourceFactory(IDockerRegistryClientFactory registryFactory, IAppSettingsStore? settingsStore = null)
        : this(registryFactory, settingsStore, selected => new DockerImageEngine(selected))
    {
    }

    internal ImageSourceFactory(IDockerRegistryClientFactory registryFactory, IAppSettingsStore? settingsStore,
        Func<DockerConnection, IDockerImageEngine> engineFactory, string? scratchRoot = null,
        Func<PlatformOptionsBase, DockerConnection>? resolveConnection = null,
        Func<LayerStore>? indexStoreFactory = null)
    {
        this.registryFactory = registryFactory;
        this.settingsStore = settingsStore ?? new AppSettingsStore();
        this.engineFactory = engineFactory;
        this.scratchRoot = scratchRoot;
        this.resolveConnection = resolveConnection ??
            (options => DockerConnection.Resolve(options.DockerContext, options.DockerHost));
        this.indexStoreFactory = indexStoreFactory ?? (() => LayerStore.Create());
    }

    public Task<ResolvedImage> OpenAsync(string reference, PlatformOptionsBase options, CancellationToken cancellationToken,
        ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent) =>
        OpenAsync(reference, options, cancellationToken, requirements, null);

    internal ComparisonScope CreateComparisonScope() => new(this);

    private async Task<ResolvedImage> OpenAsync(string reference, PlatformOptionsBase options, CancellationToken cancellationToken,
        ImageSourceRequirements requirements, ComparisonScope? comparison)
    {
        ImageSourceReference source = ImageSourceReference.Parse(reference);
        ImageName name = source.GetImageName();
        if (!source.IsDaemon)
        {
            IDockerRegistryClient client = await registryFactory.GetClientAsync(name.Registry, cancellationToken);
            try
            {
                ManifestInfo initial = await client.Manifests.GetAsync(name.Repo, (name.Tag ?? name.Digest)!, cancellationToken);
                ResolvedManifest resolved = await ManifestHelper.GetResolvedManifestAsync(
                    client, name, options, initial, settingsStore, cancellationToken);
                string digest = resolved.Manifest.Config?.Digest ??
                    throw new InvalidDataException($"Image '{reference}' has no configuration digest.");
                await using Stream config = await client.Blobs.GetAsync(name.Repo, digest, cancellationToken);
                using MemoryStream buffer = new();
                await config.CopyToAsync(buffer, cancellationToken);
                byte[] json = buffer.ToArray();
                Image image = ParseConfiguration(json);
                ValidateCapabilities(image.Os, requirements);
                return new ResolvedImage(source, name, resolved, initial, image, json,
                    new RegistryImageContentSource(client, name), resolved.ManifestInfo.DockerContentDigest,
                    GetPlatform(image), new RegistryOwner(client));
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        DockerConnection selected;
        lock (connectionLock)
        {
            connection ??= resolveConnection(options);
            selected = connection;
        }
        using IDockerImageEngine engine = engineFactory(selected);
        string identity = await engine.GetDaemonIdentityAsync(cancellationToken);
        lock (connectionLock)
        {
            daemonIdentity ??= identity;
            if (identity != daemonIdentity)
            {
                throw new InvalidOperationException("The selected Docker daemon changed during this operation. Retry with a stable --docker-host endpoint.");
            }
        }
        ImageInspectResponse inspect = await engine.InspectAsync(source.Value, cancellationToken);
        string id = inspect.ID;
        if (!DockerImageArchive.IsSha256(id))
        {
            throw new NotSupportedException("Docker did not resolve this reference to a full immutable image ID. Select a concrete local image ID.");
        }
        if (source.Value != id)
        {
            inspect = await engine.InspectAsync(id, cancellationToken);
        }
        if (inspect.ID != id)
        {
            throw new InvalidDataException("Docker immutable image inspection returned a different image ID.");
        }
        ValidatePlatform(inspect.Os, inspect.Architecture, inspect.OsVersion, options);
        ValidateCapabilities(inspect.Os, requirements);
        ArchiveKey key = new(identity, id, inspect.Os, inspect.Architecture, inspect.OsVersion, inspect.Variant,
            requirements != ImageSourceRequirements.Metadata);
        LiveArchiveLease owner = comparison is not null
            ? await comparison.GetArchiveAsync(key, ExportAsync, cancellationToken)
            : await GetArchiveAsync(key, ExportAsync, cancellationToken);
        DockerImageArchive acquired = owner.Archive;
        return new ResolvedImage(source, name, acquired.Resolved, null, acquired.Configuration, acquired.ConfigurationJson,
            acquired, id, GetPlatform(acquired.Platform), owner, selected, acquired.Platform);

        async Task<DockerImageArchive> ExportAsync(CancellationToken exportCancellationToken)
        {
            await using LayerStore? indexes = requirements == ImageSourceRequirements.LinuxFileSystem
                ? indexStoreFactory() : null;
            await using Stream export = await engine.SaveAsync(id, exportCancellationToken);
            DockerImageArchive archive = await DockerImageArchive.OpenAsync(export, id, exportCancellationToken, scratchRoot,
                inspect.Os, inspect.Architecture, inspect.OsVersion, inspect.Variant,
                verifyLayerContent: requirements != ImageSourceRequirements.Metadata,
                prepareFileSystem: requirements == ImageSourceRequirements.LinuxFileSystem,
                hasLayerIndex: indexes is null ? null : (digest, size, token) =>
                    indexes.GetValidIndexAsync(digest, size, token, ImageFileSystemSemantics.ForOs(inspect.Os)),
                maxLayerIndex: options switch
                {
                    Commands.Image.CatOptions cat => cat.Layer,
                    Commands.Image.LsOptions ls => ls.Layer,
                    _ => null
                },
                getFileSystemView: indexes is not null && (options is Commands.Image.LsOptions { Layer: null } ||
                    options is Commands.Image.ExtractOptions extract && ImagePath.NormalizeRequested(extract.Path).Length == 0)
                    ? (digest, count, token) => ImageFileSystem.ReadCachedViewAsync(
                        indexes, digest, count, token, ImageFileSystemSemantics.ForOs(inspect.Os))
                    : null,
                prepareCachedContent: options is Commands.Image.ExtractOptions);
            try
            {
                ExplorerPlatform platform = archive.Platform;
                if (platform.Os != inspect.Os || platform.Architecture != inspect.Architecture ||
                    (!string.IsNullOrEmpty(inspect.OsVersion) && platform.OsVersion != inspect.OsVersion))
                {
                    throw new InvalidDataException("Docker exported configuration platform does not match the pinned image inspection.");
                }
                return archive;
            }
            catch
            {
                await archive.DisposeAsync();
                throw;
            }
        }
    }

    internal readonly record struct ArchiveKey(string Daemon, string Id, string Os, string Architecture,
        string? OsVersion, string? Variant, bool VerifyLayerContent);

    private async Task<LiveArchiveLease> GetArchiveAsync(
        ArchiveKey key, Func<CancellationToken, Task<DockerImageArchive>> export, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LiveArchive entry;
        LiveArchiveLease lease;
        bool create;
        lock (archiveLock)
        {
            create = !archives.TryGetValue(key, out entry!);
            if (create)
            {
                entry = new(key);
                archives.Add(key, entry);
            }
            lease = Acquire(entry);
        }
        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((LiveArchiveLease)state!).Cancel(), lease);
        try
        {
            if (create)
            {
                try
                {
                    entry.Completion.SetResult(await export(entry.Cancellation.Token));
                }
                catch (Exception exception)
                {
                    lock (archiveLock)
                    {
                        Evict(entry);
                    }
                    entry.Completion.SetException(exception);
                }
            }
            if (create)
            {
                await entry.Completion.Task;
            }
            else
            {
                await entry.Completion.Task.WaitAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return lease;
        }
        catch (Exception exception)
        {
            registration.Dispose();
            try
            {
                await lease.DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                exception.Data["DaemonArchiveCleanupFailure"] = cleanupException;
            }
            throw;
        }
    }

    private LiveArchiveLease Acquire(LiveArchive entry)
    {
        entry.References++;
        entry.Active++;
        return new(this, entry);
    }

    private void Evict(LiveArchive entry)
    {
        if (archives.TryGetValue(entry.Key, out LiveArchive? current) && ReferenceEquals(current, entry))
        {
            archives.Remove(entry.Key);
        }
    }

    internal sealed class LiveArchive(ArchiveKey key)
    {
        internal ArchiveKey Key { get; } = key;
        internal TaskCompletionSource<DockerImageArchive> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenSource Cancellation { get; } = new();
        internal int References { get; set; }
        internal int Active { get; set; }
    }

    internal sealed class LiveArchiveLease(ImageSourceFactory factory, LiveArchive entry) : IAsyncDisposable
    {
        private bool disposed;
        private bool active = true;
        internal DockerImageArchive Archive => entry.Completion.Task.Result;

        internal LiveArchiveLease Acquire()
        {
            lock (factory.archiveLock)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return factory.Acquire(entry);
            }
        }

        internal void Cancel()
        {
            bool cancel;
            lock (factory.archiveLock)
            {
                if (disposed || !active)
                {
                    return;
                }
                active = false;
                cancel = --entry.Active == 0 && !entry.Completion.Task.IsCompleted;
                if (cancel)
                {
                    factory.Evict(entry);
                }
            }
            if (cancel)
            {
                entry.Cancellation.Cancel();
            }
        }

        public async ValueTask DisposeAsync()
        {
            bool release;
            lock (factory.archiveLock)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                if (active)
                {
                    entry.Active--;
                }
                release = --entry.References == 0;
                if (release)
                {
                    factory.Evict(entry);
                }
            }
            if (release)
            {
                try
                {
                    if (entry.Completion.Task.IsCompletedSuccessfully)
                    {
                        await Archive.DisposeAsync();
                    }
                }
                finally
                {
                    entry.Cancellation.Dispose();
                }
            }
        }
    }

    internal sealed class ComparisonScope : IImageSourceFactory, IAsyncDisposable
    {
        private readonly ImageSourceFactory factory;
        private readonly object gate = new();
        private readonly Dictionary<ArchiveKey, LiveArchiveLease> archives = [];
        private readonly TaskCompletionSource acquisitionsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Exception> cleanupFailures = [];
        private int pending;
        private bool disposed;

        internal ComparisonScope(ImageSourceFactory factory)
        {
            this.factory = factory;
        }

        public Task<ResolvedImage> OpenAsync(string reference, PlatformOptionsBase options, CancellationToken cancellationToken,
            ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }
            return factory.OpenAsync(reference, options, cancellationToken, requirements, this);
        }

        internal async Task<LiveArchiveLease> GetArchiveAsync(
            ArchiveKey key, Func<CancellationToken, Task<DockerImageArchive>> export, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                pending++;
            }
            LiveArchiveLease? lease = null;
            try
            {
                lease = await factory.GetArchiveAsync(key, export, cancellationToken);
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (!archives.ContainsKey(key))
                    {
                        archives.Add(key, lease.Acquire());
                    }
                    return lease;
                }
            }
            catch (Exception acquisitionException)
            {
                if (lease is not null)
                {
                    try
                    {
                        await lease.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        lock (gate)
                        {
                            cleanupFailures.Add(exception);
                        }
                        acquisitionException.Data["DaemonArchiveCleanupFailure"] = exception;
                    }
                }
                throw;
            }
            finally
            {
                lock (gate)
                {
                    if (--pending == 0 && disposed)
                    {
                        acquisitionsDrained.TrySetResult();
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            LiveArchiveLease[] owners;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                if (pending == 0)
                {
                    acquisitionsDrained.TrySetResult();
                }
                owners = archives.Values.ToArray();
                archives.Clear();
            }
            await acquisitionsDrained.Task;
            List<Exception> failures;
            lock (gate)
            {
                failures = [.. cleanupFailures];
            }
            foreach (LiveArchiveLease owner in owners)
            {
                try
                {
                    await owner.DisposeAsync();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            if (failures.Count != 0)
            {
                throw new AggregateException("Failed to dispose comparison image archives.", failures);
            }
        }
    }

    private void ValidatePlatform(string os, string architecture, string? osVersion, PlatformOptionsBase options)
    {
        (string? selectedOs, string? selectedArch, string? selectedVersion) = options.ResolvePlatform(settingsStore);
        if ((selectedOs is not null && selectedOs != os) ||
            (selectedArch is not null && selectedArch != architecture) ||
            (selectedVersion is not null && selectedVersion != osVersion))
        {
            throw new InvalidOperationException(
                $"Docker image platform '{os}/{architecture}{(string.IsNullOrEmpty(osVersion) ? "" : "/" + osVersion)}' does not match the requested platform. The Engine client cannot select another platform from an image index; use a concrete matching local image ID.");
        }
        if (string.IsNullOrEmpty(os) || string.IsNullOrEmpty(architecture))
        {
            throw new InvalidDataException("Docker image inspection did not identify an OS and architecture.");
        }
    }

    private static void ValidateCapabilities(string os, ImageSourceRequirements requirements)
    {
        if ((requirements is ImageSourceRequirements.LinuxFileSystem or ImageSourceRequirements.LinuxLayerContent) &&
            !string.Equals(os, "linux", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Filesystem operations support Linux images only (found '{os}').");
        }
    }

    internal static string DecodeConfiguration(ReadOnlySpan<byte> json) =>
        Encoding.UTF8.GetString(json.StartsWith(Encoding.UTF8.Preamble) ? json[Encoding.UTF8.Preamble.Length..] : json);

    private static Image ParseConfiguration(byte[] json) =>
        JsonHelper.Deserialize<Image>(DecodeConfiguration(json)) ??
            throw new InvalidDataException("Image configuration JSON is invalid.");

    private static string GetPlatform(Image image) => GetPlatform(ExplorerPlatform.FromConfig(image));

    private static string GetPlatform(ExplorerPlatform image) =>
        $"{image.Os}/{image.Architecture}" +
        (string.IsNullOrEmpty(image.Variant) ? "" : "/" + image.Variant) +
        (string.IsNullOrEmpty(image.OsVersion) ? "" : $" ({image.OsVersion})");

    private sealed class RegistryOwner : IAsyncDisposable
    {
        private readonly IDockerRegistryClient client;
        private bool disposed;

        public RegistryOwner(IDockerRegistryClient client)
        {
            this.client = client;
        }

        public ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                disposed = true;
                client.Dispose();
            }
            return ValueTask.CompletedTask;
        }
    }
}

internal interface IDockerImageEngine : IDisposable
{
    Task<string> GetDaemonIdentityAsync(CancellationToken cancellationToken);
    Task<ImageInspectResponse> InspectAsync(string reference, CancellationToken cancellationToken);
    Task<Stream> SaveAsync(string imageId, CancellationToken cancellationToken);
}

internal sealed class DockerImageEngine : IDockerImageEngine
{
    private readonly DockerClient client;

    public DockerImageEngine(DockerConnection connection)
    {
        client = connection.CreateClient();
    }

    public async Task<string> GetDaemonIdentityAsync(CancellationToken cancellationToken)
    {
        string? identity = (await client.System.GetSystemInfoAsync(cancellationToken)).ID;
        return !string.IsNullOrEmpty(identity) ? identity :
            throw new InvalidDataException("Docker daemon did not return an immutable daemon identity.");
    }

    public Task<ImageInspectResponse> InspectAsync(string reference, CancellationToken cancellationToken) =>
        client.Images.InspectImageAsync(reference, cancellationToken);

    public Task<Stream> SaveAsync(string imageId, CancellationToken cancellationToken) =>
        client.Images.SaveImageAsync(imageId, cancellationToken);

    public void Dispose() => client.Dispose();
}
