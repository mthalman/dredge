using System.Runtime.ExceptionServices;
using Valleysoft.DockerRegistryClient.Models.Images;

namespace Valleysoft.Dredge;

internal static class ExplorerContentIdentity
{
    internal const int VerificationConcurrency = 3;

    internal static Task<string[]> GetVerifiedDigestsAsync(
        ResolvedImage source, LayerStore store, CancellationToken cancellationToken)
    {
        string[] ids = ExplorerSession.ContentDigests(source.Configuration, source.Resolved.Manifest.Layers.Length);
        return source.IsDaemon && ids.All(DockerImageArchive.IsSha256)
            ? Task.FromResult(ids)
            : VerifyAsync(source.Content, source.Configuration, source.Resolved, store, cancellationToken);
    }

    public static async Task<string[]> VerifyAsync(
        IImageContentSource source, Image config, ResolvedManifest manifest, LayerStore store,
        CancellationToken cancellationToken, int? layerCount = null,
        string[]? expectedDiffIds = null, bool indexLayers = false)
    {
        IReadOnlyList<string> ids = expectedDiffIds?.ToArray() ??
            ExplorerSession.ContentDigests(config, manifest.Manifest.Layers.Length);
        int count = layerCount ?? ids.Count;
        if (ids.Count != manifest.Manifest.Layers.Length || count < 0 || count > ids.Count)
        {
            throw new InvalidDataException("The configuration rootfs diff IDs do not match the selected layers.");
        }
        string[] identities = new string[count];
        var layers = manifest.Manifest.Layers.Take(count).Select((descriptor, layer) => (
            Digest: descriptor.Digest ?? throw new InvalidDataException("Layer digest is missing."),
            descriptor.Size, DiffId: ids[layer], Index: layer))
            .GroupBy(static descriptor => (descriptor.Digest, descriptor.DiffId))
            .Select(static group => group.ToArray()).ToArray();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ExceptionDispatchInfo? failure = null;
        int next = -1;
        Task[] workers = Enumerable.Range(0, Math.Min(layers.Length, VerificationConcurrency))
            .Select(_ => VerifyLayersAsync()).ToArray();
        await Task.WhenAll(workers);
        failure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
        return identities;

        async Task VerifyLayersAsync()
        {
            try
            {
                while (true)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    int layer = Interlocked.Increment(ref next);
                    if (layer >= layers.Length)
                    {
                        return;
                    }
                    var first = layers[layer][0];
                    if (source is DockerImageArchive archive &&
                        archive.TryGetVerifiedContentDigest(first.Digest, first.Size, first.DiffId, out string canonical))
                    {
                        foreach (var descriptor in layers[layer])
                        {
                            stop.Token.ThrowIfCancellationRequested();
                            if (!archive.TryGetVerifiedContentDigest(
                                descriptor.Digest, descriptor.Size, descriptor.DiffId, out _))
                            {
                                throw new InvalidDataException("The daemon archive has no verified content identity for the selected layer.");
                            }
                            identities[descriptor.Index] = canonical;
                        }
                        continue;
                    }
                    string identity = first.DiffId;
                    foreach (var descriptor in layers[layer].DistinctBy(static descriptor => descriptor.Size))
                    {
                        if (indexLayers)
                        {
                            await store.GetIndexAsync(source, new(descriptor.Index, descriptor.Digest),
                                descriptor.Size, stop.Token, expectedDiffId: descriptor.DiffId);
                        }
                        identity = source is DockerImageArchive daemonArchive
                            ? await daemonArchive.VerifyContentAsync(
                                descriptor.Digest, descriptor.Size, descriptor.DiffId, stop.Token)
                            : await store.VerifyContentAsync(
                                source, descriptor.Digest, descriptor.Size, descriptor.DiffId, stop.Token);
                    }
                    foreach (var descriptor in layers[layer])
                    {
                        identities[descriptor.Index] = identity;
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(exception), null);
                try
                {
                    await stop.CancelAsync();
                }
                catch (Exception cancellationFailure)
                {
                    Interlocked.CompareExchange(
                        ref failure, ExceptionDispatchInfo.Capture(cancellationFailure), null);
                }
            }
        }
    }
}
