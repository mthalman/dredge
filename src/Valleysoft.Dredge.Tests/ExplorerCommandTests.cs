using System.CommandLine;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Docker;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Commands.Image;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using DockerManifestReference = Valleysoft.DockerRegistryClient.Models.Manifests.Docker.ManifestReference;
using static Valleysoft.Dredge.Tests.ExplorerSamples;

namespace Valleysoft.Dredge.Tests;

// The command surface of `dredge image explore`: flags, settings and validation.
public class ExploreCommandOptionTests
{
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

    [Fact]
    public void FlagsPassThroughToTheExplorer()
    {
        ExploreOptions options = Parse("shop/app:1", "--layer", "3", "--compare", "2.0",
            "--base-image", "base:1", "--base-image", "base:2");

        ExplorerOptions explorer = ExploreCommand.CreateExplorerOptions(options, new ExploreSettings());

        Assert.Equal("shop/app:1", options.Image);
        Assert.Equal(["base:1", "base:2"], options.BaseImages);
        Assert.Equal(3, explorer.Layer);
        Assert.Equal("2.0", explorer.Compare);
        Assert.True(explorer.Mouse);
        Assert.Equal(Clipboard.Resolve(OperatingSystem.IsWindows(),
            Clipboard.IsRemoteSession(Environment.GetEnvironmentVariable)), explorer.Clipboard);
        Assert.Equal('q', explorer.Keys[KeyAction.Quit]);
        Assert.Equal(OperatingSystem.IsWindows() ? "cmd.exe" : "less", explorer.ViewerExePath);
        Assert.Equal(OperatingSystem.IsWindows() ? "/d /s /c \"more < \"{0}\"\"" : "-X \"{0}\"", explorer.ViewerArgs);
        Assert.True(explorer.ViewerUsesTerminal);
        Assert.True(explorer.PauseAfterViewer);
        Assert.Null(explorer.Notice);
    }

    [Fact]
    public void ViewerSettingsConfigureExecutableAndArguments()
    {
        ExploreSettings settings = new();
        settings.Viewer.ExePath = "custom-viewer";
        settings.Viewer.Args = "--read-only \"{0}\"";

        ExplorerOptions explorer = ExploreCommand.CreateExplorerOptions(Parse("app"), settings);

        Assert.Equal("custom-viewer", explorer.ViewerExePath);
        Assert.Equal("--read-only \"{0}\"", explorer.ViewerArgs);
        Assert.False(explorer.ViewerUsesTerminal);
        Assert.False(explorer.PauseAfterViewer);
    }

    [Fact]
    public void CustomTerminalViewerCanUseTheTerminal()
    {
        ExploreSettings settings = new();
        settings.Viewer.ExePath = "vim";
        settings.Viewer.Terminal = "true";

        Assert.True(ExploreCommand.CreateExplorerOptions(Parse("app"), settings).ViewerUsesTerminal);
        Assert.False(ExploreCommand.CreateExplorerOptions(Parse("app"), settings).PauseAfterViewer);
    }

    [Fact]
    public void InvalidViewerTerminalSettingIsReported()
    {
        ExploreSettings settings = new();
        settings.Viewer.ExePath = "viewer";
        settings.Viewer.Terminal = "maybe";

        Assert.Contains("explore.viewer.terminal",
            Assert.Throws<InvalidOperationException>(() =>
                ExploreCommand.CreateExplorerOptions(Parse("app"), settings)).Message);
    }

    [Theory]
    [InlineData(false, "true", true)]
    [InlineData(true, "true", false)]
    [InlineData(false, "false", false)]
    [InlineData(true, "false", false)]
    public void NoMouseFlagWinsOverTheMouseSetting(bool noMouse, string setting, bool expected)
    {
        ExploreOptions options = noMouse ? Parse("app", "--no-mouse") : Parse("app");

        ExplorerOptions explorer = ExploreCommand.CreateExplorerOptions(options, new ExploreSettings { Mouse = setting });

        Assert.Equal(expected, explorer.Mouse);
    }

