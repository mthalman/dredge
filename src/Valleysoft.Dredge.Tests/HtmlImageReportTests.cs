using System.CommandLine;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;

namespace Valleysoft.Dredge.Tests;

public sealed class HtmlImageReportTests
{
    private const string Payload = "</script><script>window.injected=true</script>";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ExploreOutputOptionsPreserveTuiAndValidateHtml()
    {
        Assert.Equal(ExploreOutput.Tui, Parse("docker://app:base").OutputFormat);
        Assert.Equal(ExploreOutput.Html, Parse("docker://app:base", "--output", "HTML").OutputFormat);
        ExploreCommand command = new(Mock.Of<IDockerRegistryClientFactory>());
        Assert.NotEmpty(command.Parse(["docker://app:base", "--output", "xml"]).Errors);
    }

    [Fact]
    public async Task ReportIncludesLayerChangesMetadataAndComparisonWithoutFileContents()
    {
        byte[] common = Tar(("app/removed", "SECRET FILE CONTENT"), ("app/value", "old"));
        byte[] baselineLayer = Tar(("app/.wh.removed", ""), ("app/value", "baseline"));
        byte[] targetLayer = Tar(("app/.wh.removed", ""), ("app/value", "target"), ("app/new", "new"));
        Sources sources = new(common, baselineLayer, targetLayer);
        await using LayerCacheTestContext cache = new();
        ExploreOptions options = Parse("docker://app:base", "--output", "html", "--compare", "docker://app:target", "--layer", "1");
        using StringWriter errors = new();

        string html = await HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(), options, errors, Token, cache.Store);

