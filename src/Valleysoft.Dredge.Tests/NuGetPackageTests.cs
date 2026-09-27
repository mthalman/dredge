namespace Valleysoft.Dredge.Tests;

public class NuGetPackageTests
{
    [Theory]
    [InlineData("app/App.deps.json", true)]
    [InlineData("App.DEPS.JSON", true)]
    [InlineData("root/.nuget/packages/example/1.0/example.nuspec", false)]
    [InlineData("app/obj/project.assets.json", false)]
    [InlineData("app/App.runtimeconfig.json", false)]
    public void DetectsDependencyManifestsOnly(string path, bool expected)
    {
        Assert.Equal(expected, InstalledPackageReader.IsNuGetDepsPath(path));
    }

    [Fact]
    public void ReadsOnlyPackagesInTheSelectedRuntimeTarget()
    {
        const string content = """
            {
              "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/linux-x64"},
              "targets": {
                ".NETCoreApp,Version=v10.0": {"Other/9.0": {}},
                ".NETCoreApp,Version=v10.0/linux-x64": {
                  "Example.App/1.0": {},
                  "Newtonsoft.Json/13.0.3": {},
                  "Transitive.Package/2.0.0-preview.1": {},
                  "Local.Assembly/1.0": {}
                }
              },
              "libraries": {
                "Other/9.0": {"type": "package"},
                "Example.App/1.0": {"type": "project"},
                "Newtonsoft.Json/13.0.3": {"type": "package"},
                "Transitive.Package/2.0.0-preview.1": {"type": "package"},
                "Local.Assembly/1.0": {"type": "reference"}
              }
            }
            """;

        Assert.Equal(
            [new InstalledPackage("newtonsoft.json", "13.0.3"),
             new InstalledPackage("transitive.package", "2.0.0-preview.1")],
            InstalledPackageReader.ParseNuGetDepsJson(content, "app/App.deps.json"));
    }

    [Fact]
    public void EmptyPackageSetIsValidAndBomIsAccepted()
    {
        Assert.Empty(InstalledPackageReader.ParseNuGetDepsJson(
            "\uFEFF" + """{"runtimeTarget":{"name":"net"},"targets":{"net":{"App/1":{"runtime":{}}}},"libraries":{"App/1":{"type":"project"}}}""",
            "App.deps.json"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"runtimeTarget":{"name":"net"},"targets":{},"libraries":{}}""")]
    [InlineData("""{"runtimeTarget":{"name":"net"},"targets":{"net":{"Pkg/1":{}}},"libraries":{}}""")]
    [InlineData("""{"runtimeTarget":{"name":"net"},"targets":{"net":{"Pkg":{}}},"libraries":{"Pkg":{"type":"package"}}}""")]
    [InlineData("""{"runtimeTarget":{"name":"net"},"targets":{"net":{"Pkg/":{}}},"libraries":{"Pkg/":{"type":"package"}}}""")]
    [InlineData("""{"runtimeTarget":{"name":"net"},"targets":{"net":{"Pkg/1":{}}},"libraries":{"Pkg/1":{"type":42}}}""")]
    public void RejectsMalformedDependencyMetadata(string content)
    {
        Assert.Throws<InvalidDataException>(() =>
            InstalledPackageReader.ParseNuGetDepsJson(content, "App.deps.json"));
    }

    [Fact]
    public void MultipleApplicationsAggregatePackageVersionsWithoutCaseDuplicates()
    {
        static IReadOnlyList<InstalledPackage> Parse(string name, string version) =>
            InstalledPackageReader.ParseNuGetDepsJson(
                """{"runtimeTarget":{"name":"net"},"targets":{"net":{"IDENTITY":{}}},"libraries":{"IDENTITY":{"type":"package"}}}"""
                    .Replace("IDENTITY", $"{name}/{version}", StringComparison.Ordinal),
                "App.deps.json");

        InstalledPackageEcosystemMetadata metadata = InstalledPackageReader.CreateMetadata(true,
            Parse("Example", "2.0").Concat(Parse("example", "1.0")).Concat(Parse("EXAMPLE", "2.0")));

        Assert.Equal(["example"], metadata.Packages.Keys);
        Assert.Equal(["1.0", "2.0"], metadata.Packages["example"]);
    }

    [Fact]
    public void ComparisonIncludesAddedRemovedAndChangedNuGetPackages()
    {
        static ExplorerSession Session(params InstalledPackage[] packages)
        {
            ExplorerSession template = ExplorerSamples.Session([], [], []);
            Dictionary<InstalledPackageEcosystem, InstalledPackageEcosystemMetadata> ecosystems =
                template.Packages.Ecosystems.ToDictionary(pair => pair.Key, pair => pair.Value);
            ecosystems[InstalledPackageEcosystem.NuGet] = InstalledPackageReader.CreateMetadata(true, packages);
            return new ExplorerSession
            {
                Image = template.Image,
                Resolved = template.Resolved,
                Config = template.Config,
                Files = template.Files,
                Analysis = template.Analysis,
                Entries = template.Entries,
                Packages = new(ecosystems)
            };
        }

        ExplorerComparison comparison = ExplorerSession.Compare(
            Session(new("changed", "1"), new("removed", "1"), new("same", "1")),
            Session(new("changed", "2"), new("added", "1"), new("same", "1")));

        Assert.Equal(
            [
                new ExplorerPackageDifference(InstalledPackageEcosystem.NuGet, "added", null, "1"),
                new ExplorerPackageDifference(InstalledPackageEcosystem.NuGet, "changed", "1", "2"),
                new ExplorerPackageDifference(InstalledPackageEcosystem.NuGet, "removed", "1", null)
            ],
            comparison.Packages);
    }
}
