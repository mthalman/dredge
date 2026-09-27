using Terminal.Gui.Input;
using Valleysoft.DockerRegistryClient.Models.Images;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

// A small synthetic image: one base layer and three of the image's own, with a
// deleted cache file, an overwritten manifest, and leftover apt lists.
internal static class ExplorerSamples
{
    public const long Mb = 1_000_000;
    public const string Reference = "registry.test/shop/storefront:1.0";

    public static readonly string[] Digests = ["sha256:l0", "sha256:l1", "sha256:l2", "sha256:l3"];

    public static LayerChanges[] Layers() =>
    [
        Layer([File("etc/os-release", 100, "os"), File("bin/sh", 1000, "sh")]),
        Layer([File("var/lib/apt/lists/main", 1_200_000, "apt"), File("usr/bin/tool", 500, "tool")]),
        Layer([File("app/package.json", 40, "pkg1"), File("app/src/index.js", 300, "idx"),
            File("app/cache/big.bin", 2 * Mb, "big"), File("app/node_modules/left-pad/index.js", 60, "lp1")]),
        Layer([File("app/package.json", 45, "pkg2"), File("app/dist/main.js", 800, "main")],
            whiteouts: ["app/cache/big.bin"]),
    ];

    public static LayerHistory[] History() =>
    [
        new() { CreatedBy = "/bin/sh -c #(nop) ADD file:rootfs in / " },
        new() { CreatedBy = "RUN /bin/sh -c apt-get update && apt-get install -y tool # buildkit" },
        new() { CreatedBy = "COPY . /app # buildkit" },
        new() { CreatedBy = "RUN /bin/sh -c rm -rf /app/cache && npm run build # buildkit" },
    ];

    public static ExplorerImage Image(bool complete = true, IReadOnlyList<ExplorerPlatform>? platforms = null,
        string digest = "sha256:manifest")
    {
        LayerChanges[] layers = Layers();
        ExplorerImage img = new(Reference, "linux/amd64", digest, Digests, [100, 400, 900, 300],
            History(), baseLayerCount: 1, baseName: "registry.test/base:1", now: new DateTime(2026, 1, 1));
        return Load(img, layers, complete, npm: new() { ["left-pad"] = "1.0.0" });
    }

    // An arbitrary image: one history entry per layer and 100-byte downloads.
    public static ExplorerImage Custom(LayerChanges[] layers, int? baseLayerCount = null, string[]? instructions = null)
    {
        string[] digests = layers.Select((_, i) => $"sha256:c{i}").ToArray();
        LayerHistory[] history = layers.Select((_, i) => new LayerHistory
        {
            CreatedBy = instructions?[i] ?? $"RUN /bin/sh -c step {i} # buildkit"
        }).ToArray();
        ExplorerImage img = new(Reference, "linux/amd64", "sha256:custom", digests,
            layers.Select(_ => 100L).ToArray(), history, baseLayerCount: baseLayerCount,
            baseName: baseLayerCount is null ? null : "registry.test/base:1", now: new DateTime(2026, 1, 1));
        return Load(img, layers, complete: true, npm: []);
    }

    private static ExplorerImage Load(ExplorerImage img, LayerChanges[] layers, bool complete, Dictionary<string, string> npm)
    {
        string[] digests = img.LayerDigests.ToArray();
        for (int layer = 0; layer < layers.Length; layer++)
        {
            if (complete || layer < 2)
            {
                img.SetIndexed(layer, layers[layer]);
            }
        }
        if (complete)
        {
            ExplorerSession session = Session(digests, layers, npm);
            img.SetSession(session, ExplorerInsights.Build(session.Analysis, img.Instructions, img.BaseLayerCount));
        }
        else
        {
            ImageAnalysisResult analysis = ImageAnalysis.Analyze(img.IndexedPrefix()!);
            img.SetAnalysis(analysis, ExplorerInsights.Build(analysis, img.Instructions, img.BaseLayerCount, includePotential: false));
        }
        return img;
    }

