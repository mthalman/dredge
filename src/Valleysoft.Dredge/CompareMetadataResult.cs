using Newtonsoft.Json.Linq;

namespace Valleysoft.Dredge;

public class CompareMetadataResult
{
    public CompareMetadataResult(CompareMetadataSummary summary, IEnumerable<MetadataComparison> comparisons)
    {
        Summary = summary;
        Comparisons = comparisons;
    }

    public CompareMetadataSummary Summary { get; }
    public IEnumerable<MetadataComparison> Comparisons { get; }
}

public class CompareMetadataSummary
{
    public CompareMetadataSummary(bool areEqual, int equal, int changed, int added, int removed)
    {
        AreEqual = areEqual;
        Equal = equal;
        Changed = changed;
        Added = added;
        Removed = removed;
    }

    public bool AreEqual { get; }
    public int Equal { get; }
    public int Changed { get; }
    public int Added { get; }
    public int Removed { get; }
}

public class MetadataComparison
{
    public MetadataComparison(
        string category,
        string path,
        JToken? baseValue,
        JToken? targetValue,
        CompareDiff diff)
    {
        Category = category;
        Path = path;
        BaseValue = baseValue;
        TargetValue = targetValue;
        Diff = diff;
    }

    public string Category { get; }
    public string Path { get; }
    public JToken? BaseValue { get; }
    public JToken? TargetValue { get; }
    public CompareDiff Diff { get; }
}
