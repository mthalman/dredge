using System.CommandLine;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Moq;
using Valleysoft.DockerRegistryClient;
using Valleysoft.DockerRegistryClient.Models;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;

namespace Valleysoft.Dredge.Tests;

public sealed class HtmlReportAuthenticationTests
{
    [Theory]
    [InlineData("baseline.test/app:base", "target.test/app:target", false, "client", "baseline.test")]
    [InlineData("baseline.test/app:base", "target.test/app:target", false, "manifest", "baseline.test")]
    [InlineData("baseline.test/app:base", "target.test/app:target", false, "config", "baseline.test")]
    [InlineData("baseline.test/app:base", "target.test/app:target", false, "layer", "baseline.test")]
    [InlineData("baseline.test/app:base", "target.test/app:target", true, "client", "target.test")]
    [InlineData("baseline.test/app:base", "target.test/app:target", true, "manifest", "target.test")]
    [InlineData("baseline.test/app:base", "registry://target.test/app:target", true, "config", "target.test")]
    [InlineData("baseline.test/app:base", "target.test/app:target", true, "layer", "target.test")]
    [InlineData("baseline.test/app:base", "target", true, "manifest", "baseline.test")]
    [InlineData("baseline.test/app:base", "app:target", true, "manifest", null)]
    [InlineData("docker://app:base", "target.test/app:target", true, "layer", "target.test")]
    [InlineData("baseline.test/app:base", "docker://app:target", true, "manifest", null)]
    public async Task UnauthorizedIdentifiesTheFailingImageRegistry(
        string baseline, string compare, bool failTarget, string stage, string? expectedRegistry)
    {
        string target = ExploreCommand.ResolveCompareReference(baseline, compare);
        ImageSourceReference baselineReference = ImageSourceReference.Parse(baseline);
        ImageSourceReference targetReference = ImageSourceReference.Parse(target);
        RegistryException failure = RegistryError("UNAUTHORIZED", "unauthorized");
        Mock<IDockerRegistryClient> baselineClient = new() { DefaultValue = DefaultValue.Mock };
        Mock<IDockerRegistryClient> targetClient = targetReference.Registry == baselineReference.Registry
            ? baselineClient : new() { DefaultValue = DefaultValue.Mock };
        Mock<IDockerRegistryClientFactory> registries = new();
        ConfigureRegistry(baselineClient, baselineReference, !failTarget ? stage : null, failure);
        ConfigureRegistry(targetClient, targetReference, failTarget ? stage : null, failure);
        registries.Setup(factory => factory.GetClientAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string? registry, CancellationToken _) =>
            {
                if (stage == "client" && registry == (failTarget ? targetReference.Registry : baselineReference.Registry))
                {
                    return Task.FromException<IDockerRegistryClient>(failure);
                }
                return Task.FromResult(registry == baselineReference.Registry
                    ? baselineClient.Object : targetClient.Object);
            });
        DaemonSources sources = new(failTarget ? target : null, failure);

        ExploreOptions options = Parse(baseline, "--output", "html", "--compare", compare);
        await using LayerCacheTestContext cache = new();
        using StringWriter error = new();
        int? exitCode = null;
        await CommandHelper.ExecuteCommandAsync(baselineReference.Registry, CancellationToken.None,
            async ct => _ = await HtmlImageReport.CreateAsync(
                sources, registries.Object, options, TextWriter.Null, ct, cache.Store),
            error, code => exitCode = code, operationTimeout: Timeout.InfiniteTimeSpan);