    // The same base, with a rebuilt application layer.
    public static ExplorerSession Target() =>
        Session(["sha256:l0", "sha256:l1", "sha256:t2"],
        [
            Layers()[0],
            Layers()[1],
            Layer([File("app/package.json", 50, "pkg3"), File("app/src/index.js", 300, "idx"),
                File("app/node_modules/left-pad/index.js", 64, "lp2")]),
        ], new() { ["left-pad"] = "1.1.0" });

    public static ExplorerSession Session(string[] digests, IReadOnlyList<LayerChanges> layers, Dictionary<string, string> npm,
        bool npmAvailable = true, string architecture = "amd64")
    {
        ImageAnalysisResult analysis = ImageAnalysis.Analyze(layers);
        OciImageManifest manifest = new()
        {
            Config = new OciDescriptor { Digest = "sha256:config" },
            Layers = digests.Select(digest => new OciDescriptor { Digest = digest, Size = 1000 }).ToArray(),
        };
        Dictionary<InstalledPackageEcosystem, InstalledPackageEcosystemMetadata> ecosystems = Enum
            .GetValues<InstalledPackageEcosystem>()
            .ToDictionary(ecosystem => ecosystem, ecosystem => ecosystem == InstalledPackageEcosystem.Npm && npmAvailable
                ? new InstalledPackageEcosystemMetadata(InstalledPackageMetadataAvailability.Available,
                    npm.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)[pair.Value]))
                : new InstalledPackageEcosystemMetadata(InstalledPackageMetadataAvailability.Unavailable,
                    new Dictionary<string, IReadOnlyList<string>>()));
        return new ExplorerSession
        {
            Image = ImageName.Parse(Reference),
            Resolved = new ResolvedManifest(new ManifestInfo("application/vnd.oci.image.manifest.v1+json",
                "sha256:" + string.Join("", digests), manifest), manifest),
            Config = new Image { Os = "linux", Architecture = architecture },
            Files = null!,
            Analysis = analysis,
            Entries = analysis.LiveEntries.Values.Select(entry => new ImageFileSystemEntry
            {
                Path = entry.Path,
                Type = entry.Type,
                Mode = entry.Mode,
                Size = entry.Size,
                LinkTarget = entry.LinkTarget,
                IntroducedLayer = new(analysis.LiveLayers[entry.Path], digests[analysis.LiveLayers[entry.Path]]),
            }).ToArray(),
            Packages = new InstalledPackageMetadata(ecosystems),
            BaseLayerCount = 1,
        };
    }

    public static LayerChanges Layer(ScannedEntry[] entries, string[]? whiteouts = null) =>
        new(entries, whiteouts ?? [], []);

    public static ScannedEntry File(string path, long size, string hash) =>
        new(path, ImageFileType.File, 0x1A4, 0, 0, size, DateTime.UnixEpoch, null, 0, 0, 0, hash);

    public static ScannedEntry Link(string path, string target) =>
        new(path, ImageFileType.SymbolicLink, 0x1FF, 0, 0, 0, DateTime.UnixEpoch, target, 0, 0, 0, null);
}

internal sealed class FakeExplorerHost : IExplorerHost
{
    public KeyMap Keys { get; init; } = KeyMap.Default;
    public bool ClipboardEnabled { get; init; }
    public IReadOnlyList<ExplorerPlatform> Platforms { get; init; } = [];
    public ExplorerPlatform? Platform { get; init; }
    public ExplorerSession? Baseline { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = ["1.0", "2.0"];
    public Func<string, PreviewContent>? Preview { get; init; }
    public Func<string, ExplorerSession>? Target { get; init; }
    public Exception? CompareError { get; init; }
    public Func<Task<ExplorerComparison>>? CompareWork { get; init; }
    public Exception? TagsError { get; init; }

    public List<int> Prioritized { get; } = [];
    public List<int> Retried { get; } = [];
    public List<string> Previewed { get; } = [];
    public List<string> Compared { get; } = [];
    public List<string> Diffed { get; } = [];
    public List<(string Path, string Destination)> Extracted { get; } = [];
    public List<string> Clipboard { get; } = [];

    public void Prioritize(int layer) => Prioritized.Add(layer);
    public void Retry(int layer) => Retried.Add(layer);

    public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken) =>
        TagsError is null ? Task.FromResult(Tags) : Task.FromException<IReadOnlyList<string>>(TagsError);

