namespace Valleysoft.Dredge.Tests;

public class InstalledPackageReaderTests
{
    [Theory]
    [InlineData("app/node_modules/react/package.json")]
    [InlineData("app/node_modules/@scope/package/package.json")]
    [InlineData("app/node_modules/parent/node_modules/child/package.json")]
    [InlineData("app/.pnpm/store/node_modules/package/package.json")]
    public void IsNpmPackageManifestPath_InstalledManifest_ReturnsTrue(string path)
    {
        Assert.True(InstalledPackageReader.IsNpmPackageManifestPath(path));
    }

    [Theory]
    [InlineData("app/package.json")]
    [InlineData("app/package-lock.json")]
    [InlineData("app/node_modules/package/package-lock.json")]
    [InlineData("app/node_modules/package/test/package.json")]
    [InlineData("app/node_modules/@scope/package/test/package.json")]
    public void IsNpmPackageManifestPath_NonInstalledManifest_ReturnsFalse(string path)
    {
        Assert.False(InstalledPackageReader.IsNpmPackageManifestPath(path));
    }

    [Theory]
    [InlineData("usr/lib/python3.13/site-packages/requests-2.32.3.dist-info/METADATA")]
    [InlineData("opt/venv/lib/python3.12/site-packages/My_Package-1.0.DIST-INFO/METADATA")]
    public void IsPipMetadataPath_DistInfoMetadata_ReturnsTrue(string path)
    {
        Assert.True(InstalledPackageReader.IsPipMetadataPath(path));
    }

    [Theory]
    [InlineData("usr/lib/python3.13/site-packages/package/METADATA")]
    [InlineData("usr/lib/python3.13/site-packages/package.dist-info/metadata")]
    [InlineData("requirements.txt")]
    public void IsPipMetadataPath_OtherPath_ReturnsFalse(string path)
    {
        Assert.False(InstalledPackageReader.IsPipMetadataPath(path));
    }

    [Fact]
    public void ParseNpmPackageJson_ReturnsNameAndVersion()
    {
        InstalledPackage package = InstalledPackageReader.ParseNpmPackageJson(
            """
            {
              "name": "@scope/example",
              "description": "test",
              "version": "2.3.4"
            }
            """,
            "node_modules/@scope/example/package.json");

        Assert.Equal(new InstalledPackage("@scope/example", "2.3.4"), package);
    }

    [Fact]
    public void ParseNpmPackageJson_Utf8Bom_ReturnsPackage()
    {
        InstalledPackage package = InstalledPackageReader.ParseNpmPackageJson(
            "\uFEFF{\"name\":\"example\",\"version\":\"1.0.0\"}",
            "node_modules/example/package.json");

        Assert.Equal(new InstalledPackage("example", "1.0.0"), package);
    }

    [Theory]
    [InlineData("""{"version":"1.0.0"}""")]
    [InlineData("""{"name":"example"}""")]
    [InlineData("""{"name":"","version":"1.0.0"}""")]
    [InlineData("""{"name":"example","version":1}""")]
    [InlineData("""[]""")]
    [InlineData("""{invalid}""")]
    public void ParseNpmPackageJson_InvalidMetadata_Throws(string content)
    {
        Assert.Throws<InvalidDataException>(() =>
            InstalledPackageReader.ParseNpmPackageJson(content, "node_modules/example/package.json"));
    }

    [Fact]
    public void ParseDpkgStatus_ReturnsOnlyInstalledPackages()
    {
        const string Content =
            """
            Package: installed
            Status: install ok installed
            Architecture: amd64
            Version: 1:2.3-4
            Description: first line
             continuation

            Package: removed
            Status: deinstall ok config-files
            Version: 5.0

            Package: also-installed
            Status: install ok installed
            Version: 6.7
            """;

        IReadOnlyList<InstalledPackage> packages = InstalledPackageReader.ParseDpkgStatus(Content);

        Assert.Equal(
            [
                new InstalledPackage("installed", "1:2.3-4"),
                new InstalledPackage("also-installed", "6.7")
            ],
            packages);
    }

    [Theory]
    [InlineData("install ok installed")]
    [InlineData("hold ok installed")]
    [InlineData("deinstall ok installed")]
    [InlineData("purge ok installed")]
    public void ParseDpkgStatus_UsesInstallationStateNotSelection(string status)
    {
        Assert.Equal([new InstalledPackage("example", "1")],
            InstalledPackageReader.ParseDpkgStatus($"Package: example\nStatus: {status}\nVersion: 1\n"));
    }

    [Theory]
    [InlineData("hold ok config-files")]
    [InlineData("install ok unpacked")]
    [InlineData("install ok half-configured")]
    public void ParseDpkgStatus_ExcludesPackagesNotInstalled(string status)
    {
        Assert.Empty(InstalledPackageReader.ParseDpkgStatus($"Package: example\nStatus: {status}\nVersion: 1\n"));
    }

    [Theory]
    [InlineData("Package: example\nStatus: install ok installed\n")]
    [InlineData("Status: install ok installed\nVersion: 1.0\n")]
    [InlineData("Package: example\nPackage: duplicate\nStatus: install ok installed\nVersion: 1.0\n")]
    [InlineData("not-a-field\n")]
    public void ParseDpkgStatus_InvalidInstalledParagraph_Throws(string content)
    {
        Assert.Throws<InvalidDataException>(() => InstalledPackageReader.ParseDpkgStatus(content));
    }

    [Fact]
    public void ParseApkInstalled_ReturnsPackages()
    {
        const string Content =
            """
            C:Q1checksum
            P:musl
            V:1.2.5-r1
            A:x86_64

            C:Q1other
            P:busybox
            V:1.36.1-r29
            A:x86_64
            """;

        IReadOnlyList<InstalledPackage> packages = InstalledPackageReader.ParseApkInstalled(Content);

        Assert.Equal(
            [
                new InstalledPackage("musl", "1.2.5-r1"),
                new InstalledPackage("busybox", "1.36.1-r29")
            ],
            packages);
    }

