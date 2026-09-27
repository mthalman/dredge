using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Valleysoft.DockerRegistryClient;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

// Runs the real explorer against a live registry: layers are downloaded and
// indexed, analyzed, and then driven through the window with injected input.
[Trait("Category", "Integration")]
[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerIntegrationTests
{
    private readonly RegistryFixture fixture;

    public ExplorerIntegrationTests(RegistryFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task ExploresInspectsAndComparesALiveImage()
    {
        await fixture.EnsureInitializedAsync();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repository = fixture.GetRepositoryName(nameof(ExploresInspectsAndComparesALiveImage));
        byte[] cache = new byte[1_500_000];
        new Random(7).NextBytes(cache);
        cache[0] = 0;

        LayerSeed os = await fixture.UploadLayerAsync(repository,
            LayerEntry.Directory("etc"),
            LayerEntry.File("etc/os-release", "ID=test\nVERSION_ID=1\n"));
        LayerSeed app1 = await fixture.UploadLayerAsync(repository,
            LayerEntry.Directory("app"),
            LayerEntry.File("app/package.json", "{\n  \"name\": \"demo\",\n  \"version\": \"1.0.0\"\n}\n"),
            LayerEntry.File("app/config.json", "{\n  \"level\": \"info\"\n}\n"),
            LayerEntry.Directory("app/node_modules"),
            LayerEntry.Directory("app/node_modules/left-pad"),
            LayerEntry.File("app/node_modules/left-pad/package.json", "{ \"name\": \"left-pad\", \"version\": \"1.0.0\" }"),
            LayerEntry.File("app/node_modules/left-pad/index.js", "module.exports = 1;\n"),
            LayerEntry.Directory("app/cache"),
            LayerEntry.File("app/cache/big.bin", cache));
        LayerSeed cleanup = await fixture.UploadLayerAsync(repository,
            LayerEntry.Directory("app"),
            LayerEntry.Directory("app/cache"),
            LayerEntry.File("app/cache/.wh.big.bin", ""),
            LayerEntry.File("app/config.json", "{\n  \"level\": \"debug\"\n}\n"));
        LayerSeed app2 = await fixture.UploadLayerAsync(repository,
            LayerEntry.Directory("app"),
            LayerEntry.File("app/package.json", "{\n  \"name\": \"demo\",\n  \"version\": \"2.0.0\"\n}\n"),
            LayerEntry.File("app/config.json", "{\n  \"level\": \"info\"\n}\n"),
            LayerEntry.Directory("app/node_modules"),
            LayerEntry.Directory("app/node_modules/left-pad"),
            LayerEntry.File("app/node_modules/left-pad/package.json", "{ \"name\": \"left-pad\", \"version\": \"1.1.0\" }"),
            LayerEntry.File("app/node_modules/left-pad/index.js", "module.exports = 2;\n"));
        object[] history1 =
        [
            new { created_by = "/bin/sh -c #(nop) ADD file:abc in / " },
            new { created_by = "COPY . /app # buildkit" },
            new { created_by = "RUN /bin/sh -c rm /app/cache/big.bin # buildkit" },
        ];
        object[] history2 =
        [
            new { created_by = "/bin/sh -c #(nop) ADD file:abc in / " },
            new { created_by = "COPY . /app # buildkit" },
        ];
        await fixture.PutImageAsync(repository, "1.0", [os, app1, cleanup], history: history1);
        await fixture.PutImageAsync(repository, "2.0", [os, app2], history: history2);

        IDockerRegistryClientFactory factory = fixture.CreateClientFactory();
        ImageName image = ImageName.Parse($"{fixture.Registry}/{repository}:1.0");
        using IDockerRegistryClient client = await factory.GetClientAsync(image.Registry, ct);
        await using LayerCacheTestContext cacheContext = new();
        ExplorerSource source = await ExplorerSource.OpenAsync(
            client, factory, image, new PlatformOptionsBase(), null, ct, settingsStore: new EmptySettingsStore());
        Assert.Equal(3, source.LayerCount);

        ExplorerOptions options = new(Layer: 1, Compare: null, Mouse: true, Clipboard: ClipboardMode.Off, Keys: KeyMap.Default,
            ViewerExePath: "less", ViewerArgs: "\"{0}\"");
        await using ExplorerApp explorer = new(client, factory, source, cacheContext.Store, options, ct);
        ExplorerState state = explorer.Start();
        Assert.Equal(FocusPane.Layers, state.Focus);
        using ExplorerUiHarness ui = new(150, 42, app => explorer.Attach(app, state));
        ExplorerImage img = explorer.Image;
        try
        {
            Assert.True(ui.Window.Layers.HasFocus);
            ui.Until(() => img.Complete || img.SessionError is not null, "the session to load");
            Assert.Null(img.SessionError);
            Assert.Equal(1, ui.State.Layer);
            Assert.True(ui.Shows("COPY . /app"), ui.Screen());
            Assert.Throws<InvalidOperationException>(() => explorer.Host.Session!.Packages);
            img.States[1] = ExplorerLayerState.Indexing;
            img.Progress[1] = 1;
            ui.Until(() => img.States[1] == ExplorerLayerState.Ready,
                "the indexed layer to recover from a missed UI update");
            Assert.Equal(img.LayerCount, img.ReadyCount);

            // The deleted cache file and the replaced config are hidden bytes.
            Assert.Equal(cache.Length + "{\n  \"level\": \"info\"\n}\n".Length, img.TotalReclaimable);
            Assert.Contains(img.Findings, finding => finding.Kind == ExplorerFindingKind.Deleted && finding.Bytes == cache.Length);
            ui.Press(new Key('i'));
            Assert.Equal(RightView.Insights, ui.State.View);
            ui.Press(Key.Esc);
            Assert.Equal(RightView.Files, ui.State.View);

            // Search every layer, then open the hit in the tree.
            ui.Press(new Key('/'));
            Assert.True(ui.Window.Search.HasFocus);
            ui.Type("config.json");
            ui.Until(() => ui.Shows("1 match "), "search matches");
            Assert.True(ui.Shows("hidden by layer 2"), ui.Screen());
            ui.Press(Key.Esc);

            // Inspect a file: history and a highlighted preview of its final version.
            ui.Window.Apply(new SelectLayer(2));
            ui.State.Expanded.Add("app");
            ui.Window.Presenter.Invalidate();
            ui.Window.Apply(new SetCursor(ui.Window.Presenter.IndexOf(ui.State, "app/config.json")));
            ui.Press(Key.Enter);
            Assert.Equal(RightView.Inspector, ui.State.View);
            ui.Until(() => ui.State.Preview?.Path == "app/config.json", "the preview");
            Assert.Contains(ui.State.Preview!.Lines!, line => line.Contains("debug", StringComparison.Ordinal));
            Assert.True(ui.Shows("\"debug\""), ui.Screen());

            ui.Press(new Key('y'));
            Assert.StartsWith("$ dredge image cat", ui.State.Notice);
            Assert.Contains("/app/config.json", ui.State.Notice);
            ui.Press(Key.Esc);

            // Compare through the tag picker.
            bool picked = ui.AnswerDialog(() => ui.Press(new Key('c')),
                new Key('2'), new Key('.'), new Key('0'), Key.Enter);
            Assert.True(picked, "the tag picker opened");
            ui.Until(() => ui.State.Compare is not null, "the comparison");
            Assert.Null(ui.State.Notice);
            ExplorerComparison comparison = ui.State.Compare!.Comparison;
            Assert.Same(explorer.Host.Session!.Packages, comparison.Baseline.Packages);
            Assert.Contains(comparison.Packages, package =>
                package.Name == "left-pad" && package.BaselineVersion == "1.0.0" && package.TargetVersion == "1.1.0");
            Assert.Contains(comparison.Files, file => file.Path == "app/package.json");
            Assert.True(ui.Shows("left-pad"), ui.Screen());

            TextDiffContent diff = ui.Wait(() => explorer.Host.DiffAsync(comparison, "app/package.json", ct), "the diff");
            Assert.NotNull(diff.Lines);
            Assert.Contains(diff.Lines!, line => line.Op == DiffOp.Insert && line.Text.Contains("2.0.0", StringComparison.Ordinal));

            ExplorerPackageDifference leftPad = comparison.Packages.Single(package => package.Name == "left-pad");
            PackageFilesContent files = ui.Wait(() => explorer.Host.PackageFilesAsync(comparison, leftPad, ct), "the package files");
            Assert.Contains(files.Files!, file => file.Path == "app/node_modules/left-pad/index.js" && file.Change == Change.Modified);

            ui.Press(Key.Esc);
            Assert.Null(ui.State.Compare);

            // Extract and viewer staging read from the live image.
            string output = Path.Combine(cacheContext.Root, "out", "config.json");
            string message = ui.Wait(() => explorer.Host.ExtractAsync("app/config.json", output, ct), "the extraction");
            Assert.Contains("Extracted", message);
            Assert.Contains("debug", File.ReadAllText(output));
            string viewed = ui.Wait(() => explorer.Host.PrepareForViewerAsync("app/package.json", ct), "the viewer file");
            Assert.Contains("1.0.0", File.ReadAllText(viewed));
            Directory.Delete(Path.GetDirectoryName(viewed)!, recursive: true);

            PreviewContent binary = ui.Wait(() => explorer.Host.PreviewAsync("etc/os-release", 0, ct), "the preview");
            Assert.Equal(["ID=test", "VERSION_ID=1"], binary.Lines);

            ui.Press(new Key('q'));
            Assert.Equal(ExplorerExitKind.Quit, ui.Window.Exit.Kind);
        }
        finally
        {
            explorer.Detach();
        }
    }

    [Fact]
    public async Task CompareOptionStartsTheComparisonOnceLoadedAndLargePreviewsAreCut()
    {
        await fixture.EnsureInitializedAsync();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repository = fixture.GetRepositoryName(nameof(CompareOptionStartsTheComparisonOnceLoadedAndLargePreviewsAreCut));
        string large = string.Concat(Enumerable.Range(0, 40_000).Select(i => $"line {i}\n"));
        LayerSeed os = await fixture.UploadLayerAsync(repository,
            LayerEntry.Directory("etc"),
            LayerEntry.File("etc/os-release", "ID=test\n"));
        LayerSeed v1 = await fixture.UploadLayerAsync(repository,
            LayerEntry.Directory("app"),
            LayerEntry.File("app/large.txt", large),
            LayerEntry.File("app/version", "1\n"));
        LayerSeed v2 = await fixture.UploadLayerAsync(repository,
            LayerEntry.Directory("app"),
            LayerEntry.File("app/version", "2\n"));
        object[] history = [new { created_by = "ADD os" }, new { created_by = "COPY . /app" }];
        await fixture.PutImageAsync(repository, "1.0", [os, v1], history: history);
        await fixture.PutImageAsync(repository, "2.0", [os, v2], history: history);

        IDockerRegistryClientFactory factory = fixture.CreateClientFactory();
        ImageName image = ImageName.Parse($"{fixture.Registry}/{repository}:1.0");
        using IDockerRegistryClient client = await factory.GetClientAsync(image.Registry, ct);
        await using LayerCacheTestContext cacheContext = new();
        ExplorerSource source = await ExplorerSource.OpenAsync(
            client, factory, image, new PlatformOptionsBase(), null, ct, settingsStore: new EmptySettingsStore());

        ExplorerOptions options = new(Layer: null, Compare: "2.0", Mouse: true, Clipboard: ClipboardMode.Off, Keys: KeyMap.Default,
            ViewerExePath: "less", ViewerArgs: "\"{0}\"", Notice: "Saved settings were ignored.");
        await using ExplorerApp explorer = new(client, factory, source, cacheContext.Store, options, ct);
        ExplorerState state = explorer.Start();
        Assert.Equal(1, state.Layer);
        Assert.True(state.NoticeIsError);
        using ExplorerUiHarness ui = new(150, 42, app => explorer.Attach(app, state));
        try
        {
            Assert.True(ui.Shows("Saved settings were ignored."), ui.Screen());
            ui.Until(() => ui.State.Compare is { Busy: false }, "the comparison");
            Assert.Contains(ui.State.Compare!.Comparison.Files, file => file.Path == "app/version");
            Assert.True(ui.Shows("1.0 → 2.0"), ui.Screen());
            ui.Press(Key.Esc);
            Assert.Null(ui.State.Compare);

            PreviewContent preview = ui.Wait(() => explorer.Host.PreviewAsync("app/large.txt", 1, ct), "the preview");
            Assert.Equal("Showing the first 256 KB.", preview.Message);
            Assert.Equal("line 0", preview.Lines![0]);
            Assert.True(preview.Lines.Count < 40_000);
        }
        finally
        {
            explorer.Detach();
        }
    }

    private sealed class EmptySettingsStore : IAppSettingsStore
    {
        public string SettingsPath => "";
        public AppSettings Load() => new();
    }
}