    [Fact]
    public void SettingsRemapKeysWithoutChangingClipboardSelection()
    {
        ExploreSettings settings = new();
        settings.Keys.Quit = "Q";

        ExplorerOptions explorer = ExploreCommand.CreateExplorerOptions(Parse("app"), settings);

        Assert.Equal(Clipboard.Resolve(OperatingSystem.IsWindows(),
            Clipboard.IsRemoteSession(Environment.GetEnvironmentVariable)), explorer.Clipboard);
        Assert.Equal('Q', explorer.Keys[KeyAction.Quit]);
    }

    [Fact]
    public void InvalidSettingsAreReportedBeforeTheExplorerStarts()
    {
        Assert.Contains("explore.mouse",
            Assert.Throws<InvalidOperationException>(() =>
                ExploreCommand.CreateExplorerOptions(Parse("app"), new ExploreSettings { Mouse = "yes" })).Message);
    }

    [Theory]
    [InlineData(null, 4)]
    [InlineData(0, 4)]
    [InlineData(3, 4)]
    [InlineData(null, 0)]
    public void AcceptsLayersInRange(int? layer, int count) => ExploreCommand.ValidateLayer(layer, count);

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void RejectsLayersOutOfRange(int layer)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ExploreCommand.ValidateLayer(layer, 4));
        Assert.Equal("--layer must be between 0 and 3.", error.Message);
    }

    [Fact]
    public void RejectsALayerForAnImageWithoutLayers()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ExploreCommand.ValidateLayer(0, 0));
        Assert.Equal("--layer can't be used with an image that has no layers.", error.Message);
    }

    [Theory]
    [InlineData("2.0", "registry.test/shop/app:2.0")]
    [InlineData("shop/other:1", "shop/other:1")]
    [InlineData("other.test/app:3", "other.test/app:3")]
    public void CompareAcceptsABareTagOrAFullReference(string input, string expected) =>
        Assert.Equal(ImageName.Parse(expected).ToString(),
            ExploreCommand.ResolveCompareImage(ImageName.Parse("registry.test/shop/app:1.0"), input).ToString());

    [Fact]
    public void CompareRejectsAnEmptyReference() =>
        Assert.Throws<ArgumentException>(() => ExploreCommand.ResolveCompareImage(ImageName.Parse("app:1"), " "));
}

// Opening an image: platform resolution, Linux-only support and base verification.
public class ExplorerSourceResolutionTests
{
    private static readonly ImageName Image = ImageName.Parse("image");

    [Fact]
    public async Task PlatformSettingsBreakAmbiguity()
    {
        Mock<IDockerRegistryClient> client = ListClient(("sha256:amd", "amd64"), ("sha256:arm", "arm64"));
        AppSettings settings = new();
        settings.Platform.Architecture = "arm64";

        (ResolvedManifest resolved, _, ExplorerPlatform? platform) = await ExplorerSource.ResolveAsync(
            client.Object, Image, new PlatformOptionsBase(), null, TestContext.Current.CancellationToken,
            settingsStore: new TestAppSettingsStore(settings));

        Assert.Equal("sha256:arm", resolved.ManifestInfo.DockerContentDigest);
        Assert.Equal(new ExplorerPlatform("linux", "arm64", null, null), platform);
    }

    [Fact]
    public async Task PlatformOptionsWinOverSettings()
    {
        Mock<IDockerRegistryClient> client = ListClient(("sha256:amd", "amd64"), ("sha256:arm", "arm64"));
        AppSettings settings = new();
        settings.Platform.Architecture = "arm64";

        (ResolvedManifest resolved, _, _) = await ExplorerSource.ResolveAsync(
            client.Object, Image, new PlatformOptionsBase { Architecture = "amd64" }, null,
            TestContext.Current.CancellationToken, settingsStore: new TestAppSettingsStore(settings));

        Assert.Equal("sha256:amd", resolved.ManifestInfo.DockerContentDigest);
    }