    public Task DescribeTagAsync(TagChoice choice, CancellationToken cancellationToken)
    {
        if (choice.Tag == "broken")
        {
            throw new InvalidOperationException("manifest unknown");
        }
        choice.Shared = choice.Tag == "1.0" ? 4 : choice.Tag == "other-base" ? 0 : 2;
        choice.LayerCount = choice.Tag == "1.0" ? 4 : 3;
        choice.AdditionalDownload = choice.Tag == "2.0" ? 1_000 : 0;
        choice.Note = ExplorerHost.TagNote(choice.Tag == "1.0" ? "sha256:manifest" : "sha256:" + choice.Tag,
            "sha256:manifest", choice.Shared.Value, 1);
        return Task.CompletedTask;
    }

    public Task<ExplorerComparison> CompareAsync(string tag, Action readingPackages, CancellationToken cancellationToken)
    {
        Compared.Add(tag);
        if (CompareError is not null)
        {
            return Task.FromException<ExplorerComparison>(CompareError);
        }
        readingPackages();
        if (CompareWork is not null)
        {
            return CompareWork();
        }
        return Task.FromResult(ExplorerSession.Compare(Baseline!, Target?.Invoke(tag) ?? ExplorerSamples.Target()));
    }

    public Task<PreviewContent> PreviewAsync(string path, int layer, CancellationToken cancellationToken)
    {
        Previewed.Add(path);
        return Task.FromResult(Preview?.Invoke(path) ?? new PreviewContent(path, ExplorerHost.LanguageFor(path),
            ["{", "  \"name\": \"storefront\"", "}"], null, 30));
    }

    public Task<TextDiffContent> DiffAsync(ExplorerComparison comparison, string path, CancellationToken cancellationToken)
    {
        Diffed.Add(path);
        return Task.FromResult(new TextDiffContent(path, TextDiff.Diff(["a", "old"], ["a", "new"]), null));
    }

    public Task<PackageFilesContent> PackageFilesAsync(
        ExplorerComparison comparison, ExplorerPackageDifference package, CancellationToken cancellationToken) =>
        Task.FromResult(new PackageFilesContent(package, [("app/node_modules/left-pad/index.js", Change.Modified)], null, 1));

    public Task<string> ExtractAsync(string path, string destination, CancellationToken cancellationToken)
    {
        Extracted.Add((path, destination));
        return Task.FromResult($"Extracted /{path} to {destination}");
    }

    public Task<string> PrepareForViewerAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult("/tmp/dredge-test/" + path.Split('/')[^1]);

