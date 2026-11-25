using static Valleysoft.Dredge.Tests.SemanticCompareFixture;

namespace Valleysoft.Dredge.Tests;

public sealed class CompareFixtureTests
{
    public static bool SmokeEnabled => Environment.GetEnvironmentVariable("DREDGE_COMPARE_SMOKE") == "1";

    [Fact(Skip = "Set DREDGE_COMPARE_SMOKE=1 to write fixed archive smoke-test inputs.", SkipUnless = nameof(SmokeEnabled))]
    public async Task WriteSmokeInputs()
    {
        var (baseline, target) = SmallFixture();
        string root = Path.Combine(RepositoryRoot(), ".compare-smoke");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "base.tar"), Export(baseline, "dredge-406-base:fixture"), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(root, "target.tar"), Export(target, "dredge-406-target:fixture"), TestContext.Current.CancellationToken);
        using var fixture = new SemanticCompareFixture { UseDaemonArchive = true };
        fixture.Images["base"] = baseline;
        fixture.Images["target"] = target;
        fixture.Images["docker://target"] = target;
        var complete = await fixture.CompareAsync("base", "target");
        var partial = await fixture.CompareAsync("base", "docker://target");
        await File.WriteAllTextAsync(Path.Combine(root, "full-report.json"), JsonHelper.Serialize(complete), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "partial-report.json"), JsonHelper.Serialize(partial), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FixedFixtureReportsExpectedDifferencesAndSizes()
    {
        using var fixture = new SemanticCompareFixture();
        var (baseline, target) = SmallFixture();
        fixture.Images["base"] = baseline;
        fixture.Images["target"] = target;
        var report = await fixture.CompareAsync("base", "target");
        Assert.True(report.Complete);
        Assert.Equal(16, report.Files.Count);
        Assert.Equal(128 * 256, report.Sizes.Single(static s => s.Name == "effectiveLiveContentBytes").BaseBytes);
        Assert.Equal(0, report.Sizes.Single(static s => s.Name == "hiddenContentBytes").BaseBytes);
        Assert.Equal(16 * 256, report.Sizes.Single(static s => s.Name == "hiddenContentBytes").TargetBytes);
        Assert.Equal("sha256:5c06921bcbcd87572a6b4223381a3a9acb3938cf526f3e55d84b2b1a8058ddc2", Digest(baseline.Layers[0]));
    }

    private static (Snapshot Base, Snapshot Target) SmallFixture()
    {
        const int count = 128;
        const int width = 256;
        List<TarItem> entries = [];
        for (int i = 0; i < count; i++)
        {
            entries.Add(new($"data/file{i:D5}", new string((char)('a' + i % 26), width)));
        }
        byte[] baselineLayer = Layer(entries.ToArray());
        var changes = entries.Take(count / 8).Select(e => e with { Content = new string('z', width) }).ToArray();
        return (Image(baselineLayer), Image(baselineLayer, Layer(changes)));
    }

    internal static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("The smoke fixture must run from a Dredge checkout.");
    }
}