    [Fact]
    public async Task AmbiguousPlatformWithoutAChooserListsTheMatches()
    {
        Mock<IDockerRegistryClient> client = ListClient(("sha256:amd", "amd64"), ("sha256:arm", "arm64"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExplorerSource.ResolveAsync(
            client.Object, Image, new PlatformOptionsBase(), null, TestContext.Current.CancellationToken,
            settingsStore: new TestAppSettingsStore(new AppSettings())));

        Assert.Contains("has 2 matching platforms", error.Message);
        Assert.Contains("linux/amd64, linux/arm64", error.Message);
    }

    [Fact]
    public async Task AmbiguousPlatformAsksTheChooser()
    {
        Mock<IDockerRegistryClient> client = ListClient(("sha256:amd", "amd64"), ("sha256:arm", "arm64"));
        IReadOnlyList<ExplorerPlatform>? offered = null;

        (ResolvedManifest resolved, IReadOnlyList<ExplorerPlatform> platforms, _) = await ExplorerSource.ResolveAsync(
            client.Object, Image, new PlatformOptionsBase(), null, TestContext.Current.CancellationToken,
            choosePlatform: list => (offered = list)[1], settingsStore: new TestAppSettingsStore(new AppSettings()));

        Assert.Equal("sha256:arm", resolved.ManifestInfo.DockerContentDigest);
        Assert.Equal(2, offered!.Count);
        Assert.Equal(2, platforms.Count);
    }

    [Fact]
    public async Task DismissingTheChooserCancels()
    {
        Mock<IDockerRegistryClient> client = ListClient(("sha256:amd", "amd64"), ("sha256:arm", "arm64"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => ExplorerSource.ResolveAsync(
            client.Object, Image, new PlatformOptionsBase(), null, TestContext.Current.CancellationToken,
            choosePlatform: _ => null, settingsStore: new TestAppSettingsStore(new AppSettings())));
    }

    [Fact]
    public async Task NoMatchingPlatformListsWhatIsAvailable()
    {
        Mock<IDockerRegistryClient> client = ListClient(("sha256:amd", "amd64"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExplorerSource.ResolveAsync(
            client.Object, Image, new PlatformOptionsBase { Architecture = "s390x" }, null,
            TestContext.Current.CancellationToken, settingsStore: new TestAppSettingsStore(new AppSettings())));

        Assert.StartsWith("No platform in ", error.Message);
        Assert.Contains("matches the requested platform", error.Message);
        Assert.Contains("linux/amd64", error.Message);
    }

    [Fact]
    public async Task ManifestListWithoutLinuxIsNotSupported()
    {
        ManifestList list = new() { Manifests = [Reference("sha256:win", "windows", "amd64")] };
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", list));

        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(() => ExplorerSource.ResolveAsync(
            client.Object, Image, new PlatformOptionsBase(), null, TestContext.Current.CancellationToken));

        Assert.Contains("has no Linux platform", error.Message);
    }

    [Fact]
    public void PlatformListsOnlyDistinctLinuxEntries()
    {
        ManifestList list = new()
        {
            Manifests =
            [
                Reference("sha256:a", "linux", "amd64"),
                Reference("sha256:b", "windows", "amd64"),
                Reference("sha256:c", "linux", "amd64"),
                Reference("sha256:d", "linux", "arm", "v7"),
            ]
        };

        Assert.Equal(
            [new ExplorerPlatform("linux", "amd64", null, null), new ExplorerPlatform("linux", "arm", "v7", null)],
            ExplorerSource.GetPlatforms(list));
        Assert.Empty(ExplorerSource.GetPlatforms(new DockerManifest { Layers = [] }));
        Assert.Equal("linux/arm/v7", new ExplorerPlatform("linux", "arm", "v7", null).ToString());
    }

    [Fact]
    public async Task OpenRejectsWindowsImages()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        DockerManifest manifest = new() { Config = new ManifestConfig { Digest = "sha256:config" }, Layers = [] };
        client.Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/manifest", "sha256:m", manifest));
        client.Setup(o => o.Blobs.GetAsync("library/image", "sha256:config", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"os\":\"windows\",\"architecture\":\"amd64\"}")));

        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(() => ExplorerSource.OpenAsync(
            client.Object, Mock.Of<IDockerRegistryClientFactory>(), Image, new PlatformOptionsBase(), null,
            TestContext.Current.CancellationToken));

        Assert.Equal("The image explorer supports Linux images only (found 'windows').", error.Message);
    }

    private static Mock<IDockerRegistryClient> ListClient(params (string Digest, string Architecture)[] entries)
    {
        ManifestList list = new() { Manifests = entries.Select(e => Reference(e.Digest, "linux", e.Architecture)).ToArray() };
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(o => o.Manifests.GetAsync("library/image", "latest", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/index", "sha256:index", list));
        foreach ((string digest, _) in entries)
        {
            client.Setup(o => o.Manifests.GetAsync("library/image", digest, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ManifestInfo("application/manifest", digest, new DockerManifest { Layers = [] }));
        }
        return client;
    }

    private static DockerManifestReference Reference(string digest, string os, string architecture, string? variant = null) =>
        new() { Digest = digest, Platform = new ManifestPlatform { Os = os, Architecture = architecture, Variant = variant! } };
}

public class ExplorerBaseVerificationTests
{
    private static readonly ImageName Image = ImageName.Parse("registry.test/shop/app:1");

    private static ResolvedManifest Target(Dictionary<string, string>? annotations)
    {
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = "sha256:config" },
            Layers = [Layer("sha256:l0"), Layer("sha256:l1"), Layer("sha256:l2")],
            Annotations = annotations!,
        };
        return new ResolvedManifest(new ManifestInfo("application/vnd.oci.image.manifest.v1+json", "sha256:app", manifest), manifest);
    }

    private static OciDescriptor Layer(string digest) => new() { Digest = digest, Size = 10 };

    private static void SetupBase(Mock<IDockerRegistryClient> client, string repo, string tag, string digest, params string[] layers)
    {
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = "sha256:baseconfig" },
            Layers = layers.Select(Layer).ToArray(),
        };
        client.Setup(o => o.Manifests.GetAsync(repo, tag, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/vnd.oci.image.manifest.v1+json", digest, manifest));
    }

    private static async Task<(int? Count, string? Name, string? Warning)> Verify(
        Mock<IDockerRegistryClient> client, ResolvedManifest target, string? explicitBase,
        IDockerRegistryClientFactory? factory = null, ExplorerPlatform? platform = null)
    {
        (IReadOnlyList<ExplorerBaseImage> bases, string? warning) = await ExplorerSession.VerifyBasesAsync(
            client.Object, factory ?? Mock.Of<IDockerRegistryClientFactory>(), Image, target,
            new PlatformOptionsBase(), explicitBase is null ? null : [explicitBase],
            TestContext.Current.CancellationToken, platform);
        return (bases.LastOrDefault()?.LayerCount, bases.LastOrDefault()?.Name, warning);
    }

    private static Task<(IReadOnlyList<ExplorerBaseImage> Bases, string? Warning)> VerifyChain(
        Mock<IDockerRegistryClient> client, ResolvedManifest target, params string[] bases) =>
        ExplorerSession.VerifyBasesAsync(client.Object, Mock.Of<IDockerRegistryClientFactory>(), Image, target,
            new PlatformOptionsBase(), bases, TestContext.Current.CancellationToken);

    [Fact]
    public async Task BasesHaveSeparateVerifiedLayerBoundariesRegardlessOfInputOrder()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        SetupBase(client, "base", "1", "sha256:one", "sha256:l0");
        SetupBase(client, "base", "2", "sha256:two", "sha256:l0", "sha256:l1");

        foreach (string[] input in new[]
        {
            new[] { "registry.test/base:1", "registry.test/base:2" },
            new[] { "registry.test/base:2", "registry.test/base:1" }
        })
        {
            (IReadOnlyList<ExplorerBaseImage> bases, string? warning) = await VerifyChain(
                client, Target(Annotated("registry.test/base:2", "sha256:two")), input);

            Assert.Null(warning);
            Assert.Equal([new ExplorerBaseImage("registry.test/base:1", 1),
                new ExplorerBaseImage("registry.test/base:2", 2)], bases);
        }
    }

    [Fact]
    public async Task RepeatedBaseBoundariesFail()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        SetupBase(client, "base", "1", "sha256:one", "sha256:l0");
        SetupBase(client, "base", "2", "sha256:two", "sha256:l0", "sha256:l1");

        SetupBase(client, "base", "same", "sha256:same", "sha256:l0");
        foreach (string[] chain in new[]
        {
            new[] { "registry.test/base:1", "registry.test/base:1" },
            new[] { "registry.test/base:same", "registry.test/base:1" }
        })
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                VerifyChain(client, Target(null), chain));
            Assert.Contains("same layer boundary", error.Message);
        }
    }

    [Fact]
    public async Task EveryBaseInTheChainMustMatchTheExploredImage()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        SetupBase(client, "base", "1", "sha256:one", "sha256:elsewhere");
        SetupBase(client, "base", "2", "sha256:two", "sha256:l0", "sha256:l1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyChain(client, Target(null),
            "registry.test/base:1", "registry.test/base:2"));
    }

    [Fact]
    public async Task DeepestBaseMustAgreeWithAnnotation()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        SetupBase(client, "base", "1", "sha256:one", "sha256:l0");
        SetupBase(client, "base", "2", "sha256:two", "sha256:l0", "sha256:l1");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyChain(client,
            Target(Annotated("registry.test/base:1")), "registry.test/base:2", "registry.test/base:1"));
        Assert.Contains("disagrees with the annotation", error.Message);
    }

    private static void SetupArmBase(Mock<IDockerRegistryClient> client)
    {
        ManifestList list = new()
        {
            Manifests =
            [
                new DockerManifestReference { Digest = "sha256:v5", Platform = new ManifestPlatform { Os = "linux", Architecture = "arm", Variant = "v5" } },
                new DockerManifestReference { Digest = "sha256:v7", Platform = new ManifestPlatform { Os = "linux", Architecture = "arm", Variant = "v7" } },
            ],
        };
        client.Setup(o => o.Manifests.GetAsync("base", "1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("application/vnd.docker.distribution.manifest.list.v2+json", "sha256:list", list));
        SetupBase(client, "base", "sha256:v5", "sha256:v5", "sha256:elsewhere");
        SetupBase(client, "base", "sha256:v7", "sha256:v7", "sha256:l0", "sha256:l1");
    }

    [Fact]
    public async Task BaseListIsResolvedToTheExactPlatformVariant()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        SetupArmBase(client);

        Assert.Equal((2, "registry.test/base:1", null),
            await Verify(client, Target(Annotated("registry.test/base:1", "sha256:v7")), null,
                platform: new ExplorerPlatform("linux", "arm", "v7", null)));
    }

    [Fact]
    public async Task AnnotatedBaseWithoutTheExploredPlatformWarnsInsteadOfFailing()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        SetupArmBase(client);

        (int? count, _, string? warning) = await Verify(client, Target(Annotated("registry.test/base:1")), null,
            platform: new ExplorerPlatform("linux", "arm", "v6", null));

        Assert.Null(count);
        Assert.StartsWith("Annotated base 'registry.test/base:1' could not be verified: 'registry.test/base:1' has no linux/arm/v6 platform.", warning);
    }

    private static Dictionary<string, string> Annotated(string? name, string? digest = null)
    {
        Dictionary<string, string> annotations = [];
        if (name is not null) annotations["org.opencontainers.image.base.name"] = name;
        if (digest is not null) annotations["org.opencontainers.image.base.digest"] = digest;
        return annotations;
    }

    [Fact]
    public async Task NoBaseInformationMeansNoBase()
    {
        Assert.Equal((null, null, null), await Verify(new Mock<IDockerRegistryClient>(), Target(null), null));
    }

    [Fact]
    public async Task ADigestWithoutANameWarns()
    {
        (int? count, _, string? warning) = await Verify(new Mock<IDockerRegistryClient>(), Target(Annotated(null, "sha256:base")), null);

        Assert.Null(count);
        Assert.Equal("A base digest is annotated without a base name; the base boundary cannot be verified.", warning);
    }

    [Fact]
    public async Task AnnotatedBaseIsVerifiedAgainstTheLayerPrefix()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        SetupBase(client, "base", "1", "sha256:base", "sha256:l0", "sha256:l1");

        Assert.Equal((2, "registry.test/base:1", null),
            await Verify(client, Target(Annotated("registry.test/base:1", "sha256:base")), null));
    }

    [Fact]
    public async Task ExplicitBaseNeedsNoAnnotations()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        SetupBase(client, "base", "1", "sha256:base", "sha256:l0");

        Assert.Equal((1, "registry.test/base:1", null), await Verify(client, Target(null), "registry.test/base:1"));
    }

    [Fact]
    public async Task BaseOnAnotherRegistryUsesItsOwnClient()
    {
        Mock<IDockerRegistryClient> other = new() { DefaultValue = DefaultValue.Mock };
        SetupBase(other, "base", "1", "sha256:base", "sha256:l0");
        Mock<IDockerRegistryClientFactory> factory = new();
        factory.Setup(o => o.GetClientAsync("other.test", It.IsAny<CancellationToken>())).ReturnsAsync(other.Object);

        Assert.Equal((1, "other.test/base:1", null),
            await Verify(new Mock<IDockerRegistryClient>(), Target(Annotated("other.test/base:1")), null, factory.Object));
    }

    [Fact]
    public async Task UnreachableAnnotatedBaseWarnsInsteadOfFailing()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(o => o.Manifests.GetAsync("base", "1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        (int? count, _, string? warning) = await Verify(client, Target(Annotated("registry.test/base:1")), null);

        Assert.Null(count);
        Assert.Equal("Annotated base 'registry.test/base:1' could not be verified: connection refused", warning);
    }

    [Fact]
    public async Task UnreachableExplicitBaseFails()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        client.Setup(o => o.Manifests.GetAsync("base", "1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Verify(client, Target(null), "registry.test/base:1"));
    }

    [Fact]
    public async Task ExplicitBaseMustAgreeWithTheAnnotation()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        SetupBase(client, "base", "1", "sha256:one", "sha256:l0");
        SetupBase(client, "base", "2", "sha256:two", "sha256:l0");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Verify(client, Target(Annotated("registry.test/base:1")), "registry.test/base:2"));

        Assert.Contains("disagrees with the annotation", error.Message);
    }

    [Fact]
    public async Task AnnotatedDigestMustMatchTheBase()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        SetupBase(client, "base", "1", "sha256:moved", "sha256:l0");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Verify(client, Target(Annotated("registry.test/base:1", "sha256:pinned")), null));

        Assert.Contains("does not match", error.Message);
    }

    [Fact]
    public async Task BaseWhoseLayersAreNotAPrefixFails()
    {
        Mock<IDockerRegistryClient> client = new() { DefaultValue = DefaultValue.Mock };
        SetupBase(client, "base", "1", "sha256:base", "sha256:elsewhere");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Verify(client, Target(Annotated("registry.test/base:1")), null));
    }
}

