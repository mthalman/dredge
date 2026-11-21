using System.Formats.Tar;
using static Valleysoft.Dredge.Tests.SemanticCompareFixture;

namespace Valleysoft.Dredge.Tests;

public sealed class ImageComparisonTests
{
    [Fact]
    public async Task EffectiveEqualityIgnoresLayerBoundariesOrderingAndTimestamps()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new("a", "same"), new("b", "same")));
        fixture.Images["target"] = Image(Layer(new TarItem("b", "same", Time: Timestamp.AddDays(1))), Layer(new TarItem("a", "same", Time: Timestamp.AddDays(1))));
        var result = await fixture.CompareAsync("base", "target", "--scope", "files");
        Assert.Equal(0, result.ExitCode);
        result = await fixture.CompareAsync("base", "target", "--scope", "files", "--include-timestamps");
        Assert.Equal(2, result.ExitCode);
        Assert.All(result.Files, static d => Assert.Equal(["modifiedTime"], d.Reasons));
    }

    [Theory]
    [InlineData("value", "other", 420, 420, 0, 0, 0, 0, "content")]
    [InlineData("value", "value", 420, 493, 0, 0, 0, 0, "mode")]
    [InlineData("value", "value", 420, 420, 0, 1, 0, 0, "owner")]
    [InlineData("value", "value", 420, 420, 0, 0, 0, 1, "group")]
    public async Task FileReasonsAreExplicit(string old, string current, int oldMode, int newMode, int oldOwner, int newOwner, int oldGroup, int newGroup, string reason)
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new TarItem("file", old, Mode: oldMode, Owner: oldOwner, Group: oldGroup)));
        fixture.Images["target"] = Image(Layer(new TarItem("file", current, Mode: newMode, Owner: newOwner, Group: newGroup)));
        var result = await fixture.CompareAsync("base", "target", "--scope", "files");
        Assert.Equal([reason], Assert.Single(result.Files).Reasons);
        Assert.NotNull(result.Files[0].Base!.ContentHash);
        Assert.Equal("registryBlobDigest", result.Files[0].Target!.Provenance.IdentityKind);
    }

    [Fact]
    public async Task HardLinksUseBoundContentAndPhysicalBytesAreDeduplicated()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new("original", "old"), new("alias", "original", TarEntryType.HardLink)));
        fixture.Images["target"] = Image(Layer(new("original", "new"), new("alias", "original", TarEntryType.HardLink)));
        var result = await fixture.CompareAsync("base", "target", "--scope", "files", "--scope", "sizes");
        Assert.Equal(2, result.Files.Count);
        Assert.All(result.Files, static d => Assert.Contains("content", d.Reasons));
        Assert.Equal(3, result.Sizes.Single(static s => s.Name == "effectiveLiveContentBytes").BaseBytes);
    }

    [Theory]
    [InlineData(".wh.gone", "gone")]
    [InlineData("dir/.wh..wh..opq", "dir/gone")]
    [InlineData(".wh..wh..opq", "dir/gone")]
    public async Task WhiteoutsAndOpaqueDirectoriesCompareEffectiveContents(string marker, string path)
    {
        using var fixture = new SemanticCompareFixture();
        byte[] lower = Layer(new TarItem(path, "old"));
        fixture.Images["base"] = Image(lower);
        fixture.Images["target"] = Image(lower, Layer(new TarItem(marker)));
        var result = await fixture.CompareAsync("base", "target", "--scope", "files", "--scope", "sizes");
        Assert.Contains(result.Files, d => d.Path == path && d.Kind == "removed");
        Assert.Equal(3, result.Sizes.Single(static s => s.Name == "hiddenContentBytes").TargetBytes);
    }

    [Fact]
    public async Task IgnoresOnlyFilterFilesAndRetainCountsAndOtherEvidence()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new TarItem("app/node_modules/pkg/package.json", """{"name":"pkg","version":"1"}""")));
        fixture.Images["target"] = Image(Layer(new TarItem("app/node_modules/pkg/package.json", """{"name":"pkg","version":"2"}""")));
        var result = await fixture.CompareAsync("base", "target", "--ignore-path", "app/**");
        Assert.Empty(result.Files);
        Assert.Equal(1, result.IgnoredFileDifferences);
        Assert.Single(result.Packages);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData("**/file", "file", true)]
    [InlineData("**/file", "a/b/file", true)]
    [InlineData("a/*", "a/b/c", false)]
    [InlineData("a/?", "a/b", true)]
    [InlineData("/etc/**", "etc/passwd", true)]
    [InlineData("A/**", "a/file", false)]
    public void GlobsUseImageSemantics(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, ImageComparison.CreateIgnore(pattern, false).IsMatch(path));
        Assert.Matches(ImageComparison.CreateIgnore("A/**", true), "a/file");
    }

    [Fact]
    public async Task ScopedPackageVersionSwapsCannotCancelOut()
    {
        using var fixture = new SemanticCompareFixture();
        static TarItem Package(string root, string version) => new(root + "/node_modules/pkg/package.json", $"{{\"name\":\"pkg\",\"version\":\"{version}\"}}");
        fixture.Images["base"] = Image(Layer(Package("a", "1"), Package("b", "2")));
        fixture.Images["target"] = Image(Layer(Package("a", "2"), Package("b", "1")));
        var result = await fixture.CompareAsync("base", "target", "--scope", "packages");
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(2, result.Packages.Count);
        Assert.Equal(["a/node_modules/pkg", "b/node_modules/pkg"], result.Packages.Select(static p => p.Scope));
    }

    [Fact]
    public void SharedScopedPackageComparisonPreservesExplorerFacts()
    {
        static InstalledPackageMetadata Inventory(string firstVersion, string secondVersion)
        {
            ScopedInstalledPackage[] packages =
            [
                new(InstalledPackageEcosystem.Npm, "first/node_modules/pkg", "pkg", firstVersion, "first/node_modules/pkg/package.json"),
                new(InstalledPackageEcosystem.Npm, "second/node_modules/pkg", "pkg", secondVersion, "second/node_modules/pkg/package.json")
            ];
            return new(Enum.GetValues<InstalledPackageEcosystem>().ToDictionary(
                static ecosystem => ecosystem,
                ecosystem => InstalledPackageReader.CreateMetadata(true,
                    packages.Where(p => p.Ecosystem == ecosystem).Select(static p => new InstalledPackage(p.Name, p.Version)))))
            {
                ScopedPackages = packages,
                Coverage = new Dictionary<InstalledPackageEcosystem, PackageInventoryCoverage>
                {
                    [InstalledPackageEcosystem.Npm] = new(2, 2, 0)
                }
            };
        }
        var before = Inventory("1", "2");
        var after = Inventory("2", "1");
        var explorer = ImageComparison.Packages(before, after);
        var headless = ImageComparison.ComparePackages(
            ImageComparison.PackageFacts(before), ImageComparison.PackageFacts(after), true);
        Assert.Equal(2, explorer.Count);
        Assert.Equal(
            explorer.Select(static p => (Assert.IsType<string>(p.Scope), p.Name, p.BaselineVersion, p.TargetVersion)),
            headless.Select(static p => (p.Scope, p.Name,
                p.Base is null ? null : string.Join(", ", p.Base.Versions),
                p.Target is null ? null : string.Join(", ", p.Target.Versions))));
    }

    [Fact]
    public async Task PartialPackageMetadataDoesNotAssertDefinitiveAbsence()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new TarItem("app/node_modules/pkg/package.json", """{"name":"pkg","version":"1"}""")));
        fixture.Images["target"] = Image(Layer(new TarItem("app/node_modules/pkg/package.json", "broken")));
        var result = await fixture.CompareAsync("base", "target", "--scope", "packages");
        Assert.Equal(1, result.ExitCode);
        Assert.False(Assert.Single(result.Packages).Definitive);
        Assert.Contains(result.PackageCoverage, static c => c.Operand == "target" && c.Ecosystem == "npm" && c.Failed == 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Package: foo\nStatus: deinstall ok config-files\n")]
    public async Task ValidEmptyInventoryIsNotMissingMetadata(string content)
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new TarItem("var/lib/dpkg/status", content)));
        fixture.Images["target"] = fixture.Images["base"];
        var result = await fixture.CompareAsync("base", "target", "--scope", "packages");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.PackageCoverage, static c => c.Ecosystem == "dpkg" && c.Coverage == "available");
        Assert.Contains(result.PackageCoverage, static c => c.Ecosystem == "npm" && c.Coverage == "notDetected");
    }

    [Fact]
    public async Task OneSidedInventoryDiscoveryIsIncomplete()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new TarItem("file", "value")));
        fixture.Images["target"] = Image(Layer(new TarItem("var/lib/dpkg/status", "")));
        var result = await fixture.CompareAsync("base", "target", "--scope", "packages");
        Assert.Equal(1, result.ExitCode);
        Assert.Null(result.AreEqual);
    }

    [Fact]
    public async Task RecordedAndInferredPackageAssociationsAreLabeled()
    {
        using var fixture = new SemanticCompareFixture();
        var manifest = new TarItem("app/node_modules/pkg/package.json", """{"name":"pkg","version":"1"}""");
        fixture.Images["base"] = Image(Layer(manifest, new("app/node_modules/pkg/value", "old")));
        fixture.Images["target"] = Image(Layer(manifest, new("app/node_modules/pkg/value", "new")));
        var result = await fixture.CompareAsync("base", "target", "--scope", "files", "--scope", "packages");
        Assert.Equal("inferredPath", Assert.Single(Assert.Single(result.Files).TargetPackages).Evidence);
    }

    [Fact]
    public void MissingContentHashDoesNotCompareEqual()
    {
        ImageFileSystemEntry entry = new()
        {
            Path = "file",
            Type = ImageFileType.File,
            Size = 1,
            IntroducedLayer = new(0, "sha256:fixture")
        };
        var facts = ImageComparison.CompareFiles([entry], [entry], new Dictionary<string, ScannedEntry>(),
            new Dictionary<string, ScannedEntry>(), ImageFileSystemSemantics.ForOs("linux"), false);
        Assert.True(Assert.Single(facts).Incomplete);
    }

    [Fact]
    public async Task CancelledContentAcquisitionDisposesResolvedSources()
    {
        using var fixture = new SemanticCompareFixture { CancelContent = true };
        fixture.Images["base"] = Image(Layer(new TarItem("file", "value")));
        fixture.Images["target"] = fixture.Images["base"];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.CompareAsync("base", "target"));
        Assert.Equal(2, fixture.Disposals);
    }

    [Fact]
    public async Task UnsupportedMetadataCandidateIsIncompleteNotNotDetected()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new TarItem("var/lib/dpkg/status", Type: TarEntryType.Directory)));
        fixture.Images["target"] = fixture.Images["base"];
        var result = await fixture.CompareAsync("base", "target", "--scope", "packages");
        Assert.Equal(1, result.ExitCode);
        Assert.All(result.PackageCoverage.Where(static c => c.Ecosystem == "dpkg"), static c => Assert.Equal(1, c.Failed));
    }

    [Fact]
    public async Task PartlyMalformedDatabaseRetainsObservedPackagesAndCountsSkippedRecords()
    {
        using var fixture = new SemanticCompareFixture();
        const string valid = "Package: observed\nStatus: install ok installed\nVersion: 1\n";
        fixture.Images["base"] = Image(Layer(new TarItem("var/lib/dpkg/status", valid)));
        fixture.Images["target"] = Image(Layer(new TarItem("var/lib/dpkg/status",
            valid + "\nPackage: malformed\nStatus: install ok installed\n")));
        var result = await fixture.CompareAsync("base", "target", "--scope", "packages");
        Assert.Equal(1, result.ExitCode);
        Assert.Single(result.TargetPackages);
        Assert.Contains(result.PackageCoverage, static c => c.Ecosystem == "dpkg" && c.Operand == "target" &&
            c.Parsed == 1 && c.Failed == 0 && c.SkippedRecords == 1 && c.Coverage == "partial");
    }

    [Fact]
    public async Task NuGetScopeRetainsApplicationAndRuntimeEvenForEmptyInventory()
    {
        using var fixture = new SemanticCompareFixture();
        const string deps = """{"runtimeTarget":{"name":"net/linux-x64"},"targets":{"net/linux-x64":{}},"libraries":{}}""";
        fixture.Images["base"] = Image(Layer(new TarItem("app/App.deps.json", deps)));
        fixture.Images["target"] = fixture.Images["base"];
        var result = await fixture.CompareAsync("base", "target", "--scope", "packages");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.PackageCoverage, static c => c.Ecosystem == "nuget" &&
            c.Evidence.Single().Scope == "app/App.deps.json#net/linux-x64");
    }
}