        string login = expectedRegistry is null ? "docker login" : $"docker login {expectedRegistry}";
        Assert.Equal(1, exitCode);
        Assert.Equal(
            $"The repository does not exist or may require authentication. If authentication is required, ensure that your credentials are stored for the registry by running '{login}'." +
            Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task RegistryContextPreservesExceptionAndInnermostRegistry()
    {
        RegistryException failure = RegistryError("UNAUTHORIZED", "unauthorized");
        RegistryException actual = await Assert.ThrowsAsync<RegistryException>(() =>
            CommandHelper.WithRegistryAsync("outer.test", () =>
                CommandHelper.WithRegistryAsync("inner.test", () => Task.FromException(failure))));
        Assert.Same(failure, actual);

        using StringWriter error = new();
        await CommandHelper.ExecuteCommandAsync("baseline.test", CancellationToken.None,
            _ => Task.FromException(actual), error, _ => { }, Timeout.InfiniteTimeSpan);
        Assert.Contains("'docker login inner.test'", error.ToString());
    }

    [Fact]
    public async Task RegistryContextUsesTheExistingJsonErrorFormatter()
    {
        Command command = new("test");
        command.Options.Add(new JsonOutputOption("Output format", "text", "text", "json"));
        CommandHelper.SetJsonErrorOutput(command.Parse(["--output", "json"]));
        using StringWriter error = new();
        int? exitCode = null;
        try
        {
            await CommandHelper.ExecuteCommandAsync("baseline.test", CancellationToken.None,
                _ => CommandHelper.WithRegistryAsync("target.test",
                    () => Task.FromException(RegistryError("UNAUTHORIZED", "unauthorized"))),
                error, code => exitCode = code, Timeout.InfiniteTimeSpan);
        }
        finally
        {
            CommandHelper.SetJsonErrorOutput(new Command("test").Parse([]));
        }

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "{\"error\":{\"code\":\"UNAUTHORIZED\",\"message\":\"The repository does not exist or may require authentication. " +
            "If authentication is required, ensure that your credentials are stored for the registry by running 'docker login target.test'.\"}}" +
            Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task OtherErrorsAndCancellationKeepTheirExistingBehavior()
    {
        RegistryException denied = RegistryError("DENIED", "access denied");
        using StringWriter error = new();
        int? exitCode = null;
        await CommandHelper.ExecuteCommandAsync("baseline.test", CancellationToken.None,
            _ => CommandHelper.WithRegistryAsync("target.test", () => Task.FromException(denied)),
            error, code => exitCode = code, Timeout.InfiniteTimeSpan);
        Assert.Equal(1, exitCode);
        Assert.Equal("access denied" + Environment.NewLine, error.ToString());

        error.GetStringBuilder().Clear();
        await CommandHelper.ExecuteCommandAsync("baseline.test", CancellationToken.None,
            _ => CommandHelper.WithRegistryAsync("target.test",
                () => Task.FromException(new InvalidOperationException("failure"))),
            error, code => exitCode = code, Timeout.InfiniteTimeSpan);
        Assert.Equal(1, exitCode);
        Assert.Equal("failure" + Environment.NewLine, error.ToString());

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        error.GetStringBuilder().Clear();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CommandHelper.ExecuteCommandAsync("baseline.test", cancellation.Token,
                ct => CommandHelper.WithRegistryAsync("target.test", () => Task.FromCanceled(ct)),
                error, operationTimeout: Timeout.InfiniteTimeSpan));
        Assert.Empty(error.ToString());
    }

    private static void ConfigureRegistry(
        Mock<IDockerRegistryClient> client, ImageSourceReference reference, string? failureStage, RegistryException failure)
    {
        if (reference.IsDaemon) return;
        ImageName image = reference.GetImageName();
        byte[] raw = Layer(image.Tag!);
        byte[] compressed;
        using (MemoryStream buffer = new())
        {
            using (GZipStream gzip = new(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(raw);
            }
            compressed = buffer.ToArray();
        }
        string digest = LayerCacheTestContext.Digest(compressed);
        string configDigest = LayerCacheTestContext.Digest(Encoding.UTF8.GetBytes(image.ToString()));
        DockerManifest manifest = new()
        {
            Config = new ManifestConfig { Digest = configDigest },
            Layers = [new ManifestLayer
            {
                Digest = digest, Size = compressed.Length,
                MediaType = "application/vnd.oci.image.layer.v1.tar+gzip"
            }]
        };
        ManifestInfo info = new("application/vnd.docker.distribution.manifest.v2+json",
            LayerCacheTestContext.Digest(Encoding.UTF8.GetBytes("manifest-" + image)), manifest);
        Image config = new()
        {
            Os = "linux", Architecture = "amd64",
            RootFilesystem = new RootFilesystem { Type = "layers", DiffIds = [LayerCacheTestContext.Digest(raw)] }
        };
        byte[] json = Encoding.UTF8.GetBytes(JsonHelper.Serialize(config));
        var manifestCall = client.Setup(value => value.Manifests.GetAsync(image.Repo, image.Tag!, It.IsAny<CancellationToken>()));
        if (failureStage == "manifest") manifestCall.ThrowsAsync(failure);
        else manifestCall.ReturnsAsync(info);
        var configCall = client.Setup(value => value.Blobs.GetAsync(image.Repo, configDigest, It.IsAny<CancellationToken>()));
        if (failureStage == "config") configCall.ThrowsAsync(failure);
        else configCall.ReturnsAsync(() => new MemoryStream(json));
        var layerCall = client.Setup(value => value.Blobs.GetRangeAsync(image.Repo, digest, It.IsAny<long>(),
            It.IsAny<long?>(), It.IsAny<CancellationToken>()));
        if (failureStage == "layer") layerCall.ThrowsAsync(failure);
        else layerCall.ReturnsAsync(() => new BlobDownloadResult(
            new MemoryStream(compressed), false, null, null, compressed.Length));
    }

    private static byte[] Layer(string value)
    {
        using MemoryStream result = new();
        using (TarWriter writer = new(result, leaveOpen: true))
        using (MemoryStream data = new(Encoding.UTF8.GetBytes(value)))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "value") { DataStream = data });
        }
        return result.ToArray();
    }

    private sealed class DaemonSources(string? failReference, RegistryException failure)
        : IImageSourceFactory, IImageContentSource
    {
        public Task<ResolvedImage> OpenAsync(
            string reference, PlatformOptionsBase options, CancellationToken cancellationToken,
            ImageSourceRequirements requirements = ImageSourceRequirements.LayerContent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reference == failReference) return Task.FromException<ResolvedImage>(failure);
            ImageSourceReference parsed = ImageSourceReference.Parse(reference);
            DockerManifest manifest = new()
            {
                Config = new ManifestConfig { Digest = LayerCacheTestContext.Digest(Encoding.UTF8.GetBytes(reference)) },
                Layers = []
            };
            ManifestInfo info = new("application/vnd.docker.distribution.manifest.v2+json",
                LayerCacheTestContext.Digest(Encoding.UTF8.GetBytes(reference)), manifest);
            Image image = new()
            {
                Os = "linux", Architecture = "amd64",
                RootFilesystem = new RootFilesystem { Type = "layers", DiffIds = [] }
            };
            return Task.FromResult(new ResolvedImage(parsed, parsed.GetImageName(), new(info, manifest), info, image, [],
                this, info.DockerContentDigest, "linux/amd64"));
        }

        public Task<BlobDownloadResult> GetRangeAsync(
            string digest, long start, long? length, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The empty daemon image has no layers.");
    }

    private static RegistryException RegistryError(string code, string message) =>
        new("failure") { Errors = [new Error { Code = code, Message = message }] };

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
}