public class ExplorerComparisonTests
{
    [Fact]
    public void RequiresTheSamePlatform()
    {
        ExplorerSession baseline = Session(["sha256:a"], [Layer([File("a", 1, "a")])], []);
        ExplorerSession target = Session(["sha256:a"], [Layer([File("a", 1, "a")])], [], architecture: "arm64");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => ExplorerSession.Compare(baseline, target));

        Assert.Contains("same platform", error.Message);
    }

    [Fact]
    public void CountsEachNewLayerDownloadOnce()
    {
        LayerChanges empty = Layer([]);
        ExplorerSession baseline = Session(["sha256:a", "sha256:b"], [empty, empty], []);
        ExplorerSession target = Session(["sha256:a", "sha256:new", "sha256:new", "sha256:b"], [empty, empty, empty, empty], []);

        Assert.Equal(1000, ExplorerSession.Compare(baseline, target).AdditionalDownloadBytes);
        Assert.Equal(0, ExplorerSession.Compare(baseline, baseline).AdditionalDownloadBytes);
    }

    [Fact]
    public void ReportsAddedDeletedAndModifiedFilesButNotIdenticalOnes()
    {
        ExplorerSession baseline = Session(["sha256:a"],
            [Layer([File("same", 5, "s"), File("edited", 5, "old"), File("resized", 5, "r"), File("gone", 5, "g")])], []);
        ExplorerSession target = Session(["sha256:b"],
            [Layer([File("same", 5, "s"), File("edited", 5, "new"), File("resized", 6, "r"), File("fresh", 5, "f")])], []);

        ExplorerComparison comparison = ExplorerSession.Compare(baseline, target);

        Assert.Equal(
            [("edited", LayerChangeKind.Modified), ("fresh", LayerChangeKind.Added), ("gone", LayerChangeKind.Deleted),
                ("resized", LayerChangeKind.Modified)],
            comparison.Files.Select(f => (f.Path, f.Kind)));
        Assert.Null(comparison.Files.Single(f => f.Path == "fresh").Baseline);
        Assert.Null(comparison.Files.Single(f => f.Path == "gone").Target);
    }

    [Fact]
    public void ChangedLinkTargetsAreModifications()
    {
        ExplorerSession baseline = Session(["sha256:a"], [Layer([Link("current", "v1")])], []);
        ExplorerSession target = Session(["sha256:b"], [Layer([Link("current", "v2")])], []);

        Assert.Equal(LayerChangeKind.Modified, Assert.Single(ExplorerSession.Compare(baseline, target).Files).Kind);
    }

    [Fact]
    public void ReportsPackageVersionChanges()
    {
        ExplorerSession baseline = Session(["sha256:a"], [Layer([])], new() { ["kept"] = "1", ["bumped"] = "1", ["dropped"] = "1" });
        ExplorerSession target = Session(["sha256:b"], [Layer([])], new() { ["kept"] = "1", ["bumped"] = "2", ["new"] = "1" });

        Assert.Equal(
            [new ExplorerPackageDifference(InstalledPackageEcosystem.Npm, "bumped", "1", "2"),
                new ExplorerPackageDifference(InstalledPackageEcosystem.Npm, "dropped", "1", null),
                new ExplorerPackageDifference(InstalledPackageEcosystem.Npm, "new", null, "1")],
            ExplorerSession.Compare(baseline, target).Packages);
    }

    [Fact]
    public void SkipsEcosystemsWithoutMetadataOnEitherSide()
    {
        ExplorerSession baseline = Session(["sha256:a"], [Layer([])], new() { ["left-pad"] = "1" });
        ExplorerSession target = Session(["sha256:b"], [Layer([])], new() { ["left-pad"] = "2" }, npmAvailable: false);

        Assert.Empty(ExplorerSession.Compare(baseline, target).Packages);
        Assert.Empty(ExplorerSession.Compare(target, baseline).Packages);
    }

    [Theory]
    [InlineData("sha256:img", 3, 1, "same digest")]
    [InlineData("sha256:other", 0, 1, "different base image")]
    [InlineData("sha256:other", 0, null, null)]
    [InlineData("sha256:other", 0, 0, null)]
    [InlineData("sha256:other", 2, 1, null)]
    [InlineData(null, 0, null, null)]
    public void FlagsTagsInThePicker(string? digest, int shared, int? baseLayers, string? expected) =>
        Assert.Equal(expected, ExplorerHost.TagNote(digest, "sha256:img", shared, baseLayers));
}

