namespace Valleysoft.Dredge.Tests;

public class ImageAnalysisTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HardLinksCompareCapturedSymbolicTargets(bool chain, bool changed)
    {
        ScannedEntry original = File("original", 0, "") with
            { Type = ImageFileType.SymbolicLink, LinkTarget = "before" };
        ScannedEntry saved = File("saved", 0, "") with
            { Type = ImageFileType.HardLink, LinkTarget = "original" };
        ScannedEntry last = saved with { Path = "chain", LinkTarget = "saved" };
        ScannedEntry[] Entries(ScannedEntry target) => chain ? [target, saved, last] : [target, saved];
        ImageAnalysisResult result = ImageAnalysis.Analyze(
        [
            Layer(Entries(original)),
            Layer(Entries(original with { LinkTarget = changed ? "after" : "before" }))
        ]);

        foreach (string path in chain ? new[] { "saved", "chain" } : ["saved"])
        {
            Assert.Equal(changed ? LayerChangeKind.Modified : LayerChangeKind.Identical,
                Assert.Single(result.Layers[1].Changes, change => change.Path == path).Kind);
        }
        Assert.Equal(0, result.FileBytes);
        Assert.Equal(0, result.HiddenBytes);
        Assert.Empty(result.HiddenFiles);
    }

    [Theory]
    [Trait("Upstream", "wagoodman/dive#684")]
    [Trait("Upstream", "wagoodman/dive#696")]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(10, false)]
    [InlineData(100, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(10, true)]
    [InlineData(100, true)]
    public void RepeatedCopiesCountOnlyHiddenPayload(int copies, bool deleted)
    {
        const long size = 4_000_000;
        List<LayerChanges> layers = Enumerable.Range(0, copies)
            .Select(_ => Layer([File("app/data", size, "same")])).ToList();
        if (deleted)
        {
            layers.Add(Layer([], whiteouts: ["app/data"]));
        }

        ImageAnalysisResult result = ImageAnalysis.Analyze(layers);
        long hidden = (deleted ? copies : copies - 1) * size;
        Assert.Equal(copies * size, result.FileBytes);
        Assert.Equal(hidden, result.HiddenBytes);
        Assert.Equal(hidden, result.HiddenFiles.Sum(file => file.Size));
        Assert.Equal(hidden, result.Layers.Sum(layer => layer.HiddenBytes));
        Assert.Equal(deleted ? 0 : 1d / copies, result.Efficiency, 10);
        Assert.Equal(!deleted, result.LiveEntries.ContainsKey("app/data"));
    }

    [Theory]
    [Trait("Upstream", "wagoodman/dive#685")]
    [Trait("Upstream", "wagoodman/dive#604")]
    [InlineData(0L, 0L)]
    [InlineData(0L, 5L)]
    [InlineData(5L, 0L)]
    [InlineData(9L, 3L)]
    [InlineData(3L, 9L)]
    [InlineData(3_000_000_000L, 4_000_000_000L)]
    public void WasteRetainsFinalVersionRatherThanSmallestCopy(long before, long after)
    {
        ImageAnalysisResult result = ImageAnalysis.Analyze(
        [
            Layer([File("app/data", before, "before")]),
            Layer([File("app/data", after, "after")])
        ]);

        Assert.Equal(before + after, result.FileBytes);
        Assert.Equal(before, result.HiddenBytes);
        Assert.Equal(after, result.LiveContents["app/data"].Size);
        Assert.Equal(before + after == 0 ? 1 : (double)after / (before + after), result.Efficiency, 10);
        Assert.InRange(result.Efficiency, 0, 1);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurvivingHardLinksKeepOriginalContentLive(bool replace)
    {
        ScannedEntry link = File("survivor", 0, "") with
        {
            Type = ImageFileType.HardLink, LinkTarget = "original/file"
        };
        ImageAnalysisResult result = ImageAnalysis.Analyze(
        [
            Layer([File("original/file", 7, "old"), link,
                link with { Path = "chain", LinkTarget = "survivor" }]),
            replace ? Layer([File("original/file", 3, "new")]) : Layer([], whiteouts: ["original"]),
            Layer([], whiteouts: ["survivor"])
        ]);
        Assert.Equal(0, result.HiddenBytes);
        Assert.Empty(result.HiddenFiles);
        Assert.Equal(1, result.Efficiency);
    }

    [Fact]
    public void LastHardLinkRemovalChargesOriginalContentOnce()
    {
        ScannedEntry link = File("survivor", 0, "") with
        {
            Type = ImageFileType.HardLink, LinkTarget = "original/file"
        };
        ImageAnalysisResult result = ImageAnalysis.Analyze(
        [
            Layer([File("original/file", 7, "old")]),
            Layer([link], whiteouts: []),
            Layer([], whiteouts: ["original"]),
            Layer([], whiteouts: ["survivor"])
        ]);
        Assert.Equal(7, result.HiddenBytes);
        Assert.Equal(new HiddenFile("original/file", 0, 3, LayerChangeKind.Deleted, 7),
            Assert.Single(result.HiddenFiles));
    }

    [Fact]
    public void HardLinkThroughSymbolicParentKeepsContentLive()
    {
        ImageAnalysisResult result = ImageAnalysis.Analyze(
        [
            Layer([File("original/file", 7, "old"),
                File("alias", 0, "") with { Type = ImageFileType.SymbolicLink, LinkTarget = "original" },
                File("survivor", 0, "") with { Type = ImageFileType.HardLink, LinkTarget = "alias/file" }]),
            Layer([], whiteouts: ["original"])
        ]);
        Assert.Equal(0, result.HiddenBytes);
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
