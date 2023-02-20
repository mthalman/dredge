using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using ImageConfig = Valleysoft.DockerRegistryClient.Models.Image;

namespace Valleysoft.Dredge.Core;

public static class LayersComparer
{
    public async static Task<CompareLayersResult> GetCompareLayersResult(
        IDockerRegistryClientFactory dockerRegistryClientFactory, string baseImage, string targetImage, AppSettings appSettings,
        LayerCompareOptions compareOptions)
    {
        IList<LayerInfo> baseLayers = await GetLayersAsync(baseImage, dockerRegistryClientFactory, appSettings, compareOptions);
        IList<LayerInfo> targetLayers = await GetLayersAsync(targetImage, dockerRegistryClientFactory, appSettings, compareOptions);
        List<LayerComparison> layerComparisons = GetLayerComparisons(baseLayers, targetLayers);
        CompareLayersSummary summary = GetSummary(layerComparisons);

        return new CompareLayersResult(
            summary,
            layerComparisons);
    }

    private static async Task<IList<LayerInfo>> GetLayersAsync(
        string image, IDockerRegistryClientFactory dockerRegistryClientFactory, AppSettings appSettings, LayerCompareOptions compareOptions)
    {
        ImageName imageName = ImageName.Parse(image);
        using IDockerRegistryClient client = await dockerRegistryClientFactory.GetClientAsync(imageName.Registry);
        DockerManifestV2 manifest = (await ManifestHelper.GetResolvedManifestAsync(client, imageName, appSettings, compareOptions.PlatformOptions)).Manifest;

        string? digest = manifest.Config?.Digest;
        if (digest is null)
        {
            throw new NotSupportedException($"Could not resolve the image config digest of '{image}'.");
        }

        ImageConfig imageConfig = await client.Blobs.GetImageAsync(imageName.Repo, digest);

        List<LayerInfo> layerInfos = new();
        int layerIndex = 0;
        foreach (LayerHistory history in imageConfig.History)
        {
            if (compareOptions.IncludeHistory || !history.IsEmptyLayer)
            {
                string? layerDigest = !history.IsEmptyLayer ? manifest.Layers[layerIndex].Digest : null;
                long? compressedSize = null;
                if (compareOptions.IncludeCompressedSize)
                {
                    if (history.IsEmptyLayer)
                    {
                        compressedSize = 0;
                    }
                    else
                    {
                        compressedSize = manifest.Layers[layerIndex].Size;
                    }
                }

                layerInfos.Add(
                    new LayerInfo(
                        layerDigest,
                        compareOptions.IncludeHistory ? history.CreatedBy : null,
                        compareOptions.IncludeCompressedSize ? compressedSize : null));

                if (!history.IsEmptyLayer)
                {
                    layerIndex++;
                }
            }
        }

        return layerInfos;
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
}