// The configured viewer suspends the explorer while displaying a staged file.
public class ExplorerViewerTests
{
    [Theory]
    [InlineData("app/package.json", "package.json")]
    [InlineData("etc/.bashrc", ".bashrc")]
    [InlineData("tmp/%PATH%.txt", "_PATH_.txt")]
    [InlineData("tmp/a:b?c*d<e>f|g\"h.log", "a_b_c_d_e_f_g_h.log")]
    [InlineData("tmp/x & y ^z.sh", "x___y__z.sh")]
    [InlineData("tmp/naïve.md", "na_ve.md")]
    [InlineData("tmp/trailing...", "trailing")]
    [InlineData("tmp/..", "_")]
    [InlineData("dev/nul.txt", "_nul.txt")]
    [InlineData("dev/COM1", "_COM1")]
    public void StagedFilesUseOnlySafeCharacters(string path, string expected)
    {
        Assert.Equal(expected, ExplorerHost.StagedFileName(path));
    }

    [Fact]
    public void LongStagedNamesKeepTheirExtension()
    {
        string name = ExplorerHost.StagedFileName("tmp/" + new string('a', 300) + ".json");

        Assert.Equal(100, name.Length);
        Assert.EndsWith("a.json", name);
    }

    [Fact]
    public void DefaultTerminalViewerWaitsForConfirmationAfterItCloses()
    {
        string file = Stage("needle\n");
        (string exe, string args) = Grep("needle");
        using StringReader input = new("\n");
        using StringWriter output = new();

        Assert.Null(ExplorerApp.RunTerminalViewer(file, exe, args, true, input, output));
        Assert.Equal("\nPress Enter to return to the explorer...", output.ToString());
        Assert.False(System.IO.File.Exists(file));
    }

