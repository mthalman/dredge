using System.CommandLine;
using System.Formats.Tar;
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
        JsonNode baseline = data["images"]![0]!;
        Assert.Contains("VALUE=baseline", baseline["metadata"]!.GetValue<string>());
        Assert.Contains("COPY", baseline["history"]![0]!["instruction"]!.GetValue<string>());
        Assert.Contains(baseline["layers"]![1]!["changes"]!.AsArray(), entry =>
            entry!["path"]!.GetValue<string>() == "app/removed" && entry["change"]!.GetValue<string>() == "Deleted");
        Assert.Contains(baseline["layers"]![1]!["changes"]!.AsArray(), entry =>
            entry!["path"]!.GetValue<string>() == "app/value" && entry["change"]!.GetValue<string>() == "Modified");
        Assert.Contains(baseline["files"]!.AsArray(), entry =>
            entry!["path"]!.GetValue<string>() == "app/value" && entry["size"]!.GetValue<int>() == 8);
        Assert.Contains(data["differences"]!.AsArray(), entry =>
            entry!["path"]!.GetValue<string>() == "app/value" && entry["change"]!.GetValue<string>() == "Modified");
        Assert.Contains(data["differences"]!.AsArray(), entry =>
            entry!["path"]!.GetValue<string>() == "app/new" && entry["change"]!.GetValue<string>() == "Added");
        Assert.DoesNotContain("SECRET FILE CONTENT", html);
        Assert.DoesNotContain(Payload, html);
        Assert.Contains(Payload, baseline["metadata"]!.GetValue<string>());
        Assert.Equal(2, sources.Disposals);
        Assert.Contains("Docker image ID", errors.ToString());
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
        Sources sources = new(common, Tar(("app/value", "baseline")), Tar(("app/value", "target")));
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        foreach (string tag in new[] { "base", "target" })
        {
            await using ResolvedImage sample = await sources.OpenAsync($"docker://app:{tag}", new(), Token);
            byte[] config = Encoding.UTF8.GetBytes(JsonHelper.Serialize(sample.Configuration));
            string digest = sample.Resolved.Manifest.Config!.Digest!;
            client.Setup(value => value.Manifests.GetAsync("app", tag, It.IsAny<CancellationToken>()))
                .ReturnsAsync(sample.Resolved.ManifestInfo);
            client.Setup(value => value.Blobs.GetAsync("app", digest, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(config));
        }
        client.Setup(value => value.Blobs.GetRangeAsync("app", It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string digest, long start, long? length, CancellationToken token) =>
                sources.GetRangeAsync(digest, start, length, token));
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
    }

    [Fact]
    public void UntrustedReportStringsRemainData()
    {
        string path = Payload + "\u03b1";
        HtmlReportImage image = new(Payload, "snapshot", "linux", Payload, [],
            [new(0, "sha256:a", 0, null, [new(path, "File", 1, "Added")])], [], []);

        string html = HtmlImageReport.Render(new([image], null, [], null));

        Assert.DoesNotContain(Payload, html);
        Assert.Equal(path, Data(html)["images"]![0]!["layers"]![0]!["changes"]![0]!["path"]!.GetValue<string>());
        Assert.Contains("textContent", html);
        Assert.DoesNotContain("innerHTML", html);
    }

    [Fact]
    public void ReportModelHasSourceGeneratedJsonMetadata()
    {
        HtmlReportData report = new(
            [new("image", "sha256:image", "linux/amd64", "{}", [],
                [new(0, "sha256:layer", 4, null, [new("hello", "File", 4, "Added")])], [], [])],
            [new("hello", "Added", null, 4)], [], 0);

        JsonNode data = JsonNode.Parse(JsonSerializer.Serialize(report, DredgeJsonContext.Default.HtmlReportData))!;

        Assert.Equal("hello", data["images"]![0]!["layers"]![0]!["changes"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("Added", data["differences"]![0]!["change"]!.GetValue<string>());
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
        html = html.Replace("\r\n", "\n", StringComparison.Ordinal);
        const string prefix = "const report = ";
        int start = html.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        int end = html.IndexOf(";\nconst imageSelect", start, StringComparison.Ordinal);
        Assert.True(start >= prefix.Length && end > start);
        return JsonNode.Parse(html[start..end])!;
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
            byte[][] chosen = layers.Length == 0 ? [] : [layers[0], layers[target ? 2 : 1]];
            DockerManifest manifest = new()
            {
                Config = new ManifestConfig { Digest = LayerCacheTestContext.Digest(Encoding.UTF8.GetBytes(reference)) },
                Layers = [.. chosen.Select(bytes => new ManifestLayer
                {
                    Digest = LayerCacheTestContext.Digest(bytes), Size = bytes.Length,
                    MediaType = "application/vnd.oci.image.layer.v1.tar"
                })]
            };
            Image config = new()
            {
                Os = Os, Architecture = "amd64",
                Config = new ImageConfig { EnvironmentVariables = [$"VALUE={(target ? "target" : "baseline")}", Payload] },
                RootFilesystem = new RootFilesystem
                {
                    Type = "layers", DiffIds = [.. chosen.Select(LayerCacheTestContext.Digest)]
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
            byte[] data = layers.Single(bytes => LayerCacheTestContext.Digest(bytes) == digest);
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
