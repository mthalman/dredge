namespace Valleysoft.Dredge.Tests;

public class ImageAnalysisTests
{
    [Fact]
    public void CountsHiddenFilesOnceAndAttributesThemToTheShippingLayer()
    {
        ImageAnalysisResult result = ImageAnalysis.Analyze(
        [
            Layer([File("app/old", 5, "a"), File("app/shared", 7, "b")]),
            Layer([File("app/shared", 9, "c")], whiteouts: ["app/old"]),
            Layer([], opaque: ["app"])
        ]);

        Assert.Equal(21, result.FileBytes);
        Assert.Equal(21, result.HiddenBytes);
        Assert.Equal([12L, 9L, 0L], result.Layers.Select(layer => layer.HiddenBytes));
        Assert.Equal(0, result.Efficiency);
        Assert.Contains(result.Layers[1].Changes,
            change => change.Path == "app/old" && change.Kind == LayerChangeKind.Deleted);
        Assert.Contains(result.Layers[2].Changes,
            change => change.Path == "app/shared" && change.Kind == LayerChangeKind.Deleted);
    }

    [Fact]
    public void IdenticalRewriteStillShipsHiddenBytes()
    {
        ImageAnalysisResult result = ImageAnalysis.Analyze(
        [
            Layer([File("same", 4, "hash")]),
            Layer([File("same", 4, "hash")])
        ]);

        Assert.Equal(8, result.FileBytes);
        Assert.Equal(4, result.HiddenBytes);
        Assert.Equal(0.5, result.Efficiency);
        Assert.Contains(result.Layers[1].Changes,
            change => change.Path == "same" && change.Kind == LayerChangeKind.Identical);
    }

    [Fact]
    public void ReplacingDirectoryWithFileHidesDescendants()
    {
        ImageAnalysisResult result = ImageAnalysis.Analyze(
        [
            Layer([File("app/cache/item", 11, "old")]),
            Layer([File("app/cache", 3, "new")])
        ]);

        Assert.Equal(11, result.HiddenBytes);
        Assert.Equal(11, result.Layers[0].HiddenBytes);
        Assert.Contains(result.Layers[1].Changes,
            change => change.Path == "app/cache/item" && change.Kind == LayerChangeKind.Deleted);
    }

    [Fact]
    public void EmptyImageIsFullyEfficient()
    {
        ImageAnalysisResult result = ImageAnalysis.Analyze([]);
        Assert.Equal(1, result.Efficiency);
        Assert.Empty(result.Layers);
    }

    [Fact]
    public void PotentialSavingsCountOnlyLiveFilesAndStayOutsideEfficiency()
    {
        ImageAnalysisResult result = ImageAnalysis.Analyze(
        [
            Layer([File("root/.npm/_cacache/old", 10, "old"),
                File("root/.npm/_cacache/live", 20, "live"),
                File("app/.git/objects/one", 4, "git")]),
            Layer([], whiteouts: ["root/.npm/_cacache/old"])
        ]);

        Assert.Equal(10, result.HiddenBytes);
        Assert.Equal(10d / 34d, 1 - result.Efficiency, 6);
        Assert.Equal(24, result.FindPotentialSavings().Sum(finding => finding.Bytes));
        Assert.DoesNotContain(result.FindPotentialSavings()
            .SelectMany(finding => finding.Paths), path => path.EndsWith("/old", StringComparison.Ordinal));
    }

    private static LayerChanges Layer(
        ScannedEntry[] entries, string[]? whiteouts = null, string[]? opaque = null) =>
        new(entries, whiteouts ?? [], opaque ?? []);

    private static ScannedEntry File(string path, long size, string hash) =>
        new(path, ImageFileType.File, 0x1A4, 0, 0, size,
            DateTime.UnixEpoch, null, 0, 0, 0, hash);
}