    [Fact]
    public void WindowedOrFailedViewerDoesNotPrompt()
    {
        (string exe, string args) = Grep("needle");
        using StringReader input = new("");
        using StringWriter output = new();

        Assert.Null(ExplorerApp.RunTerminalViewer(Stage("needle\n"), exe, args, false, input, output));
        Assert.Equal("", output.ToString());
        Assert.StartsWith("Could not run the viewer",
            ExplorerApp.RunTerminalViewer(Stage("needle\n"), "missing-dredge-test-viewer", "\"{0}\"",
                true, input, output));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void ViewerGetsTheStagedFileEvenWhenItsNameLooksLikeAVariable()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dredge-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, ExplorerHost.StagedFileName("tmp/%USERNAME%.txt"));
        System.IO.File.WriteAllText(file, "needle\n");
        (string exe, string args) = Grep("needle");

        Assert.Null(ExplorerApp.RunViewer(file, exe, args));
        Assert.False(Directory.Exists(directory));
    }

    private static string Stage(string contents)
    {
        string directory = Path.Combine(Path.GetTempPath(), "dredge viewer " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "a file.txt");
        System.IO.File.WriteAllText(file, contents);
        return file;
    }

    [Fact]
    public void SuccessfulViewerReturnsNoErrorAndRemovesTheStagedFile()
    {
        string file = Stage("needle\n");
        (string exe, string args) = Grep("needle");

        Assert.Null(ExplorerApp.RunViewer(file, exe, args));
        Assert.False(System.IO.File.Exists(file));
        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
    }

    [Fact]
    public void ViewerReadsTheFileItWasGiven()
    {
        string file = Stage("hay\n");
        (string exe, string args) = Grep("needle");

        Assert.Equal($"The viewer '{exe}' exited with code 1.", ExplorerApp.RunViewer(file, exe, args));
        Assert.False(System.IO.File.Exists(file));
    }

    [Fact]
    public void NonzeroExitBecomesANotice()
    {
        string file = Stage("x");

        string exe = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        string args = OperatingSystem.IsWindows() ? "/c exit 3" : "-c \"exit 3\"";
        Assert.Equal($"The viewer '{exe}' exited with code 3.", ExplorerApp.RunViewer(file, exe, args));
    }

    [Fact]
    public void MissingViewerBecomesANotice()
    {
        string file = Stage("x");

        const string exe = "missing-dredge-test-viewer";
        string? error = ExplorerApp.RunViewer(file, exe, "\"{0}\"");

        Assert.StartsWith($"Could not run the viewer '{exe}': ", error);
        Assert.False(System.IO.File.Exists(file));
    }

    private static (string Exe, string Args) Grep(string pattern) => OperatingSystem.IsWindows()
        ? ("findstr.exe", $"{pattern} \"{{0}}\"")
        : ("/bin/grep", $"{pattern} \"{{0}}\"");
}