    public bool ClipboardWorks { get; init; } = true;
    public bool WriteClipboard(string text)
    {
        Clipboard.Add(text);
        return ClipboardWorks;
    }
}

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerWindowTests
{
    internal static ExplorerUiHarness Open(
        out FakeExplorerHost host, int width = 150, int height = 42, bool complete = true,
        KeyMap? keys = null, bool clipboard = false, IReadOnlyList<ExplorerPlatform>? platforms = null, bool clipboardWorks = true)
    {
        ExplorerImage img = ExplorerSamples.Image(complete);
        FakeExplorerHost fake = new()
        {
            Keys = keys ?? KeyMap.Default,
            ClipboardEnabled = clipboard,
            ClipboardWorks = clipboardWorks,
            Platforms = platforms ?? [],
            Platform = platforms?.FirstOrDefault(),
            Baseline = img.Session,
        };
        host = fake;
        ExplorerState state = new() { Layer = 2 };
        state.Expanded.Add("app");
        return new ExplorerUiHarness(width, height, _ => new ExplorerWindow(img, state, fake, CancellationToken.None));
    }

    // Opens any image with a caller-configured host; the host's baseline is the image's session.
    internal static ExplorerUiHarness Open(
        ExplorerImage img, ExplorerState state, Func<ExplorerSession?, FakeExplorerHost> host,
        out FakeExplorerHost created, int width = 150, int height = 42)
    {
        FakeExplorerHost fake = created = host(img.Session);
        return new ExplorerUiHarness(width, height, _ => new ExplorerWindow(img, state, fake, CancellationToken.None));
    }

    private static int RowOf(ExplorerUiHarness ui, string path) =>
        ui.Window.Presenter.IndexOf(ui.State, path);

    [Fact]
    public void KeyboardNavigatesPanesLayersAndTree()
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;
        Assert.True(s.Focus == FocusPane.Right && ui.Window.Right.HasFocus);
        Assert.True(ui.Shows("COPY . /app"), ui.Screen());

        ui.Press(Key.Tab);
        Assert.True(s.Focus == FocusPane.Layers && ui.Window.Layers.HasFocus);
        ui.Press(Key.CursorDown);
        Assert.Equal(3, s.Layer);
        ui.Press(Key.Home);
        Assert.Equal(0, s.Layer);
        ui.Press(Key.Tab);
        Assert.True(s.Focus == FocusPane.Right && ui.Window.Right.HasFocus);

        ui.Press(new Key(']'));
        ui.Press(new Key(']'));
        Assert.Equal(2, s.Layer);
        ui.Press(new Key('['));
        Assert.Equal(1, s.Layer);
        ui.Press(new Key('b'));
        Assert.Equal(1, s.Layer);
        ui.Window.Apply(new SelectLayer(2));

        s.Expanded.Add("app");
        ui.Window.Presenter.Invalidate();
        ui.Press(Key.CursorDown);
        Assert.Equal(1, s.Cursor);
        ui.Press(Key.End);
        Assert.Equal(ui.Window.Presenter.Flatten(s).Count - 1, s.Cursor);
        ui.Press(Key.Home);
        Assert.Equal(0, s.Cursor);
        ui.Press(Key.Space);
        Assert.Contains("app", s.Expanded);
        ui.Press(Key.CursorRight);
        Assert.Contains("app", s.Expanded);

        ui.Press(Key.Esc);
        Assert.False(ui.Window.StopRequested);
        ui.Press(new Key('q'));
        Assert.True(ui.Window.StopRequested);
        Assert.Equal(ExplorerExitKind.Quit, ui.Window.Exit.Kind);
    }

    [Fact]
    public void SearchFiltersAcrossLayersAndOpensHits()
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;
        int footer = ui.Height - 1;

        ui.Press(new Key('/'));
        Assert.True(s.View == RightView.Search && ui.Window.Search.HasFocus);
        ui.Type("big.bin");
        Assert.Equal("big.bin", s.SearchQuery);
        Assert.True(ui.Find("1 match", footer).Y == footer, ui.Screen());

        ui.Window.Search.Text = "BIG";
        ui.Pump();
        Assert.True(ui.Find("1 match", footer).Y == footer, ui.Screen());
        ui.Press(Key.C.WithAlt);
        Assert.True(s.SearchExactCase && ui.Window.Search.HasFocus);
        Assert.True(ui.Find("0 matches", footer).Y == footer, ui.Screen());
        ui.Press(Key.C.WithAlt);
        Assert.False(s.SearchExactCase);

        ui.Window.Search.Text = "package.json";
        ui.Pump();
        ui.Press(Key.L.WithAlt);
        Assert.True(s.SearchLayerOnly && ui.Window.Search.HasFocus);
        ui.Window.Apply(new SelectLayer(1));
        ui.Pump();
        Assert.True(ui.Find("0 matches", footer).Y == footer, ui.Screen());
        (int x, int y) = ui.Find(" Whole image ", 3);
        Assert.True(y > 0, ui.Screen());
        ui.Click(x + 2, y);
        Assert.True(!s.SearchLayerOnly && ui.Window.Search.HasFocus);

        ui.Window.Search.Text = "big.bin";
        ui.Pump();
        ui.Press(Key.D.WithAlt);
        Assert.False(s.SearchIncludeDeleted);
        // big.bin is deleted from the final image, so hiding deleted paths hides it.
        Assert.True(ui.Find("0 matches", footer).Y == footer, ui.Screen());
        Assert.True(ui.Shows("includes deleted paths"), ui.Screen());
        ui.Press(Key.D.WithAlt);
        Assert.True(ui.Find("1 match", footer).Y == footer, ui.Screen());

        ui.Press(Key.Enter);
        Assert.Equal(RightView.Files, s.View);
        Assert.Equal(2, s.Layer);
        Assert.Contains("app/cache", s.Expanded);
        Assert.Equal(RowOf(ui, "app/cache/big.bin"), s.Cursor);
        Assert.True(ui.Window.Right.HasFocus);
    }

    [Fact]
    public void MouseSelectsLayersTogglesChipsAndMode()
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;

        (int x, int y) = ui.Find("RUN rm -rf");
        Assert.True(y > 0, ui.Screen());
        ui.Click(x, y);
        Assert.Equal(3, s.Layer);
        // Layer 0 is too small for a column, so the bar starts with layer 1.
        ui.Click(3, 1);
        Assert.Equal(1, s.Layer);
        ui.Window.Apply(new SelectLayer(2));
        ui.Window.Apply(new FocusOn(FocusPane.Right));
        ui.Pump();

        (int cx, int cy) = ui.Find(" added", 3);
        Assert.True(cy > 0, ui.Screen());
        ui.Click(cx + 1, cy);
        Assert.Contains(Change.Added, s.Hidden);
        ui.Click(cx + 1, cy);
        Assert.DoesNotContain(Change.Added, s.Hidden);

        (int wx, int wy) = ui.Find("Whole filesystem", 3);
        ui.Click(wx + 2, wy);
        Assert.True(s.WholeFilesystem);
        Assert.True(ui.Shows("etc/"), ui.Screen());
        ui.Press(new Key('a'));
        Assert.False(s.WholeFilesystem);
    }

    [Fact]
    public void InsightsLeadToTheFilesBehindAFinding()
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;

        ui.Press(new Key('i'));
        Assert.Equal(RightView.Insights, s.View);
        Assert.True(ui.Shows("Files deleted after they were shipped"), ui.Screen());
        Assert.True(ui.Shows("apt package lists left in the image"), ui.Screen());
        ui.Press(Key.Enter);
        Assert.Equal(RightView.Files, s.View);
        Assert.True(s.FindingsOnly);
        Assert.Equal(3, s.Layer);
        Assert.Equal(RowOf(ui, "app/cache/big.bin"), s.Cursor);
        Assert.True(ui.Shows("deletes layer 2"), ui.Screen());

        ui.Press(Key.Esc);
        Assert.False(s.FindingsOnly);
        ui.Window.Apply(new SelectLayer(2));
        ui.Press(new Key('w'));
        List<string> shown = ui.Window.Presenter.Flatten(s).Select(row => row.Path).ToList();
        Assert.Contains("app/cache/big.bin", shown);
        Assert.DoesNotContain("app/src/index.js", shown);
        ui.Press(new Key('w'));
        Assert.False(s.FindingsOnly);
    }

    [Fact]
    public void InspectorPreviewsAndCopiesCommands()
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host);
        ExplorerState s = ui.State;

        ui.Window.Apply(new SetCursor(RowOf(ui, "app/package.json")));
        ui.Press(Key.Enter);
        Assert.Equal(RightView.Inspector, s.View);
        ui.Until(() => s.Preview is not null, "the preview");
        Assert.Equal(["app/package.json"], host.Previewed);
        Assert.True(ui.Shows("\"storefront\""), ui.Screen());

        ui.Press(new Key('y'));
        Assert.Equal("$ dredge image cat registry.test/shop/storefront:1.0 /app/package.json", s.Notice);
        Assert.Empty(host.Clipboard);
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Inspector, s.View);
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Files, s.View);
    }

    [Fact]
    public void ClipboardReceivesCopiedCommands()
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host, clipboard: true);
        ui.Window.Apply(new SetCursor(RowOf(ui, "app")));
        ui.Window.Apply(new CopyCommand());
        string copied = Assert.Single(host.Clipboard);
        Assert.StartsWith("dredge image ls registry.test/shop/storefront:1.0 /app", copied);
        Assert.StartsWith("Copied: ", ui.State.Notice);
    }

    [Theory]
    [InlineData(true, "Copy as dredge command")]
    [InlineData(false, "Show as dredge command")]
    public void KeysScreenSaysWhetherYCopies(bool clipboard, string label)
    {
        using ExplorerUiHarness ui = Open(out _, clipboard: clipboard);
        ui.Press(new Key('?'));
        Assert.True(ui.Shows(label), ui.Screen());
    }

    [Fact]
    public void AClipboardFailureShowsTheCommandInstead()
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host, clipboard: true, clipboardWorks: false);
        ui.Window.Apply(new SetCursor(RowOf(ui, "app")));
        ui.Window.Apply(new CopyCommand());
        Assert.Single(host.Clipboard);
        Assert.StartsWith("Couldn't reach the clipboard. $ dredge image ls registry.test/shop/storefront:1.0 /app", ui.State.Notice);
    }

    [Fact]
    public void ExtractPromptsForADestination()
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host);
        ExplorerState s = ui.State;

        ui.Window.Apply(new SetCursor(RowOf(ui, "app/cache")));
        ui.Press(new Key('x'));
        Assert.Contains("not in the final image", s.Notice);

        ui.Window.Apply(new SelectLayer(3));
        s.Expanded.Add("app");
        ui.Window.Presenter.Invalidate();
        ui.Window.Apply(new SetCursor(RowOf(ui, "app/package.json")));
        ui.Press(new Key('x'));
        Assert.True(ui.Window.ExtractField.Visible && ui.Window.ExtractField.HasFocus);
        Assert.Equal("./package.json", ui.Window.ExtractField.Text);
        Assert.True(ui.Shows("Extract to"), ui.Screen());
        ui.Press(Key.Enter);
        ui.Until(() => s.Notice?.StartsWith("Extracted") == true, "the extraction");
        Assert.Equal([("app/package.json", "./package.json")], host.Extracted);

        ui.Press(new Key('x'));
        ui.Press(Key.Esc);
        Assert.False(ui.Window.ExtractField.Visible);
        Assert.Single(host.Extracted);
    }

    [Fact]
    public void ViewerExitsWithTheStagedFile()
    {
        using ExplorerUiHarness ui = Open(out _);
        ui.Window.Apply(new SetCursor(RowOf(ui, "app")));
        ui.Window.Apply(new OpenInViewer());
        Assert.Equal("Select a file to open in the viewer.", ui.State.Notice);

        ui.State.Expanded.Add("app/src");
        ui.Window.Presenter.Invalidate();
        ui.Window.Apply(new SetCursor(RowOf(ui, "app/src/index.js")));
        ui.Window.Apply(new OpenInViewer());
        ui.Until(() => ui.Window.StopRequested, "the viewer exit");
        Assert.Equal(ExplorerExitKind.Viewer, ui.Window.Exit.Kind);
        Assert.Equal("/tmp/dredge-test/index.js", ui.Window.Exit.ViewerFile);

        // The explorer reopens with this state after the viewer, so focus moves
        // while the screen closes must not leak into it.
        Assert.Equal(FocusPane.Right, ui.State.Focus);
        ui.Window.Layers.SetFocus();
        Assert.Equal(FocusPane.Right, ui.State.Focus);
    }

    [Fact]
    public void WindowedViewerOpensWithoutStoppingTheExplorer()
    {
        ExplorerImage img = ExplorerSamples.Image();
        ExplorerState state = new() { Layer = 2, Focus = FocusPane.Right };
        state.Expanded.Add("app");
        FakeExplorerHost host = new() { Baseline = img.Session };
        List<string> opened = [];
        using ExplorerUiHarness ui = new(150, 42, _ =>
            new ExplorerWindow(img, state, host, CancellationToken.None, opened.Add));
        state.Expanded.Add("app/src");
        ui.Window.Presenter.Invalidate();
        ui.Window.Apply(new SetCursor(ui.Window.Presenter.IndexOf(state, "app/src/index.js")));

        ui.Window.Apply(new OpenInViewer());
        ui.Until(() => opened.Count == 1, "the windowed viewer launch");

        Assert.Equal(["/tmp/dredge-test/index.js"], opened);
        Assert.False(ui.Window.StopRequested);
        Assert.Equal(ExplorerExitKind.Quit, ui.Window.Exit.Kind);
        Assert.True(ui.Window.Right.HasFocus);
        ui.Window.Apply(new SelectLayer(1));
        Assert.Equal(1, state.Layer);
    }

    [Fact]
    public void LoadingImageRetriesFailedLayersAndDefersWholeImageActions()
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host, complete: false);
        ExplorerImage img = ui.Window.Presenter.Image;
        img.States[3] = ExplorerLayerState.Failed;
        img.Errors[3] = "connection reset";
        ui.Window.ImageChanged();

        ui.Press(new Key('w'));
        Assert.Equal("Findings appear once every layer is indexed.", ui.State.Notice);
        ui.Press(new Key('c'));
        Assert.Equal("Compare works once every layer is indexed.", ui.State.Notice);

        ui.Press(new Key('r'));
        Assert.Empty(host.Retried);
        ui.Window.Apply(new SelectLayer(3));
        ui.Pump();
        Assert.Contains(3, host.Prioritized);
        Assert.True(ui.Shows("This layer failed to index."), ui.Screen());
        ui.Press(new Key('r'));
        Assert.Equal([3], host.Retried);
        Assert.Equal(ExplorerLayerState.Waiting, img.States[3]);
    }

    [Fact]
    public void FailedSessionExplainsWhyInsightsAreUnavailable()
    {
        using ExplorerUiHarness ui = Open(out _, complete: false);
        ExplorerImage img = ui.Window.Presenter.Image;
        img.SessionError = "apk installed database is unreadable.";
        ui.Window.ImageChanged();
        ui.Window.Apply(new SelectLayer(0));
        ui.Pump();
        Assert.True(ui.Shows("Insights are unavailable; press"), ui.Screen());

        ui.Press(new Key('i'));
        Assert.True(ui.Shows("Insights are unavailable."), ui.Screen());
        Assert.True(ui.Shows("apk installed database is unreadable."), ui.Screen());
        Assert.False(ui.Shows("reclaimable"), ui.Screen());
        Assert.Empty(ui.Window.Presenter.VisibleFindings(ui.State));
    }

    [Fact]
    public void TooSmallTerminalOnlyAcceptsQuit()
    {
        using ExplorerUiHarness ui = Open(out _, width: 70, height: 20);
        Assert.True(ui.Shows("needs at least"), ui.Screen());
        Assert.True(ui.Shows("dredge image ls registry.test/shop/storefront:1.0 --recursive"), ui.Screen());
        ui.Press(Key.CursorDown);
        ui.Press(new Key('i'));
        Assert.Equal(RightView.Files, ui.State.View);
        Assert.Equal(2, ui.State.Layer);
        ui.Press(new Key('q'));
        Assert.True(ui.Window.StopRequested);
    }

    [Fact]
    public void NarrowTerminalStacksLayersAboveFiles()
    {
        using ExplorerUiHarness ui = Open(out _, width: 100, height: 40);
        Assert.Equal(ui.Window.Layers.Frame.Width, ui.Window.Right.Frame.Width);
        Assert.True(ui.Window.Right.Frame.Y > ui.Window.Layers.Frame.Y);
        Assert.True(ui.Shows("COPY . /app"), ui.Screen());
        Assert.True(ui.Shows("package.json"), ui.Screen());
    }

    [Fact]
    public void ComparesWithAnotherTag()
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host);
        ExplorerState s = ui.State;

        bool picked = ui.AnswerDialog(() => ui.Press(new Key('c')), new Key('2'), new Key('.'), new Key('0'), Key.Enter);
        Assert.True(picked);
        ui.Until(() => s.Compare is not null, "the comparison");
        Assert.Equal(["2.0"], host.Compared);
        CompareState c = s.Compare!;
        Assert.Equal(("1.0", "2.0"), (c.BaselineLabel, c.TargetLabel));
        Assert.Equal(2, c.Layer);
        Assert.True(ui.Shows("left-pad"), ui.Screen());

        ui.Press(new Key(']'));
        Assert.Equal(3, c.Layer);
        ui.Press(new Key(']'));
        Assert.Equal("No later layer differs.", s.Notice);

        c.Expanded.Add("file:app");
        ui.Window.Apply(new Redraw());
        List<CompareRow> rows = new CompareView(ui.Window.Presenter, c).Rows();
        ui.Window.Apply(new SetCursor(rows.FindIndex(row => row.Path == "app/package.json")));
        ui.Press(Key.Enter);
        ui.Until(() => c.Diff is not null, "the diff");
        Assert.Equal(["app/package.json"], host.Diffed);
        Assert.True(ui.Shows("new"), ui.Screen());
        ui.Press(Key.Esc);
        Assert.Null(c.Diff);

        ui.Window.Apply(new SetCursor(rows.FindIndex(row => row.Package?.Name == "left-pad")));
        ui.Press(Key.Enter);
        ui.Until(() => c.PackageFiles?.Files is not null, "the package files");
        ui.Press(Key.Esc);

        ui.Press(new Key('s'));
        Assert.Equal(("2.0", "1.0"), (s.Compare!.BaselineLabel, s.Compare.TargetLabel));
        ui.Press(new Key('y'));
        Assert.Equal("$ dredge image compare files registry.test/shop/storefront:2.0 registry.test/shop/storefront:1.0", s.Notice);
        ui.Press(new Key('w'));
        Assert.False(s.FindingsOnly);
        Assert.StartsWith("$ dredge image compare files", s.Notice);

        ui.Press(Key.Esc);
        Assert.NotNull(s.Compare);
        ui.Press(Key.Esc);
        Assert.Null(s.Compare);
        Assert.False(ui.Window.StopRequested);
    }

    [Fact]
    public void PlatformPickerRestartsOnAnotherPlatform()
    {
        using (ExplorerUiHarness single = Open(out _))
        {
            Assert.DoesNotContain(single.Window.Presenter.Hints(single.State), h => h.Cmd is PickPlatform);
            single.Press(new Key('p'));
            Assert.Null(single.State.Notice);
        }

        ExplorerPlatform amd = new("linux", "amd64", null, null), arm = new("linux", "arm64", "v8", null);
        using ExplorerUiHarness ui = Open(out _, platforms: [amd, arm]);
        Assert.Contains(ui.Window.Presenter.Hints(ui.State), h => h.Cmd is PickPlatform);
        bool picked = ui.AnswerDialog(() => ui.Press(new Key('p')), Key.CursorDown, Key.Enter);
        Assert.True(picked);
        Assert.True(ui.Window.StopRequested);
        Assert.Equal(ExplorerExitKind.Platform, ui.Window.Exit.Kind);
        Assert.Equal(arm, ui.Window.Exit.Platform);
    }

    [Fact]
    public void RemappedKeysDriveActionsAndHelp()
    {
        KeyMap keys = KeyMap.FromSettings(new ExploreKeysSettings { Quit = "Q", Insights = "n" });
        using ExplorerUiHarness ui = Open(out _, keys: keys);
        ui.Press(new Key('?'));
        Assert.Equal(RightView.Keys, ui.State.View);
        Assert.True(ui.Shows(" Q "), ui.Screen());
        ui.Press(Key.Esc);

        ui.Press(new Key('i'));
        Assert.Equal(RightView.Files, ui.State.View);
        ui.Press(new Key('n'));
        Assert.Equal(RightView.Insights, ui.State.View);
        ui.Press(new Key('q'));
        Assert.False(ui.Window.StopRequested);
        ui.Press(new Key('Q'));
        Assert.True(ui.Window.StopRequested);
    }
}
