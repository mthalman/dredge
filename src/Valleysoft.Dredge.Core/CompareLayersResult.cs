using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Runtime.Serialization;

namespace Valleysoft.Dredge.Core;

public class CompareLayersResult
{
    public CompareLayersResult(CompareLayersSummary summary, IEnumerable<LayerComparison> layerComparisons)
    {
        Summary = summary;
        LayerComparisons = layerComparisons;
    }

    public CompareLayersSummary Summary { get; }
    public IEnumerable<LayerComparison> LayerComparisons { get; }
}

public class CompareLayersSummary
{
    public CompareLayersSummary(bool areEqual, bool targetIncludesAllBaseLayers, int lastCommonLayerIndex)
    {
        AreEqual = areEqual;
        TargetIncludesAllBaseLayers = targetIncludesAllBaseLayers;
        LastCommonLayerIndex = lastCommonLayerIndex;
    }

    /// <summary>
    /// Indicates whether the two images have the same set of layers.
    /// </summary>
    public bool AreEqual { get; }

    /// <summary>
    /// Indicates whether the target image includes all the layers that the base image has.
    /// </summary>
    public bool TargetIncludesAllBaseLayers { get; }

    /// <summary>
    /// Indicates the index of the last layer shared by the two images. Any layers after this index indicates divergence between
    /// the two images.
    /// </summary>
    public int LastCommonLayerIndex { get; }
}

public class LayerComparison
{
    public LayerComparison(LayerInfo? @base, LayerInfo? target, LayerDiff layerDiff)
    {
        Base = @base;
        Target = target;
        LayerDiff = layerDiff;
    }

    public LayerInfo? Base { get; }
    public LayerInfo? Target { get; }
    public LayerDiff LayerDiff { get; }
}

[JsonConverter(typeof(StringEnumConverter))]
public enum LayerDiff
{
    [EnumMember(Value = "Equal")]
    Equal,
    [EnumMember(Value = "Not Equal")]
    NotEqual,
    [EnumMember(Value = "Added")]
    Added,
    [EnumMember(Value = "Removed")]
    Removed
}

public class LayerInfo
{
    public LayerInfo(string? digest, string? history, long? compressedSize)
    {
        Digest = digest;
        History = history;
        CompressedSize = compressedSize;
    }

    public string? Digest { get; }
    public string? History { get; }
    public long? CompressedSize { get; }
}