        JsonNode data = Data(html);
        Assert.Equal(2, data["images"]!.AsArray().Count);
        Assert.Equal(1, data["initialLayer"]!.GetValue<int>());
        Assert.True(data["usesContentIdentity"]!.GetValue<bool>());
        Assert.False(data["downloadSizeAvailable"]!.GetValue<bool>());
        JsonNode baseline = data["images"]![0]!;
        Assert.Equal("base", baseline["label"]!.GetValue<string>());
        Assert.Equal(baseline["layers"]!.AsArray().Sum(layer => layer!["reclaimable"]!.GetValue<long>()),
            baseline["hiddenBytes"]!.GetValue<long>());
        Assert.NotNull(baseline["findings"]);
        Assert.Contains(baseline["fileTreeMetadata"]!.AsArray(), item =>
            item!["path"]!.GetValue<string>() == "app/value" &&
            item["layer"]!.GetValue<int>() == 1 &&
            !item["wholeFilesystem"]!.GetValue<bool>() &&
            item["type"]!.GetValue<string>() == "File");
        JsonNode changedTree = baseline["fileTreeSummaries"]!.AsArray().Single(item =>
            item!["layer"]!.GetValue<int>() == 1 && !item["wholeFilesystem"]!.GetValue<bool>())!;
        Assert.Equal(8, changedTree["size"]!.GetValue<long>());
        Assert.Equal(2, changedTree["pathCount"]!.GetValue<int>());
        Assert.DoesNotContain(baseline["fileTreeSummaries"]!.AsArray(), item =>
            item!["wholeFilesystem"]!.GetValue<bool>());
        Assert.Contains("COPY", baseline["history"]![0]!["instruction"]!.GetValue<string>());
        Assert.Contains("COPY", baseline["layers"]![1]!["instruction"]!.GetValue<string>());
        Assert.Contains(baseline["layers"]![1]!["changes"]!.AsArray(), entry =>
            entry!["path"]!.GetValue<string>() == "app/removed" && entry["change"]!.GetValue<string>() == "Deleted");
        Assert.Contains(baseline["layers"]![1]!["changes"]!.AsArray(), entry =>
            entry!["path"]!.GetValue<string>() == "app/value" && entry["change"]!.GetValue<string>() == "Modified");
        JsonNode changedFile = baseline["layers"]![1]!["changes"]!.AsArray()
            .Single(entry => entry!["path"]!.GetValue<string>() == "app/value")!;
        Assert.Equal("-rw-r--r--", changedFile["mode"]!.GetValue<string>());
        Assert.Equal("0:0", changedFile["owner"]!.GetValue<string>());
        Assert.Contains(baseline["files"]!.AsArray(), entry =>
            entry!["path"]!.GetValue<string>() == "app/value" && entry["size"]!.GetValue<int>() == 8);
        Assert.Contains(data["differences"]!.AsArray(), entry =>
            entry!["path"]!.GetValue<string>() == "app/value" && entry["change"]!.GetValue<string>() == "Modified");
        Assert.Contains(data["differences"]!.AsArray(), entry =>
            entry!["path"]!.GetValue<string>() == "app/new" && entry["change"]!.GetValue<string>() == "Added");
        JsonNode folderSizes = data["pathSizes"]!.AsArray()
            .Single(entry => entry!["path"]!.GetValue<string>() == "app")!;
        Assert.Equal(8, folderSizes["before"]!.GetValue<int>());
        Assert.Equal(9, folderSizes["after"]!.GetValue<int>());
        Assert.True(folderSizes["directory"]!.GetValue<bool>());
        Assert.Equal(2, folderSizes["fileCount"]!.GetValue<int>());
        Assert.DoesNotContain("SECRET FILE CONTENT", html);
        Assert.DoesNotContain(Payload, html);
        Assert.DoesNotContain("VALUE=baseline", html);
        Assert.Equal(2, sources.Disposals);
        Assert.Contains("Docker image ID", errors.ToString());
    }

    [Fact]
    public async Task ComparisonFolderSizesCountHardLinkedContentOnce()
    {
        static byte[] LinkedLayer(string content)
        {
            using MemoryStream result = new();
            using (TarWriter writer = new(result, leaveOpen: true))
            {
                using MemoryStream data = new(Encoding.UTF8.GetBytes(content));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "app/source") { DataStream = data });
                writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "app/link") { LinkName = "app/source" });
            }
            return result.ToArray();
        }
        Sources sources = new(Tar(("common", "same"), ("app/unchanged", new string('x', 1000))),
            LinkedLayer("old"), LinkedLayer("newer"));
        await using LayerCacheTestContext cache = new();
        JsonNode data = Data(await HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(),
            Parse("docker://app:base", "--output", "html", "--compare", "docker://app:target"),
            TextWriter.Null, Token, cache.Store));

        JsonNode folder = data["pathSizes"]!.AsArray().Single(item => item!["path"]!.GetValue<string>() == "app")!;
        Assert.Equal(3, folder["before"]!.GetValue<int>());
        Assert.Equal(5, folder["after"]!.GetValue<int>());
        Assert.Equal(2, folder["fileCount"]!.GetValue<int>());
        Assert.Contains(data["pathSizes"]!.AsArray(), item => item!["path"]!.GetValue<string>() == "app/link" &&
            item["before"]!.GetValue<int>() == 3 && item["after"]!.GetValue<int>() == 5 &&
            !item["link"]!.GetValue<bool>() && item["hardLink"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ComparisonPreservesHardLinkTargetsForBothOrientations()
    {
        static byte[] LinkedLayer(bool target, params (string Path, string Target)[] links)
        {
            using MemoryStream result = new();
            using (TarWriter writer = new(result, leaveOpen: true))
            {
                foreach ((string path, string linkTarget) in links)
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, path) { LinkName = linkTarget });
                }
                if (target)
                {
                    using MemoryStream content = new(Encoding.UTF8.GetBytes("replacement"));
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "app/replaced") { DataStream = content });
                }
            }
            return result.ToArray();
        }

        Sources sources = new(Tar(("app/old", "old"), ("app/new", "new")),
            LinkedLayer(false, ("app/link", "app/old"), ("app/replaced", "app/old"),
                ("app/removed", "app/old")),
            LinkedLayer(true, ("app/link", "app/new"), ("app/added", "app/new")));
        await using LayerCacheTestContext cache = new();
        JsonNode data = Data(await HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(),
            Parse("docker://app:base", "--output", "html", "--compare", "docker://app:target"),
            TextWriter.Null, Token, cache.Store));

        JsonArray sizes = data["pathSizes"]!.AsArray();
        JsonNode Find(string path) => sizes.Single(item => item!["path"]!.GetValue<string>() == path)!;
        JsonNode link = Find("app/link");
        Assert.Equal("app/new", link["linkTarget"]!.GetValue<string>());
        Assert.Equal("app/old", link["swappedLinkTarget"]!.GetValue<string>());
        Assert.Null(Find("app/replaced")["linkTarget"]);
        Assert.Equal("app/old", Find("app/replaced")["swappedLinkTarget"]!.GetValue<string>());
        Assert.Equal("app/old", Find("app/removed")["linkTarget"]!.GetValue<string>());
        Assert.Equal("app/old", Find("app/removed")["swappedLinkTarget"]!.GetValue<string>());
        Assert.Equal("app/new", Find("app/added")["linkTarget"]!.GetValue<string>());
        Assert.Equal("app/new", Find("app/added")["swappedLinkTarget"]!.GetValue<string>());
    }

    [Fact]
    public async Task ComparisonRetainsFileSizeWhenReplacedBySymbolicLink()
    {
        using MemoryStream target = new();
        using (TarWriter writer = new(target, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "app/item") { LinkName = "other" });
        }
        Sources sources = new(Tar(("common", "same")), Tar(("app/item", "123456")), target.ToArray());
        await using LayerCacheTestContext cache = new();
        JsonNode data = Data(await HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(),
            Parse("docker://app:base", "--output", "html", "--compare", "docker://app:target"),
            TextWriter.Null, Token, cache.Store));

        JsonNode item = data["pathSizes"]!.AsArray()
            .Single(value => value!["path"]!.GetValue<string>() == "app/item")!;
        Assert.Equal(6, item["before"]!.GetValue<int>());
        Assert.Equal(0, item["after"]!.GetValue<int>());
    }

    [Fact]
    public async Task ReportIncludesImplicitDirectoryReplacementChanges()
    {
        Sources sources = new(Tar(("item", "12345")), Tar(("item/new", "123")), Tar());
        await using LayerCacheTestContext cache = new();
        JsonNode image = Data(await HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(), Parse("docker://app:base", "--output", "html"),
            TextWriter.Null, Token, cache.Store))["images"]![0]!;

        JsonArray changes = image["layers"]![1]!["changes"]!.AsArray();
        Assert.Contains(changes, item => item!["path"]!.GetValue<string>() == "item" &&
            item["change"]!.GetValue<string>() == "Deleted");
        Assert.Contains(changes, item => item!["path"]!.GetValue<string>() == "item/new" &&
            item["change"]!.GetValue<string>() == "Added" && item["size"]!.GetValue<int>() == 3);
    }

    [Fact]
    public async Task LayerSummaryExcludesDirectoryChangesAndPreservesFileFindingNotes()
    {
        static byte[] Layer(string content)
        {
            using MemoryStream result = new();
            using (TarWriter writer = new(result, leaveOpen: true))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "app/"));
                using MemoryStream data = new(Encoding.UTF8.GetBytes(content));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "app/value") { DataStream = data });
            }
            return result.ToArray();
        }

        Sources sources = new(Layer("before"), Layer("after"));
        await using LayerCacheTestContext cache = new();
        JsonNode data = Data(await HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(), Parse("docker://app:base", "--output", "html"),
            TextWriter.Null, Token, cache.Store));
        JsonNode image = data["images"]![0]!;
        JsonNode changed = image["fileTreeSummaries"]!.AsArray().Single(item =>
            item!["layer"]!.GetValue<int>() == 1 && !item["wholeFilesystem"]!.GetValue<bool>())!;

        Assert.Contains(image["layers"]![1]!["changes"]!.AsArray(), item =>
            item!["type"]!.GetValue<string>() == "Directory" && item["change"]!.GetValue<string>() == "Identical");
        Assert.Equal(1, changed["pathCount"]!.GetValue<int>());
        Assert.DoesNotContain(changed["changes"]!.AsArray(), item =>
            item!["change"]!.GetValue<string>() == "Identical" && item["count"]!.GetValue<int>() != 0);
        Assert.Contains(image["fileTreeMetadata"]!.AsArray(), item =>
            item!["layer"]!.GetValue<int>() == 1 && !item["wholeFilesystem"]!.GetValue<bool>() &&
            item["path"]!.GetValue<string>() == "app/value" &&
            item["note"]?.GetValue<string>() == "replaces layer 0");
        Assert.StartsWith("sha256:", image["digest"]!.GetValue<string>());
        Assert.True(image["isDaemon"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ReportIncludesPerLayerPackagesAndComparablePackageDifferences()
    {
        byte[] common = Tar(
            ("app/node_modules/core/package.json", """{"name":"core","version":"1","private":"SECRET PACKAGE CONTENT"}"""),
            ("app/node_modules/bad/package.json", "{"),
            ("lib/apk/db/installed", "P:busybox\nV:1\n\n"));
        byte[] baselineLayer = Tar(
            ("app/node_modules/core/package.json", """{"name":"core","version":"2"}"""),
            ("app/node_modules/removed/package.json", """{"name":"removed","version":"1"}"""));
        byte[] targetLayer = Tar(
            ("app/node_modules/core/package.json", """{"name":"core","version":"3"}"""),
            ("app/node_modules/added/package.json", """{"name":"added","version":"4"}"""),
            ("lib/apk/db/.wh.installed", ""));
        Sources sources = new(common, baselineLayer, targetLayer);
        await using LayerCacheTestContext cache = new();

        string html = await HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(),
            Parse("docker://app:base", "--output", "html", "--compare", "docker://app:target"),
            TextWriter.Null, Token, cache.Store);

        JsonNode data = Data(html);
        JsonNode baseline = data["images"]![0]!;
        JsonNode target = data["images"]![1]!;
        JsonNode Npm(JsonNode image, int layer) =>
            image["packageInventories"]![layer]!["ecosystems"]!.AsArray()
                .Single(group => group!["ecosystem"]!.GetValue<string>() == "npm")!;
        JsonNode Package(JsonNode group, string name) =>
            group["packages"]!.AsArray().Single(package => package!["name"]!.GetValue<string>() == name)!;

        Assert.Equal("1", Package(Npm(baseline, 0), "core")["versions"]![0]!.GetValue<string>());
        Assert.Equal("2", Package(Npm(baseline, 1), "core")["versions"]![0]!.GetValue<string>());
        Assert.Equal("3", Package(Npm(target, 1), "core")["versions"]![0]!.GetValue<string>());
        Assert.Contains(baseline["packageInventories"]![0]!["warnings"]!.AsArray(),
            warning => warning!.GetValue<string>().Contains("app/node_modules/bad/package.json", StringComparison.Ordinal));

        JsonArray differences = data["packageDifferences"]!.AsArray();
        Assert.Contains(differences, package => package!["name"]!.GetValue<string>() == "core" &&
            package["before"]!.GetValue<string>() == "2" && package["after"]!.GetValue<string>() == "3");
        Assert.Contains(differences, package => package!["name"]!.GetValue<string>() == "removed" &&
            package["before"]!.GetValue<string>() == "1" && package["after"] is null);
        Assert.Contains(differences, package => package!["name"]!.GetValue<string>() == "added" &&
            package["before"] is null && package["after"]!.GetValue<string>() == "4");
        Assert.DoesNotContain(differences, package => package!["ecosystem"]!.GetValue<string>() == "apk");
        Assert.False(target["packageInventories"]![1]!["ecosystems"]!.AsArray()
            .Single(group => group!["ecosystem"]!.GetValue<string>() == "apk")!["available"]!.GetValue<bool>());
        Assert.DoesNotContain("SECRET PACKAGE CONTENT", html);
    }

    [Fact]
    public async Task EmptyDaemonImageProducesHtmlThroughTheCommand()
    {
        Sources sources = new();
        using StringWriter output = new();
        ExploreCommand command = new(Mock.Of<IDockerRegistryClientFactory>(), output) { ImageSourceFactory = sources };
        RecordingProcessTerminator terminator = new();
        ((IProcessTerminationAware)command).ProcessTerminator = terminator;

        int exitCode = await command.Parse(["docker://app:base", "--output", "html"])
            .InvokeAsync(new InvocationConfiguration(), Token);

        Assert.Equal(0, exitCode);
        Assert.Null(terminator.ExitCode);
        Assert.StartsWith("<!doctype html>", output.ToString());
        Assert.Empty(Data(output.ToString())["images"]![0]!["files"]!.AsArray());
        Assert.Equal(-1, Data(output.ToString())["images"]![0]!["packageInventories"]![0]!["layer"]!.GetValue<int>());
        Assert.Equal(1, sources.Disposals);
    }

    [Fact]
    public async Task InvalidLayerDoesNotWriteAReportAndDisposesTheSource()
    {
        Sources sources = new();
        using StringWriter output = new();
        ExploreCommand command = new(Mock.Of<IDockerRegistryClientFactory>(), output) { ImageSourceFactory = sources };
        RecordingProcessTerminator terminator = new();
        ((IProcessTerminationAware)command).ProcessTerminator = terminator;

        await command.Parse(["docker://app:base", "--output", "html", "--layer", "0"])
            .InvokeAsync(new InvocationConfiguration(), Token);

        Assert.Equal(1, terminator.ExitCode);
        Assert.Empty(output.ToString());
        Assert.Equal(1, sources.Disposals);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ReportComparesRegistryAndDaemonSources(bool baseDaemon, bool targetDaemon)
    {
        byte[] common = Tar(("app/shared", "same"));
        byte[] baselineLayer = Tar(("app/value", "baseline"));
        byte[] targetLayer = Tar(("app/value", "target"));
        Sources sources = new(common, baselineLayer, targetLayer);
        Dictionary<string, byte[]> registryLayers = new(StringComparer.Ordinal);
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        foreach (string tag in new[] { "base", "target" })
        {
            await using ResolvedImage sample = await sources.OpenAsync($"docker://app:{tag}", new(), Token);
            byte[] config = Encoding.UTF8.GetBytes(JsonHelper.Serialize(sample.Configuration));
            string digest = sample.Resolved.Manifest.Config!.Digest!;
            byte[][] chosen = [common, tag == "base" ? baselineLayer : targetLayer];
            DockerManifest manifest = new()
            {
                Config = new ManifestConfig { Digest = digest },
                Layers = [.. chosen.Select(bytes =>
                {
                    byte[] compressed = Gzip(bytes);
                    string layerDigest = LayerCacheTestContext.Digest(compressed);
                    registryLayers[layerDigest] = compressed;
                    return new ManifestLayer
                    {
                        Digest = layerDigest, Size = compressed.Length,
                        MediaType = "application/vnd.oci.image.layer.v1.tar+gzip"
                    };
                })]
            };
            ManifestInfo info = new("application/vnd.docker.distribution.manifest.v2+json",
                LayerCacheTestContext.Digest(Encoding.UTF8.GetBytes("registry-" + tag)), manifest);
            client.Setup(value => value.Manifests.GetAsync("app", tag, It.IsAny<CancellationToken>()))
                .ReturnsAsync(info);
            client.Setup(value => value.Blobs.GetAsync("app", digest, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(config));
        }
        client.Setup(value => value.Blobs.GetRangeAsync("app", It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string digest, long start, long? length, CancellationToken token) =>
            {
                byte[] blob = registryLayers[digest];
                return Task.FromResult(new BlobDownloadResult(new MemoryStream(blob), false, null, null, blob.Length));
            });
        Mock<IDockerRegistryClientFactory> registry = new();
        registry.Setup(value => value.GetClientAsync("registry.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        string baseline = baseDaemon ? "docker://app:base" : "registry.test/app:base";
        string target = targetDaemon ? "docker://app:target" : "registry.test/app:target";
        ExploreOptions options = Parse(baseline, "--output", "html", "--compare", target);
        await using LayerCacheTestContext cache = new();

        JsonNode data = Data(await HtmlImageReport.CreateAsync(
            sources, registry.Object, options, TextWriter.Null, Token, cache.Store));

        Assert.Equal(baseline, data["images"]![0]!["reference"]!.GetValue<string>());
        Assert.Equal(target, data["images"]![1]!["reference"]!.GetValue<string>());
        bool usesContentIdentity = baseDaemon || targetDaemon;
        Assert.Equal(usesContentIdentity, data["usesContentIdentity"]!.GetValue<bool>());
        Assert.Equal(!usesContentIdentity, data["downloadSizeAvailable"]!.GetValue<bool>());
        JsonNode before = data["images"]![0]!["layers"]![0]!;
        JsonNode after = data["images"]![1]!["layers"]![0]!;
        string identity = before["comparisonIdentity"]!.GetValue<string>();
        Assert.Equal(identity, after["comparisonIdentity"]!.GetValue<string>());
        if (usesContentIdentity)
        {
            Assert.NotEqual(before["digest"]!.GetValue<string>(), after["digest"]!.GetValue<string>());
            Assert.Equal(LayerCacheTestContext.Digest(common), identity);
        }
        else
        {
            Assert.Equal(before["digest"]!.GetValue<string>(), identity);
        }
        Assert.Contains(data["differences"]!.AsArray(), entry =>
            entry!["path"]!.GetValue<string>() == "app/value" && entry["change"]!.GetValue<string>() == "Modified");
    }

    [Fact]
    public async Task CanceledReportDoesNotOpenAnySources()
    {
        Sources sources = new();
        await using LayerCacheTestContext cache = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(), Parse("docker://app:base", "--output", "html"),
            TextWriter.Null, new CancellationToken(canceled: true), cache.Store));
        Assert.Equal(0, sources.Disposals);
    }

    [Fact]
    public async Task FailedTargetDisposesTheBaseline()
    {
        Sources sources = new() { FailTarget = true };
        await using LayerCacheTestContext cache = new();
        await Assert.ThrowsAsync<IOException>(() => HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(),
            Parse("docker://app:base", "--output", "html", "--compare", "docker://app:target"),
            TextWriter.Null, Token, cache.Store));
        Assert.Equal(1, sources.Disposals);
    }

    [Fact]
    public async Task AmbiguousRegistryPlatformFailsWithoutInteractiveSelection()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        ManifestList index = new()
        {
            Manifests =
            [
                new ManifestReference { Digest = "sha256:one", Platform = new ManifestPlatform { Os = "linux", Architecture = "amd64" } },
                new ManifestReference { Digest = "sha256:two", Platform = new ManifestPlatform { Os = "linux", Architecture = "arm64" } }
            ]
        };
        client.Setup(value => value.Manifests.GetAsync("app", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", index));
        Mock<IDockerRegistryClientFactory> registry = new();
        registry.Setup(value => value.GetClientAsync("registry.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        ExploreOptions options = Parse("registry.test/app", "--output", "html");
        options.UsePlatformDefaults = false;
        await using LayerCacheTestContext cache = new();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HtmlImageReport.CreateAsync(new Sources(), registry.Object, options, TextWriter.Null, Token, cache.Store));

        Assert.Contains("matching platforms", error.Message);
    }

    [Fact]
    public async Task WindowsReportIncludesFilesystemLimitations()
    {
        Sources sources = new() { Os = "windows" };
        await using LayerCacheTestContext cache = new();
        JsonNode data = Data(await HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(), Parse("docker://app:base", "--output", "html"),
            TextWriter.Null, Token, cache.Store));

        Assert.Contains(data["images"]![0]!["warnings"]!.AsArray(), warning =>
            warning!.GetValue<string>().Contains("Windows content-only", StringComparison.Ordinal));
        Assert.Contains("unavailable for Windows", data["images"]![0]!["packageInventories"]![0]!["unavailableReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task WindowsReportPreservesPathIdentityAndDoesNotInventUnixMetadata()
    {
        Sources sources = new(
            Tar(("Files/Folder/Thing", "old"), ("Files/ß/file", "one"), ("Files/ss/other", "two")),
            Tar(("Files/folder/New", "new"), ("Files/FOLDER/thing", "changed"))) { Os = "windows" };
        await using LayerCacheTestContext cache = new();
        JsonNode data = Data(await HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(), Parse("docker://app:base", "--output", "html"),
            TextWriter.Null, Token, cache.Store));

        JsonNode image = data["images"]![0]!;
        Dictionary<string, int> identities = image["pathIdentities"]!.AsArray()
            .ToDictionary(item => item!["path"]!.GetValue<string>(), item => item!["id"]!.GetValue<int>());
        Assert.Equal(identities["Folder"], identities["folder"]);
        Assert.NotEqual(identities["ß"], identities["ss"]);
        JsonNode entry = image["layers"]![0]!["changes"]!.AsArray()
            .Single(item => item!["path"]!.GetValue<string>() == "Folder/Thing")!;
        Assert.Equal("Windows attributes unavailable", entry["mode"]!.GetValue<string>());
        Assert.Equal("ACL/owner not interpreted", entry["owner"]!.GetValue<string>());
    }

    [Fact]
    public async Task WindowsComparisonReportsPackageInventoryUnavailable()
    {
        Sources sources = new() { Os = "windows" };
        await using LayerCacheTestContext cache = new();

        JsonNode data = Data(await HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(),
            Parse("docker://app:base", "--output", "html", "--compare", "docker://app:target"),
            TextWriter.Null, Token, cache.Store));

        Assert.All(data["images"]!.AsArray(), image =>
            Assert.Contains("unavailable for Windows",
                image!["packageInventories"]![0]!["unavailableReason"]!.GetValue<string>()));
        Assert.Empty(data["packageDifferences"]!.AsArray());
    }

    [Fact]
    public async Task WindowsComparisonUsesSharedFilesystemPathIdentities()
    {
        Sources sources = new(
            Tar(("Files/common", "same")),
            Tar(("Files/ς/a", "old"), ("Files/Folder/one", "old")),
            Tar(("Files/σ/b", "new"), ("Files/folder/two", "new"))) { Os = "windows" };
        await using LayerCacheTestContext cache = new();

        JsonNode data = Data(await HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(),
            Parse("docker://app:base", "--output", "html", "--compare", "docker://app:target"),
            TextWriter.Null, Token, cache.Store));

        Dictionary<string, int> identities = data["comparisonPathIdentities"]!.AsArray()
            .ToDictionary(item => item!["path"]!.GetValue<string>(), item => item!["id"]!.GetValue<int>());
        Assert.NotEqual(identities["ς"], identities["σ"]);
        Assert.Equal(identities["Folder"], identities["folder"]);
    }

    [Fact]
    public async Task WholeFilesystemMetadataIsNotRepeatedForUnchangedFiles()
    {
        byte[] common = Tar([.. Enumerable.Range(0, 300).Select(i => ($"app/file-{i}", "contents"))]);
        Sources sources = new(common, Tar(("app/changed", "new")));
        await using LayerCacheTestContext cache = new();

        JsonNode image = Data(await HtmlImageReport.CreateAsync(
            sources, Mock.Of<IDockerRegistryClientFactory>(), Parse("docker://app:base", "--output", "html"),
            TextWriter.Null, Token, cache.Store))["images"]![0]!;

        JsonArray metadata = image["fileTreeMetadata"]!.AsArray();
        Assert.DoesNotContain(metadata, item => item!["wholeFilesystem"]!.GetValue<bool>());
        Assert.Single(metadata, item => item!["layer"]!.GetValue<int>() == 1 &&
            item["path"]!.GetValue<string>() == "app/changed");
        Assert.True(metadata.Count < 400);
    }

    [Fact]
    public void ReportDataIsEmbeddedCompressed()
    {
        HtmlReportFile[] changes = [.. Enumerable.Range(0, 5000).Select(i => new HtmlReportFile($"usr/lib/package-{i}/file.txt", "File", i, "Added"))];
        HtmlReportImage image = new("image", "image", "snapshot", "linux", "image", "download", [],
            [new(0, "sha256:a", "sha256:a", 0, 1, 0, null, null, "COPY app", changes)], [], [], []);
        HtmlReportData report = new([image], null, null, [], null, false, false);

        string html = HtmlImageReport.Render(report);

        Assert.DoesNotContain("usr/lib/package-", html);
        Assert.Equal(4999, Data(html)["images"]![0]!["layers"]![0]!["changes"]![4999]!["size"]!.GetValue<int>());
        Assert.True(html.Length - HtmlImageReport.Render(new([], null, null, [], null, false, false)).Length <
            JsonSerializer.Serialize(report, DredgeJsonContext.Default.HtmlReportData).Length / 4);
    }

    [Fact]
    public void UntrustedReportStringsRemainData()
    {
        string path = Payload + "\u03b1";
        HtmlReportImage image = new(Payload, "payload", "snapshot", "linux", Payload, "download", [],
            [new(0, "sha256:a", "sha256:a", 0, 1, 0, null, null, "COPY app", [new(path, "File", 1, "Added")])], [], [],
            [new(0, [new("npm", true, [new(Payload, ["1"])])], [], null)]);

        string html = HtmlImageReport.Render(new([image], null, null, [], null, false, false));

        Assert.DoesNotContain(Payload, html);
        Assert.Equal(path, Data(html)["images"]![0]!["layers"]![0]!["changes"]![0]!["path"]!.GetValue<string>());
        Assert.Equal(Payload, Data(html)["images"]![0]!["packageInventories"]![0]!["ecosystems"]![0]!["packages"]![0]!["name"]!.GetValue<string>());
        Assert.Contains("textContent", html);
        Assert.DoesNotContain("innerHTML", html);
        Assert.Contains("Warnings and limitations", html);
        Assert.Contains("data-change-filter", html);
        Assert.Contains("No differences match these filters.", html);
        Assert.Contains("Nothing matches the current filters.", html);
    }

    [Fact]
    public void ReportModelHasSourceGeneratedJsonMetadata()
    {
        HtmlReportData report = new(
            [new("image", "image", "sha256:image", "linux/amd64", "image", "download", [],
                [new(0, "sha256:layer", "sha256:layer", 4, 4, 0, null, null, "COPY hello", [new("hello", "File", 4, "Added")])], [], [],
                [new(0, [new("npm", true, [new("example", ["1"])])], [], null)], [new("Hello", 0)])],
            [new("hello", "Added", null, 4)], [new("npm", "example", null, "1")], [], 0, true, false,
            [new("hello", null, 4, false, 1, null, null, "Added", false)]);

        JsonNode data = JsonNode.Parse(JsonSerializer.Serialize(report, DredgeJsonContext.Default.HtmlReportData))!;

        Assert.Equal("hello", data["images"]![0]!["layers"]![0]!["changes"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("sha256:layer", data["images"]![0]!["layers"]![0]!["comparisonIdentity"]!.GetValue<string>());
        Assert.Equal("Hello", data["images"]![0]!["pathIdentities"]![0]!["path"]!.GetValue<string>());
        Assert.True(data["usesContentIdentity"]!.GetValue<bool>());
        Assert.False(data["downloadSizeAvailable"]!.GetValue<bool>());
        Assert.Equal("Added", data["differences"]![0]!["change"]!.GetValue<string>());
        Assert.Equal("example", data["packageDifferences"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(4, data["pathSizes"]![0]!["after"]!.GetValue<int>());
        Assert.Equal("image", data["images"]![0]!["label"]!.GetValue<string>());
        Assert.False(data["pathSizes"]![0]!["directory"]!.GetValue<bool>());
    }

    private static ExploreOptions Parse(params string[] args)
    {
        ExploreOptions options = new();
        Command command = new("explore");
        options.SetCommandOptions(command);
        ParseResult result = command.Parse(args);
        Assert.Empty(result.Errors);
        options.SetParseResult(result);
        return options;
    }

    private static JsonNode Data(string html)
    {
        const string prefix = "<script type=\"application/octet-stream\" id=\"report-data\">";
        int start = html.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        int end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        Assert.True(start >= prefix.Length && end > start);
        using GZipStream gzip = new(new MemoryStream(Convert.FromBase64String(html[start..end])), CompressionMode.Decompress);
        return JsonNode.Parse(gzip)!;
    }

    private static byte[] Tar(params (string Path, string Content)[] entries)
    {
        using MemoryStream result = new();
        using (TarWriter writer = new(result, leaveOpen: true))
        {
            foreach ((string path, string value) in entries)
            {
                using MemoryStream data = new(Encoding.UTF8.GetBytes(value));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = data });
            }
        }
        return result.ToArray();
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using MemoryStream result = new();
        using (GZipStream gzip = new(result, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes);
        }
        return result.ToArray();
    }

    private sealed class Sources(params byte[][] layers) : IImageSourceFactory, IImageContentSource
    {
        public int Disposals { get; private set; }
        public bool FailTarget { get; init; }
        public string Os { get; init; } = "linux";

        public Task<ResolvedImage> OpenAsync(
            string reference, PlatformOptionsBase options, CancellationToken cancellationToken,
            ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool target = reference.EndsWith(":target", StringComparison.Ordinal);
            if (target && FailTarget) throw new IOException("Target acquisition failed.");
            byte[][] rawLayers = layers.Length == 0 ? [] : [layers[0], layers[target ? 2 : 1]];
            byte[][] chosen = Os == "windows" ? [.. rawLayers.Select(Gzip)] : rawLayers;
            DockerManifest manifest = new()
            {
                Config = new ManifestConfig { Digest = LayerCacheTestContext.Digest(Encoding.UTF8.GetBytes(reference)) },
                Layers = [.. chosen.Select(bytes => new ManifestLayer
                {
                    Digest = LayerCacheTestContext.Digest(bytes), Size = bytes.Length,
                    MediaType = Os == "windows" ? "application/vnd.oci.image.layer.v1.tar+gzip" : "application/vnd.oci.image.layer.v1.tar"
                })]
            };
            Image config = new()
            {
                Os = Os, Architecture = "amd64",
                Config = new ImageConfig { EnvironmentVariables = [$"VALUE={(target ? "target" : "baseline")}", Payload] },
                RootFilesystem = new RootFilesystem
                {
                    Type = "layers", DiffIds = [.. rawLayers.Select(LayerCacheTestContext.Digest)]
                },
                History = [.. chosen.Select((_, index) => new LayerHistory { CreatedBy = $"COPY {index} /app" })]
            };
            ManifestInfo info = new("application/vnd.docker.distribution.manifest.v2+json",
                LayerCacheTestContext.Digest(Encoding.UTF8.GetBytes(reference)), manifest);
            ImageSourceReference parsed = ImageSourceReference.Parse(reference);
            return Task.FromResult(new ResolvedImage(parsed, parsed.GetImageName(), new(info, manifest), info,
                config, [], this, info.DockerContentDigest, $"{Os}/amd64", new Owner(() => Disposals++)));
        }

        public Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data = (Os == "windows" ? layers.Select(Gzip) : layers)
                .Single(bytes => LayerCacheTestContext.Digest(bytes) == digest);
            return Task.FromResult(new BlobDownloadResult(new MemoryStream(data), false, null, null, data.Length));
        }
    }

    private sealed class Owner(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }
}
