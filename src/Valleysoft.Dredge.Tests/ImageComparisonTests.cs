using System.Formats.Tar;
using System.Text;
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

    [Fact]
    public async Task ConfigurationNormalizesKnownEmptyValuesButRetainsUnknownOnes()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = new([], Configuration([], execution: """{"Cmd":null,"Entrypoint":[],"Labels":{},"OnBuild":[]}"""));
        fixture.Images["target"] = new([], Configuration([], execution: "{}"));

        CompareResult knownFields = await fixture.CompareAsync("base", "target", "--scope", "config");

        Assert.Equal(0, knownFields.ExitCode);
        fixture.Images["base"] = new([], Configuration([], execution: """{"OnBuild":null}"""));
        CompareResult nullOnBuild = await fixture.CompareAsync("base", "target", "--scope", "config");
        Assert.Equal(0, nullOnBuild.ExitCode);

        fixture.Images["base"] = new([], Configuration([], execution: """{"futureField":null}"""));
        fixture.Images["target"] = new([], Configuration([], execution: "{}"));

        CompareResult unknownField = await fixture.CompareAsync("base", "target", "--scope", "config");

        Assert.Equal(2, unknownField.ExitCode);
        Assert.Contains(unknownField.Config, static difference => difference.Path == "Config.futureField");
    }

    [Fact]
    public async Task ConfigurationNormalizesNullKnownImageFields()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = new([], Configuration([], extra: ",\"os.version\":null"));
        fixture.Images["target"] = Image();

        CompareResult result = await fixture.CompareAsync("base", "target", "--scope", "config");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Config);
    }

    [Fact]
    public async Task InvalidDpkgStatusMakesPackageAbsenceIndeterminate()
    {
        using var fixture = new SemanticCompareFixture();
        fixture.Images["base"] = Image(Layer(new TarItem("var/lib/dpkg/status",
            "Package: example\nStatus: install ok installed\nVersion: 1\nArchitecture: amd64\n")));
        fixture.Images["target"] = Image(Layer(new TarItem("var/lib/dpkg/status",
            "Package: example\nStatus: install ok installd\nVersion: 1\nArchitecture: amd64\n")));

        CompareResult result = await fixture.CompareAsync("base", "target", "--scope", "packages");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("partial", result.Sections["packages"].Coverage);
        Assert.Contains(result.PackageCoverage, static coverage =>
            coverage.Operand == "target" && coverage.Ecosystem == "dpkg" && coverage.SkippedRecords == 1);
        ComparePackageDifference observedRemoval = Assert.Single(result.Packages,
            static package => package.Ecosystem == "dpkg" && package.Kind == "removed");
        Assert.False(observedRemoval.Definitive);
    }

    [Fact]
    public async Task UnsupportedOperatingSystemsAreNotScannedWithLinuxSemantics()
    {
        using var fixture = new SemanticCompareFixture();
        byte[] layer = Layer(new TarItem("file", "same"));
        byte[] config = Configuration([layer], os: "freebsd");
        fixture.Images["base"] = new([layer], config);
        fixture.Images["target"] = new([layer], config);

        CompareResult result = await fixture.CompareAsync("base", "target", "--scope", "files");

        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Complete);
        Assert.Equal("unavailable", result.Sections["files"].Coverage);
        Assert.Empty(result.Files);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "unsupportedFilesystemPlatform" && diagnostic.Section == "files");
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "analysisUnavailable");
    }

    [Fact]
    public async Task SizesRetainMeasurementsFromTheSupportedOperand()
    {
        using var fixture = new SemanticCompareFixture();
        byte[] baseLayer = Layer(new TarItem("file", "base"));
        byte[] targetLayer = Layer(new TarItem("file", "target"));
        fixture.Images["base"] = Image(baseLayer);
        fixture.Images["target"] = new([targetLayer], Configuration([targetLayer], os: "freebsd"));

        CompareResult result = await fixture.CompareAsync("base", "target", "--scope", "sizes");

        CompareSize live = Assert.Single(result.Sizes,
            static size => size.Name == "effectiveLiveContentBytes");
        Assert.Equal(4, live.BaseBytes);
        Assert.Null(live.TargetBytes);
        Assert.Equal("unavailable", live.Coverage);
        Assert.Equal("partial", result.Sections["sizes"].Coverage);
        Assert.Equal(1, result.ExitCode);
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
    [InlineData("a**/b", "ab", false)]
    [InlineData("a**/b", "a/x/b", true)]
    [InlineData("/etc/**", "etc/passwd", true)]
    [InlineData("A/**", "a/file", false)]
    public void GlobsUseImageSemantics(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, ImageComparison.CreateIgnore(pattern, false).IsMatch(path));
        Assert.Matches(ImageComparison.CreateIgnore("A/**", true), "a/file");
    }

    [Fact]
    public void AdversarialValidGlobDoesNotTimeout()
    {
        var pattern = ImageComparison.CreateIgnore("*a*a*a*a*a*ab", false);

        Assert.DoesNotMatch(pattern, new string('a', 100));
    }

    [Fact]
    public async Task MissingDpkgArchitectureUsesUniqueQualifiedOwnershipList()
    {
        using var fixture = new SemanticCompareFixture();
        static byte[] PackageLayer(string fileContent) => Layer(
            new TarItem("var/lib/dpkg/status",
                "Package: example\nStatus: install ok installed\nVersion: 1\n\n"),
            new TarItem("var/lib/dpkg/info/example:amd64.list", "/usr/share/example/tool\n"),
            new TarItem("usr/share/example/tool", fileContent));
        fixture.Images["base"] = Image(PackageLayer("old"));
        fixture.Images["target"] = Image(PackageLayer("new"));

        CompareResult result = await fixture.CompareAsync(
            "base", "target", "--scope", "files", "--scope", "packages");

        CompareFileDifference file = Assert.Single(result.Files);
        Assert.Equal("example", Assert.Single(file.BasePackages).Name);
        Assert.Equal("system", Assert.Single(file.BasePackages).Scope);
        Assert.Equal("example", Assert.Single(file.TargetPackages).Name);
    }

    [Fact]
    public async Task LargeDpkgOwnershipListRetainsHeadlessAssociations()
    {
        using var fixture = new SemanticCompareFixture();
        string ownership = string.Concat(Enumerable.Repeat("/usr/share/example/tool\n", 60000));
        Assert.True(Encoding.UTF8.GetByteCount(ownership) > InstalledPackageReader.MaxPackageManifestBytes);
        byte[] PackageLayer(string fileContent) => Layer(
            new TarItem("var/lib/dpkg/status",
                "Package: example\nStatus: install ok installed\nVersion: 1\nArchitecture: amd64\n\n"),
            new TarItem("var/lib/dpkg/info/example:amd64.list", ownership),
            new TarItem("usr/share/example/tool", fileContent));
        fixture.Images["base"] = Image(PackageLayer("old"));
        fixture.Images["target"] = Image(PackageLayer("new"));

        CompareResult result = await fixture.CompareAsync(
            "base", "target", "--scope", "files", "--scope", "packages");

        CompareFileDifference file = Assert.Single(result.Files);
        Assert.Equal("example", Assert.Single(file.BasePackages).Name);
        Assert.Equal("example", Assert.Single(file.TargetPackages).Name);
        Assert.DoesNotContain(result.Diagnostics,
            static diagnostic => diagnostic.Code == "ownershipEvidenceUnavailable");
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

        var partialAfter = after with
        {
            Coverage = new Dictionary<InstalledPackageEcosystem, PackageInventoryCoverage>
            {
                [InstalledPackageEcosystem.Npm] = new(3, 2, 0) { SkippedRecords = 1 }
            }
        };
        IReadOnlyList<ExplorerPackageDifference> observedChanges = ImageComparison.Packages(before, partialAfter);
        Assert.Equal(2, observedChanges.Count);
        Assert.All(observedChanges, static difference =>
            Assert.NotEqual(difference.BaselineVersion, difference.TargetVersion));
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

    [Fact]
    public async Task LayerDownloadFailurePreservesAvailableConfigurationReport()
    {
        using var fixture = new SemanticCompareFixture
        {
            ContentFailure = new HttpRequestException("fixture transport failure")
        };
        fixture.UseDaemonArchive = true;
        fixture.Images["base"] = Image(Layer(new TarItem("file", "base")));
        fixture.Images["docker://target"] = Image(Layer(new TarItem("file", "target")));

        CompareResult files = await fixture.CompareAsync(
            "base", "docker://target", "--scope", "config", "--scope", "files");
        CompareResult layers = await fixture.CompareAsync(
            "base", "docker://target", "--scope", "config", "--scope", "layers");

        Assert.Equal(1, files.ExitCode);
        Assert.Equal("unavailable", files.Sections["files"].Coverage);
        Assert.Equal("available", files.Sections["config"].Coverage);
        Assert.Contains(files.Diagnostics, static diagnostic => diagnostic.Code == "analysisUnavailable");
        Assert.Equal(1, layers.ExitCode);
        Assert.Equal("partial", layers.Sections["layers"].Coverage);
        Assert.Equal("available", layers.Sections["config"].Coverage);
        Assert.Contains(layers.Diagnostics, static diagnostic => diagnostic.Code == "layerIdentityUnavailable");
    }

    [Fact]
    public async Task MixedSourceFileComparisonDoesNotRequireRegistryDiffIds()
    {
        using var fixture = new SemanticCompareFixture { UseDaemonArchive = true };
        byte[] layer = Layer(new TarItem("file", "same"));
        byte[] config = Configuration([layer]);
        string configJson = Encoding.UTF8.GetString(config);
        configJson = System.Text.RegularExpressions.Regex.Replace(
            configJson, "\"diff_ids\":\\[[^]]*\\]", "\"diff_ids\":[]");
        fixture.Images["base"] = new([layer], Encoding.UTF8.GetBytes(configJson));
        fixture.Images["docker://target"] = Image(layer);

        CompareResult result = await fixture.CompareAsync(
            "base", "docker://target", "--scope", "files");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("available", result.Sections["files"].Coverage);
        Assert.Empty(result.Files);
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
    public async Task PackageAssociationsOnlyIncludeFilesPresentOnThatSide()
    {
        using var fixture = new SemanticCompareFixture();
        var manifest = new TarItem("app/node_modules/pkg/package.json", """{"name":"pkg","version":"1"}""");
        fixture.Images["base"] = Image(Layer(manifest, new("app/node_modules/pkg/removed", "old")));
        fixture.Images["target"] = Image(Layer(manifest, new("app/node_modules/pkg/added", "new")));
        var result = await fixture.CompareAsync("base", "target", "--scope", "files", "--scope", "packages");
        var removed = Assert.Single(result.Files, static item => item.Kind == "removed");
        var added = Assert.Single(result.Files, static item => item.Kind == "added");
        Assert.Single(removed.BasePackages);
        Assert.Empty(removed.TargetPackages);
        Assert.Empty(added.BasePackages);
        Assert.Single(added.TargetPackages);
    }

    [Fact]
    public async Task UnqualifiedDpkgOwnershipIsNotAssignedAcrossArchitectures()
    {
        using var fixture = new SemanticCompareFixture();
        static byte[] PackageLayer(string fileContent) => Layer(
            new TarItem("var/lib/dpkg/status",
                "Package: example\nStatus: install ok installed\nVersion: 1\nArchitecture: amd64\n\n" +
                "Package: example\nStatus: install ok installed\nVersion: 1\nArchitecture: i386\n\n"),
            new TarItem("var/lib/dpkg/info/example.list", "/usr/share/example/tool\n"),
            new TarItem("usr/share/example/tool", fileContent));
        fixture.Images["base"] = Image(PackageLayer("old"));
        fixture.Images["target"] = Image(PackageLayer("new"));

        CompareResult result = await fixture.CompareAsync(
            "base", "target", "--scope", "files", "--scope", "packages");

        CompareFileDifference file = Assert.Single(result.Files);
        Assert.Empty(file.BasePackages);
        Assert.Empty(file.TargetPackages);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "ownershipEvidenceAmbiguous" &&
            diagnostic.Path == "var/lib/dpkg/info/example.list");
    }

    [Fact]
    public async Task PartialDpkgInventoryDoesNotAssignUnqualifiedOwnership()
    {
        using var fixture = new SemanticCompareFixture();
        static byte[] PackageLayer(string fileContent) => Layer(
            new TarItem("var/lib/dpkg/status",
            "Package: example\nStatus: install ok installed\nVersion: 1\nArchitecture: amd64\n\n" +
            "Package: example\nStatus: install ok installed\nArchitecture: i386\n\n"),
            new TarItem("var/lib/dpkg/info/example.list", "/usr/share/example/tool\n"),
            new TarItem("usr/share/example/tool", fileContent));
        fixture.Images["base"] = Image(PackageLayer("old"));
        fixture.Images["target"] = Image(PackageLayer("new"));

        CompareResult result = await fixture.CompareAsync(
            "base", "target", "--scope", "files", "--scope", "packages");

        CompareFileDifference file = Assert.Single(result.Files);
        Assert.Empty(file.BasePackages);
        Assert.Empty(file.TargetPackages);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "ownershipEvidenceAmbiguous" &&
            diagnostic.Path == "var/lib/dpkg/info/example.list");
    }

    [Fact]
    public async Task UnresolvedUnrelatedOwnershipPathsAreAggregated()
    {
        using var fixture = new SemanticCompareFixture();
        static byte[] PackageLayer(string fileContent) => Layer(
            new TarItem("var/lib/dpkg/status",
            "Package: example\nStatus: install ok installed\nVersion: 1\nArchitecture: amd64\n"),
            new TarItem("var/lib/dpkg/info/example:amd64.list",
            string.Concat(Enumerable.Range(0, 100).Select(static i => $"/removed/package/path{i}\n"))),
            new TarItem("usr/share/changed", fileContent));
        fixture.Images["base"] = Image(PackageLayer("old"));
        fixture.Images["target"] = Image(PackageLayer("new"));

        CompareResult result = await fixture.CompareAsync(
            "base", "target", "--scope", "files", "--scope", "packages", "--limit", "0");

        CompareDiagnostic diagnostic = Assert.Single(result.Diagnostics,
            static item => item.Code == "ownershipPathsUnresolved" && item.Operand == "base");
        Assert.Null(diagnostic.Path);
        Assert.Contains("100 recorded", diagnostic.Message);
        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "ownershipPathUnresolved");
    }

    [Fact]
    public async Task IncompleteSameScopePackageVersionChangesAreMarkedObserved()
    {
        using var fixture = new SemanticCompareFixture();
        const string root = "usr/lib/python/site-packages";
        static byte[] PackageLayer(bool malformedVersionTwo)
        {
            const string root = "usr/lib/python/site-packages";
            return Layer(
            new TarItem(root + "/example-1.dist-info/METADATA", "Name: example\nVersion: 1\n"),
            new TarItem(root + "/example-2.dist-info/METADATA",
                malformedVersionTwo ? "broken" : "Name: example\nVersion: 2\n"));
        }
        fixture.Images["base"] = Image(PackageLayer(false));
        fixture.Images["target"] = Image(PackageLayer(true));

        CompareResult result = await fixture.CompareAsync("base", "target", "--scope", "packages");

        ComparePackageDifference difference = Assert.Single(result.Packages);
        Assert.Equal(root, difference.Scope);
        Assert.Equal(["1", "2"], difference.Base!.Versions);
        Assert.Equal(["1"], difference.Target!.Versions);
        Assert.False(difference.Definitive);
    }

    [Fact]
    public async Task PipAssociationsRetainEvidenceFromMultipleMetadataDirectories()
    {
        using var fixture = new SemanticCompareFixture();
        const string root = "usr/lib/python3/site-packages";
        static byte[] PackageLayer(string fileContent)
        {
            const string packageRoot = "usr/lib/python3/site-packages";
            return Layer(
                new TarItem(packageRoot + "/example-1.dist-info/METADATA", "Name: example\nVersion: 1\n"),
                new TarItem(packageRoot + "/example-1.dist-info/RECORD", ""),
                new TarItem(packageRoot + "/example-2.dist-info/METADATA", "Name: example\nVersion: 2\n"),
                new TarItem(packageRoot + "/example-2.dist-info/RECORD", "example-2.dist-info/module.py,,\n"),
                new TarItem(packageRoot + "/example-2.dist-info/module.py", fileContent));
        }
        fixture.Images["base"] = Image(PackageLayer("old"));
        fixture.Images["target"] = Image(PackageLayer("new"));

        CompareResult result = await fixture.CompareAsync(
            "base", "target", "--scope", "files", "--scope", "packages");

        CompareFileDifference file = Assert.Single(result.Files);
        Assert.Equal(root + "/example-2.dist-info/module.py", file.Path);
        Assert.Equal("recordedMetadata", Assert.Single(file.BasePackages).Evidence);
        Assert.Equal("recordedMetadata", Assert.Single(file.TargetPackages).Evidence);
    }

    [Fact]
    public async Task PipOwnershipDiagnosticTracksEachMetadataRecord()
    {
        using var fixture = new SemanticCompareFixture();
        const string root = "usr/lib/python3/site-packages";
        static byte[] PackageLayer(string fileContent)
        {
            const string packageRoot = "usr/lib/python3/site-packages";
            return Layer(
                new TarItem(packageRoot + "/example-1.dist-info/METADATA", "Name: example\nVersion: 1\n"),
                new TarItem(packageRoot + "/example-2.dist-info/METADATA", "Name: example\nVersion: 2\n"),
                new TarItem(packageRoot + "/example-2.dist-info/RECORD", "example-2.dist-info/module.py,,\n"),
                new TarItem(packageRoot + "/example-2.dist-info/module.py", fileContent));
        }
        fixture.Images["base"] = Image(PackageLayer("old"));
        fixture.Images["target"] = Image(PackageLayer("new"));

        CompareResult result = await fixture.CompareAsync(
            "base", "target", "--scope", "files", "--scope", "packages");

        CompareFileDifference file = Assert.Single(result.Files);
        Assert.Equal(root + "/example-2.dist-info/module.py", file.Path);
        Assert.Equal("recordedMetadata", Assert.Single(file.BasePackages).Evidence);
        Assert.Equal("recordedMetadata", Assert.Single(file.TargetPackages).Evidence);
        Assert.Equal(["base", "target"], result.Diagnostics
            .Where(static diagnostic => diagnostic.Code == "ownershipEvidenceNotDetected" &&
                diagnostic.Section == "packages" && diagnostic.Path == "pip")
            .Select(static diagnostic => diagnostic.Operand).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RecordedOwnershipResolvesDirectorySymlinkParents()
    {
        using var fixture = new SemanticCompareFixture();
        static byte[] PackageLayer(string fileContent) => Layer(
            new TarItem("var/lib/dpkg/status",
                "Package: example\nStatus: install ok installed\nVersion: 1\nArchitecture: amd64\n"),
            new TarItem("var/lib/dpkg/info/example:amd64.list", "/bin/tool\n"),
            new TarItem("bin", "usr/bin", TarEntryType.SymbolicLink),
            new TarItem("usr/bin/tool", fileContent));
        fixture.Images["base"] = Image(PackageLayer("old"));
        fixture.Images["target"] = Image(PackageLayer("new"));

        CompareResult result = await fixture.CompareAsync(
            "base", "target", "--scope", "files", "--scope", "packages");

        CompareFileDifference file = Assert.Single(result.Files);
        Assert.Equal("usr/bin/tool", file.Path);
        Assert.Equal("example", Assert.Single(file.BasePackages).Name);
        Assert.Equal("example", Assert.Single(file.TargetPackages).Name);
    }

    [Fact]
    public async Task MalformedPipOwnershipMetadataDoesNotAbortComparison()
    {
        using var fixture = new SemanticCompareFixture();
        const string root = "usr/lib/python3/site-packages";
        var metadata = new TarItem(root + "/pkg-1.dist-info/METADATA", "Name: pkg\nVersion: 1\n");
        fixture.Images["base"] = Image(Layer(metadata,
            new(root + "/pkg-1.dist-info/RECORD", "pkg/file,,\n"), new(root + "/pkg/file", "old")));
        fixture.Images["target"] = Image(Layer(metadata,
            new(root + "/pkg-1.dist-info/RECORD", "\"unclosed"), new(root + "/pkg/file", "new")));
        var result = await fixture.CompareAsync("base", "target", "--scope", "files", "--scope", "packages");
        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.Files, static item => item.Path.EndsWith("/pkg/file", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "ownershipEvidenceInvalid" && diagnostic.Operand == "target" &&
            diagnostic.Path!.EndsWith("/RECORD", StringComparison.Ordinal));
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
    public void UnsupportedWindowsEntriesDoNotReportSyntheticTypeOrSizeChanges()
    {
        ImageFileSystemEntry unsupported = new()
        {
            Path = "file",
            Type = ImageFileType.Other,
            Windows = new(null, false, false, "", "unsupported entry"),
            IntroducedLayer = new(0, "sha256:fixture")
        };
        ImageFileSystemEntry file = new()
        {
            Path = "file",
            Type = ImageFileType.File,
            Size = 420,
            IntroducedLayer = new(0, "sha256:fixture")
        };

        IReadOnlyList<string> reasons = ImageComparison.Reasons(unsupported, file,
            new Dictionary<string, ScannedEntry>(), new Dictionary<string, ScannedEntry>(),
            ImageFileSystemSemantics.ForOs("windows"), false);

        Assert.DoesNotContain("type", reasons);
        Assert.DoesNotContain("size", reasons);
    }

    [Fact]
    public void UnsupportedWindowsEntriesDoNotReportSyntheticLinkTargetChanges()
    {
        ImageFileSystemEntry unsupported = new()
        {
            Path = "link",
            Type = ImageFileType.Other,
            Windows = new(null, false, false, "", "unsupported metadata"),
            IntroducedLayer = new(0, "sha256:fixture")
        };
        ImageFileSystemEntry link = new()
        {
            Path = "link",
            Type = ImageFileType.SymbolicLink,
            LinkTarget = "target",
            IntroducedLayer = new(0, "sha256:fixture")
        };

        IReadOnlyList<string> reasons = ImageComparison.Reasons(unsupported, link,
            new Dictionary<string, ScannedEntry>(), new Dictionary<string, ScannedEntry>(),
            ImageFileSystemSemantics.ForOs("windows"), false);

        Assert.DoesNotContain("linkTarget", reasons);
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