    [Fact]
    public void ParseApkInstalled_AcceptsCaseSensitiveAndRepeatedKeys()
    {
        const string Content =
            """
            C:Q1Oq1zGhBcV0t+QUOHLVdXP9WqzSg=
            P:alpine-baselayout-data
            V:3.7.0-r0
            A:x86_64
            S:11211
            I:77824
            T:Alpine base dir structure and init scripts
            U:https://git.alpinelinux.org/cgit/aports/tree/main/alpine-baselayout
            L:GPL-2.0-only
            o:alpine-baselayout
            m:Natanael Copa <ncopa@alpinelinux.org>
            t:1740000000
            c:ab12cd34
            r:alpine-baselayout
            q:1000
            F:etc
            R:fstab
            Z:Q11Q7hNe8QpDS531guqCdrXBzoA/o=
            R:group
            Z:Q12Otk4M39fP2Zjkobu0nC9FvlRI0=
            F:etc/apk
            R:world
            a:0:0:644

            P:busybox
            V:1.37.0-r12
            T:Size optimized toolbox
            t:1740000001
            """;

        Assert.Equal(
            [
                new InstalledPackage("alpine-baselayout-data", "3.7.0-r0"),
                new InstalledPackage("busybox", "1.37.0-r12")
            ],
            InstalledPackageReader.ParseApkInstalled(Content));
    }

    [Theory]
    [InlineData("P:musl\n")]
    [InlineData("V:1.2.5-r1\n")]
    [InlineData("invalid\n")]
    public void ParseApkInstalled_InvalidParagraph_Throws(string content)
    {
        Assert.Throws<InvalidDataException>(() => InstalledPackageReader.ParseApkInstalled(content));
    }

    [Fact]
    public void ParsePipMetadata_ReturnsNameAndVersion()
    {
        const string Content =
            """
            Metadata-Version: 2.4
            Name: Requests
            Version: 2.32.3
            Summary: Python HTTP for Humans.
            Requires-Dist: charset_normalizer<4,>=2
             ; python_version > "3"

            Long description is not part of the headers.
            """;

        InstalledPackage package = InstalledPackageReader.ParsePipMetadata(
            Content,
            "site-packages/requests-2.32.3.dist-info/METADATA");

        Assert.Equal(new InstalledPackage("Requests", "2.32.3"), package);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Name: example\nName: duplicate\nVersion: 1\n")]
    [InlineData("Name: example\nVersion: 1\nVersion: 2\n")]
    [InlineData(" continuation\nName: example\nVersion: 1\n")]
    [InlineData("Metadata-Version: 2.4\nVersion: 1.0\n")]
    [InlineData("Metadata-Version: 2.4\nName: example\n")]
    [InlineData("invalid\n")]
    public void ParsePipMetadata_InvalidMetadata_Throws(string content)
    {
        Assert.Throws<InvalidDataException>(() =>
            InstalledPackageReader.ParsePipMetadata(content, "example.dist-info/METADATA"));
    }

    [Fact]
    public void ParsePipMetadata_AllowsRepeatedHeadersAndIgnoresDescription()
    {
        string content = "Name: Requests\nVersion: 2.32.3\n" +
            "Requires-Dist: charset_normalizer<4,>=2\n ; python_version > \"3\"\n" +
            "Requires-Dist: idna<4,>=2.5\nClassifier: First\nClassifier: Second\n" +
            "Project-URL: Documentation, https://example.test\nProject-URL: Source, https://example.test/source\n" +
            "\nDescription: includes arbitrary text\nName: not-a-header\n";

        Assert.Equal(new InstalledPackage("Requests", "2.32.3"),
            InstalledPackageReader.ParsePipMetadata(content, "requests.dist-info/METADATA"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1048576)]
    public void ValidateManifestSize_WithinLimit_DoesNotThrow(long size)
    {
        InstalledPackageReader.ValidateManifestSize(
            "package.json",
            size,
            InstalledPackageReader.MaxPackageManifestBytes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1048577)]
    public void ValidateManifestSize_OutsideLimit_Throws(long size)
    {
        Assert.Throws<InvalidDataException>(() =>
            InstalledPackageReader.ValidateManifestSize(
                "package.json",
                size,
                InstalledPackageReader.MaxPackageManifestBytes));
    }

    [Fact]
    public void CreateMetadata_Available_GroupsNamesAndDistinctSortedVersions()
    {
        InstalledPackageEcosystemMetadata metadata = InstalledPackageReader.CreateMetadata(
            true,
            [
                new InstalledPackage("nested", "2.0.0"),
                new InstalledPackage("alpha", "1.0.0"),
                new InstalledPackage("nested", "1.0.0"),
                new InstalledPackage("nested", "2.0.0")
            ]);

        Assert.Equal(InstalledPackageMetadataAvailability.Available, metadata.Availability);
        Assert.Equal(["alpha", "nested"], metadata.Packages.Keys);
        Assert.Equal(["1.0.0"], metadata.Packages["alpha"]);
        Assert.Equal(["1.0.0", "2.0.0"], metadata.Packages["nested"]);
    }

    [Fact]
    public void CreateMetadata_Unavailable_HasExplicitStateAndNoPackages()
    {
        InstalledPackageEcosystemMetadata metadata =
            InstalledPackageReader.CreateMetadata(false, []);

        Assert.Equal(InstalledPackageMetadataAvailability.Unavailable, metadata.Availability);
        Assert.Empty(metadata.Packages);
    }
}
