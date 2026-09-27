using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using static Valleysoft.Dredge.Tests.ExplorerSamples;

namespace Valleysoft.Dredge.Tests;

// One test per feature of the explorer's tour, asserting what a person sees on
// screen as well as the state behind it.
[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerFeatureTests
{
    private static ExplorerUiHarness Open(out FakeExplorerHost host, int width = 150, int height = 42, bool complete = true) =>
        ExplorerWindowTests.Open(out host, width, height, complete);

    private static ExplorerUiHarness OpenCustom(ExplorerImage img, out FakeExplorerHost host, int layer = 0,
        Func<ExplorerSession?, FakeExplorerHost>? create = null, int width = 150, int height = 42)
    {
        ExplorerState state = new() { Layer = layer };
        return ExplorerWindowTests.Open(img, state, create ?? (session => new FakeExplorerHost { Baseline = session }),
            out host, width, height);
    }

    private static int RowOf(ExplorerUiHarness ui, string path) => ui.Window.Presenter.IndexOf(ui.State, path);

    private static void AssertShows(ExplorerUiHarness ui, params string[] texts)
    {
        foreach (string text in texts)
        {
            Assert.True(ui.Shows(text), $"Expected '{text}' on screen:{Environment.NewLine}{ui.Screen()}");
        }
    }

    private static void AssertHides(ExplorerUiHarness ui, params string[] texts)
    {
        foreach (string text in texts)
        {
            Assert.False(ui.Shows(text), $"Did not expect '{text}' on screen:{Environment.NewLine}{ui.Screen()}");
        }
    }

    private static string Footer(ExplorerUiHarness ui) => ui.Row(ui.Height - 1);

    // ───────────────────────────── tour 1: layers and files ─────────────────────────────

    [Fact]
    public void OrderedBaseImagesLabelTheirOwnLayerRanges()
    {
        ExplorerImage img = new(Reference, "linux/amd64", "sha256:app",
            ["sha256:0", "sha256:1", "sha256:2", "sha256:3"],
            [100, 100, 100, 100], null, 3, "base:new",
            baseImages: [new("base:old", 1), new("base:new", 3)]);
        ExplorerPresenter presenter = new(img, 150, 42);
        ExplorerState state = new() { Focus = FocusPane.Layers };

        Assert.Equal("base:old", img.BaseImageAt(0));
        Assert.Equal("base:new", img.BaseImageAt(1));
        Assert.Equal("base:new", img.BaseImageAt(2));
        Assert.Null(img.BaseImageAt(3));
        string[] layerLines = presenter.LayersPane(state).Lines.Select(line => line.ToString()).ToArray();
        Assert.Contains(layerLines, line => line.Contains("── base:old"));
        Assert.Contains(layerLines, line => line.Contains("── base:new"));
        Assert.Contains(layerLines, line => line.Contains("── " + img.Reference));
        string brackets = presenter.Header(state)[2].ToString();
        Assert.Contains("base:old", brackets);
        Assert.Contains("base:new", brackets);
        Assert.Contains(img.RepoName, brackets);
        presenter.Width = 100;
        state.Layer = 1;
        Assert.Equal("base:new", presenter.LayersPane(state).Subtitle);
        state.Layer = 0;
        Assert.Equal("base:old", presenter.LayersPane(state).Subtitle);
        state.Layer = 3;
        Assert.Equal(img.Reference, presenter.LayersPane(state).Subtitle);
    }

    [Fact]
    public void BaseImageGroupsUseShortUnambiguousRepositoryNames()
    {
        ExplorerImage img = new("mcr.microsoft.com/dotnet/sdk:10.0", "linux/amd64", "sha256:app",
            ["sha256:0", "sha256:1", "sha256:2", "sha256:3", "sha256:4"],
            [100, 100, 100, 100, 100], null, 4, "mcr.microsoft.com/dotnet/aspnet:10.0",
            baseImages:
            [
                new("ubuntu.azurecr.io/ubuntu:noble", 1),
                new("mcr.microsoft.com/dotnet/runtime-deps:10.0", 2),
                new("mcr.microsoft.com/dotnet/runtime:10.0", 3),
                new("mcr.microsoft.com/dotnet/aspnet:10.0", 4)
            ]);
        ExplorerPresenter presenter = new(img, 150, 42);
        ExplorerState state = new() { Focus = FocusPane.Layers };

        Assert.Equal(["ubuntu.azurecr.io/ubuntu:noble",
            "mcr.microsoft.com/dotnet/runtime-deps:10.0",
            "mcr.microsoft.com/dotnet/runtime:10.0",
            "mcr.microsoft.com/dotnet/aspnet:10.0",
            "mcr.microsoft.com/dotnet/sdk:10.0"],
            img.LayerIndexes.Select(img.GroupAt));
        string[] rows = presenter.LayersPane(state).Lines.Select(line => line.ToString()).ToArray();
        foreach (string label in img.LayerIndexes.Select(img.GroupAt))
        {
            Assert.Contains(rows, row => row.Contains($"── {label} "));
        }
        string brackets = presenter.Header(state)[2].ToString();
        Assert.Contains("ubuntu", brackets);
        Assert.DoesNotContain("ubuntu.azurecr.io", brackets);

        presenter.Width = 100;
        state.Layer = 2;
        Assert.Equal("mcr.microsoft.com/dotnet/runtime:10.0", presenter.LayersPane(state).Subtitle);
        Assert.Equal("mcr.microsoft.com/dotnet/runtime:10.0", img.BaseImageAt(2));
    }

    [Fact]
    public void DuplicateRepositoryNamesKeepFullReferencesToDistinguishGroups()
    {
        ExplorerImage img = new("registry.test/shop/app:1", "linux/amd64", "sha256:app",
            ["sha256:0", "sha256:1", "sha256:2"], [100, 100, 100], null, 2,
            "second.test/base:2", baseImages: [new("first.test/base:1", 1), new("second.test/base:2", 2)]);

        Assert.Equal("first.test/base:1", img.GroupAt(0));
        Assert.Equal("second.test/base:2", img.GroupAt(1));
        Assert.Equal("registry.test/shop/app:1", img.GroupAt(2));
    }

    [Fact]
    public void HeaderSummarizesTheImage()
    {
        using ExplorerUiHarness ui = Open(out _);

        string header = ui.Row(0);
        Assert.Contains("registry.test/shop/storefront:1.0", header);
        Assert.Contains("linux/amd64", header);
        Assert.Contains("sha256:manifest", header);
        Assert.Contains("3.2 MB file payload", header);
        Assert.Contains("1.7 KB download", header);
        Assert.Contains("37% efficient", header);
        // The strip under the core bar names the base and the user's layers.
        Assert.Contains("storefront", ui.Row(2));
    }

    [Fact]
    public void HeaderShowsTheFullImageDigest()
    {
        string digest = "sha256:" + new string('a', 64);
        using ExplorerUiHarness ui = OpenCustom(ExplorerSamples.Image(digest: digest), out _, width: 220);

        Assert.Contains(digest, ui.Row(0));
    }

    [Fact]
    public void LayerListGroupsBaseAndUserLayersWithSizes()
    {
        using ExplorerUiHarness ui = Open(out _);

        AssertShows(ui, "Layers  4 with files", "── registry.test/base:1", "── registry.test/shop/storefront:1.0");
        string selected = ui.Row(ui.Find("COPY . /app").Y);
        Assert.Contains("▌2 ", selected);
        Assert.Contains("2.0 MB", selected);
        string first = ui.Row(ui.Find("ADD file:rootfs /").Y);
        Assert.Contains("0 ", first);
        Assert.Contains("1.1 KB", first);
        // Build-step noise is stripped from instructions.
        AssertHides(ui, "# buildkit", "/bin/sh -c");
    }

    [Fact]
    public void LayerDetailsExplainTheSelectedLayer()
    {
        using ExplorerUiHarness ui = Open(out _);

        AssertShows(ui, "Layer 2 ", "2.0 MB file payload   900 B download", "+ 4 added", "sha256:l2",
            "▲ 2 findings involve this layer, 2.0 MB", "Files deleted after they were shipped, 2.0 MB", "to see insights");

        ui.Window.Apply(new SelectLayer(0));
        ui.Pump();
        AssertShows(ui, "Layer 0 ", "sha256:l0", "+ 2 added");
        AssertHides(ui, "findings involve this layer");
    }

    [Fact]
    public void TreeShowsChangeGlyphsFoldedCountsFindingsAndTheSelection()
    {
        using ExplorerUiHarness ui = Open(out _);

        AssertShows(ui, "Changes in layer 2", "This layer", "Whole filesystem", "2.0 MB in 4 paths",
            "+ 4 added   ~ 0 modified   = 0 identical   − 0 deleted", "mode", "uid:gid",
            "▾ app/", "▸ cache/  1 file ", "▸ src/  1 file ", "-rw-r--r--", "0:0", "▲ hidden by layer 3");
        AssertHides(ui, "1 files");
        // The pinned row describes the selection and what Enter does.
        AssertShows(ui, "/app/ ", "2.0 MB in 4 files", "Enter  fold");

        ui.Window.Apply(new SetCursor(RowOf(ui, "app/cache")));
        ui.Pump();
        AssertShows(ui, "/app/cache/ ", "2.0 MB in 1 file ", "Enter  unfold");

        ui.Window.Apply(new SetCursor(RowOf(ui, "app/package.json")));
        ui.Pump();
        AssertShows(ui, "/app/package.json", "layers 2+ 3~", "Enter  inspect");

        ui.Window.Apply(new SelectLayer(3));
        ui.State.Expanded.Add("app/cache");
        ui.State.Expanded.Add("app");
        ui.Window.Presenter.Invalidate();
        ui.Window.Apply(new Redraw());
        ui.Pump();
        AssertShows(ui, "~ 1 modified", "− 1 deleted", "big.bin");
    }

    [Fact]
    public void ArrowsFoldTheTreeAndJumpToParents()
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;
        s.Expanded.Add("app/src");
        ui.Window.Presenter.Invalidate();

        ui.Window.Apply(new SetCursor(RowOf(ui, "app/src/index.js")));
        ui.Press(Key.CursorLeft);
        Assert.Equal(RowOf(ui, "app/src"), s.Cursor);
        ui.Press(Key.CursorLeft);
        Assert.DoesNotContain("app/src", s.Expanded);
        ui.Press(Key.CursorRight);
        Assert.Contains("app/src", s.Expanded);
        ui.Press(Key.Enter);
        Assert.DoesNotContain("app/src", s.Expanded);
        ui.Press(Key.Enter);
        Assert.Contains("app/src", s.Expanded);

        Assert.DoesNotContain(ui.Window.Presenter.Hints(s), hint => hint.Key == "Space");
    }

    [Fact]
    public void PagingMovesTheCursorByAScreen()
    {
        LayerChanges many = Layer(Enumerable.Range(0, 80).Select(i => File($"f{i:00}", 10, $"h{i}")).ToArray());
        using ExplorerUiHarness ui = OpenCustom(Custom([many]), out _);
        ExplorerState s = ui.State;

        ui.Press(Key.PageDown);
        int page = s.Cursor;
        Assert.True(page > 10, $"page moved {page}");
        AssertShows(ui, $"f{page:00}");
        AssertHides(ui, "f00 ");
        ui.Press(Key.PageUp);
        Assert.Equal(0, s.Cursor);
        ui.Press(Key.End);
        Assert.Equal(79, s.Cursor);
        AssertShows(ui, "f79");
    }

    [Fact]
    public void ChangeKeysFilterTheTreeAndEscClearsThem()
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;
        ui.Window.Apply(new SelectLayer(3));
        s.Expanded.Add("app");
        s.Expanded.Add("app/cache");
        ui.Window.Presenter.Invalidate();
        List<string> Paths() => ui.Window.Presenter.Flatten(s).Select(row => row.Path).ToList();
        Assert.Contains("app/cache/big.bin", Paths());

        foreach ((char key, Change change) in new[] { ('+', Change.Added), ('~', Change.Modified), ('=', Change.Identical), ('-', Change.Removed) })
        {
            ui.Press(new Key(key));
            Assert.Contains(change, s.Hidden);
        }
        Assert.Empty(Paths());

        ui.Press(new Key('-'));
        Assert.DoesNotContain(Change.Removed, s.Hidden);
        Assert.Contains("app/cache/big.bin", Paths());
        // Folders are shown only for the visible changes inside them.
        Assert.DoesNotContain("app/dist", Paths());

        ui.Press(Key.Esc);
        Assert.Empty(s.Hidden);
        Assert.Contains("app/dist", Paths());
        Assert.False(ui.Window.StopRequested);
    }

    [Fact]
    public void FooterHintsAreClickable()
    {
        using ExplorerUiHarness ui = Open(out _);
        string footer = Footer(ui);
        foreach (string hint in new[] { "Tab  Layers", "[ ]  Step layer", "a  Whole filesystem", "+ ~ = -  Filter",
            "Enter  Inspect", "/  Search", "i  Insights", "?  Keys", "q  Quit" })
        {
            Assert.Contains(hint, footer);
        }
        Assert.DoesNotContain("↑↓  Move", footer);
        Assert.DoesNotContain("←→  Fold", footer);
        Assert.DoesNotContain("PgUp PgDn  Page", footer);
        Assert.DoesNotContain("Home End  First or last", footer);

        ui.Click(footer.IndexOf("i  Insights", StringComparison.Ordinal) + 3, ui.Height - 1);
        Assert.Equal(RightView.Insights, ui.State.View);
        ui.Press(Key.Esc);
        ui.Click(Footer(ui).IndexOf("?  Keys", StringComparison.Ordinal) + 3, ui.Height - 1);
        Assert.Equal(RightView.Keys, ui.State.View);
        ui.Press(Key.Esc);
        ui.Click(Footer(ui).IndexOf("Tab  Layers", StringComparison.Ordinal) + 2, ui.Height - 1);
        Assert.Equal(FocusPane.Layers, ui.State.Focus);
    }

    [Fact]
    public void LayerSizesAreShadedInProportionWithWasteInRed()
    {
        using ExplorerUiHarness ui = Open(out _);
        ui.Window.Apply(new FocusOn(FocusPane.Right));
        ExplorerImage img = ui.Window.Presenter.Image;
        long max = Enumerable.Range(0, img.LayerCount).Max(img.LayerSize);
        PaneContent pane = ui.Window.Presenter.LayersPane(ui.State);
        Rgb[] strata = ui.Window.Presenter.StrataColors();
        List<double> shaded = [];
        bool sawWaste = false;
        for (int layer = 0; layer < img.LayerCount; layer++)
        {
            bool selected = layer == ui.State.Layer;
            Line line = pane.Lines.Single(l => l.ToString().StartsWith($"{(selected ? '▌' : ' ')}{layer} "));
            // The size is right-aligned in the gauge, straight after the index.
            string gauge = line.ToString().Substring(3, ExplorerPresenter.GaugeWidth);
            Assert.Equal(Fmt.Size(img.LayerSize(layer)).PadLeft(ExplorerPresenter.GaugeWidth), gauge);
            Assert.StartsWith(gauge + " ", line.ToString()[3..]);

            Rgb under = selected ? Theme.Graphite : Theme.Ground;
            // The same colour the layer has in the core bar above.
            Rgb fill = strata[layer];
            Assert.Contains(strata[layer], img.Row(layer).IsBase ? new[] { Theme.Bedrock1, Theme.Bedrock2 } : new[] { Theme.Sand1, Theme.Sand2 });
            Rgb[] cells = Backgrounds(line).Skip(3).Take(ExplorerPresenter.GaugeWidth).Select(bg => bg ?? under).ToArray();
            double reach = ExplorerPresenter.GaugeReach((double)img.LayerSize(layer) / max);
            int full = (int)reach;
            // Fully covered cells take the fill (or red for waste); past the reach, the row shows through.
            Assert.All(cells[..full], bg => Assert.True(bg == fill || bg == Theme.StratumWaste));
            Assert.All(cells[(int)Math.Ceiling(reach)..], bg => Assert.Equal(under, bg));
            sawWaste |= cells.Contains(Theme.StratumWaste);
            // The number reads in one colour, whatever is shaded behind it.
            Assert.All(Foregrounds(line).Skip(3).Take(ExplorerPresenter.GaugeWidth), fg => Assert.Equal(Theme.Foam, fg));
            // Coverage, weighting the blended edge cell by how far it is from the unselected gauge background.
            shaded.Add(cells.Take((int)Math.Ceiling(reach)).Sum(bg => Coverage(Theme.Ground, fill, bg)));
        }
        Assert.True(sawWaste);
        // The largest layer fills the gauge; the rest are ordered by size and don't collapse together.
        Assert.Equal(ExplorerPresenter.GaugeWidth, shaded[Enumerable.Range(0, img.LayerCount).First(l => img.LayerSize(l) == max)], 1);
        for (int a = 0; a < img.LayerCount; a++)
        {
            for (int b = 0; b < img.LayerCount; b++)
            {
                // Layers too small to show more than the minimum sliver are allowed to tie.
                double reachA = ExplorerPresenter.GaugeReach((double)img.LayerSize(a) / max);
                double reachB = ExplorerPresenter.GaugeReach((double)img.LayerSize(b) / max);
                if (img.LayerSize(a) > img.LayerSize(b) && reachA - reachB > 0.1)
                {
                    Assert.True(shaded[a] > shaded[b], $"layer {a} ({img.LayerSize(a)}) should shade more than layer {b} ({img.LayerSize(b)})");
                }
            }
        }
    }

    [Fact]
    public void SelectingALayerPreservesItsSizeAndCoreBarColors()
    {
        ExplorerImage img = ExplorerSamples.Image();
        ExplorerPresenter presenter = new(img, 150, 42);
        ExplorerState state = new() { Focus = FocusPane.Layers };
        Rgb[] strata = presenter.StrataColors();

        foreach (int layer in new[] { 1, 2 })
        {
            state.Layer = layer == 1 ? 2 : 1;
            Rgb?[] unselectedBar = Foregrounds(presenter.Header(state)[1]).ToArray();
            Line unselected = presenter.LayersPane(state).Lines.Single(line => line.ToString().StartsWith($" {layer} "));
            state.Layer = layer;
            Rgb?[] selectedBar = Foregrounds(presenter.Header(state)[1]).ToArray();
            Line selected = presenter.LayersPane(state).Lines.Single(line => line.ToString().StartsWith($"▌{layer} "));

            int shadedCells = (int)Math.Ceiling(ExplorerPresenter.GaugeReach(
                (double)img.LayerSize(layer) / Enumerable.Range(0, img.LayerCount).Max(img.LayerSize)));
            Assert.Equal(Backgrounds(unselected).Skip(3).Take(shadedCells),
                Backgrounds(selected).Skip(3).Take(shadedCells));
            Assert.Equal(unselectedBar, selectedBar);
            Assert.Equal(Theme.Channel, selected.Parts[0].Sty.Foreground);
            if (layer == 1)
            {
                Assert.Contains(strata[layer], Backgrounds(selected).Skip(3).Take(shadedCells));
            }
            else
            {
                Assert.Contains(Theme.StratumWaste, Backgrounds(selected).Skip(3).Take(shadedCells));
            }
        }
    }

    // How much of a cell is shaded: the blend amount that reproduces its colour
    // from the row colour toward the fill or the waste colour, whichever fits.
    private static double Coverage(Rgb under, Rgb fill, Rgb bg)
    {
        static (double Amount, double Error) Fit(Rgb from, Rgb to, Rgb bg)
        {
            (double, double)[] ch = [(from.R, to.R), (from.G, to.G), (from.B, to.B)];
            double[] got = [bg.R, bg.G, bg.B];
            double num = 0, den = 0;
            for (int i = 0; i < 3; i++)
            {
                num += (got[i] - ch[i].Item1) * (ch[i].Item2 - ch[i].Item1);
                den += (ch[i].Item2 - ch[i].Item1) * (ch[i].Item2 - ch[i].Item1);
            }
            double t = den == 0 ? 0 : Math.Clamp(num / den, 0, 1);
            double err = 0;
            for (int i = 0; i < 3; i++)
            {
                err += Math.Abs(ch[i].Item1 + (ch[i].Item2 - ch[i].Item1) * t - got[i]);
            }
            return (t, err);
        }
        var a = Fit(under, fill, bg);
        var b = Fit(under, Theme.StratumWaste, bg);
        return a.Error <= b.Error ? a.Amount : b.Amount;
    }

    [Fact]
    public void LayerRowsUseOnlyTheMarginsTheyNeedAndKeepInstructionsAligned()
    {
        Valleysoft.DockerRegistryClient.Models.Images.LayerHistory[] history =
        [
            new() { CreatedBy = "RUN /bin/sh -c step 0 # buildkit" },
            new() { CreatedBy = "ENV APP_UID=1654 ASPNETCORE_HTTP_PORTS=8080", IsEmptyLayer = true },
            new() { CreatedBy = "RUN /bin/sh -c step 1 # buildkit" },
        ];
        ExplorerImage img = new("registry.test/group/app:1.0", "linux/amd64", "sha256:m", ["sha256:c0", "sha256:c1"], [100, 200],
            history, baseLayerCount: null, baseName: null, now: new DateTime(2026, 1, 1));
        using ExplorerUiHarness ui = OpenCustom(img, out _);
        PaneContent pane = ui.Window.Presenter.LayersPane(ui.State);
        // The marker sits in the pane's padding column, and the index is only as wide as it needs to be.
        Assert.Equal(0, pane.Inset);
        string gauge = "waiting".PadLeft(ExplorerPresenter.GaugeWidth);
        Assert.Equal([$"▌0 {gauge} RUN step 0", $"{new string(' ', 4 + ExplorerPresenter.GaugeWidth)}ENV APP_UID=165", $" 1 {gauge} RUN step 1"],
            pane.Lines.Select(l => l.ToString().TrimEnd()[..Math.Min(l.ToString().TrimEnd().Length, 3 + ExplorerPresenter.GaugeWidth + 16)]));
        using ExplorerUiHarness screen = OpenCustom(img, out _);
        Assert.Contains(screen.Screen().Split('\n'), row => row.Contains("┃▌0 ") || row.Contains("│▌0 "));
    }

    [Fact]
    public void GaugeUsesASquareRootScaleSoSmallLayersStayDistinct()
    {
        // With one layer 25x the next, a linear scale would give it a third of one cell.
        Assert.Equal(ExplorerPresenter.GaugeWidth, ExplorerPresenter.GaugeReach(1));
        Assert.Equal(ExplorerPresenter.GaugeWidth / 5.0, ExplorerPresenter.GaugeReach(1 / 25.0), 6);
        Assert.Equal(0, ExplorerPresenter.GaugeReach(0));
        Assert.True(ExplorerPresenter.GaugeReach(1e-9) > 0);
        Assert.True(ExplorerPresenter.GaugeReach(0.16) > ExplorerPresenter.GaugeReach(0.08));
    }

    private static IEnumerable<Rgb?> Foregrounds(Line line) =>
        line.Parts.SelectMany(p => Enumerable.Repeat(p.Sty.Foreground, p.Text.Length));

    private static IEnumerable<Rgb?> Backgrounds(Line line) =>
        line.Parts.SelectMany(p => Enumerable.Repeat(p.Sty.Background, p.Text.Length));

    [Fact]
    public void OnlyPanesWhoseContentChangedRepaint()
    {
        using ExplorerUiHarness ui = Open(out _);
        ui.Window.Apply(new Redraw());
        Assert.False(ui.Window.Layers.NeedsDraw);
        Assert.False(ui.Window.Right.NeedsDraw);

        Assert.Equal(FocusPane.Right, ui.State.Focus);
        ui.Window.Apply(new Move(1));
        Assert.True(ui.Window.Right.NeedsDraw);
        Assert.False(ui.Window.Layers.NeedsDraw);
    }

    [Fact]
    public void RepaintingUnchangedCellsSendsNothingToTheTerminal()
    {
        using ExplorerUiHarness ui = Open(out _);
        int dirty = -1;
        ui.Window.DrawComplete += (_, _) => dirty = CountDirty(ui.App.Driver!.Contents!);

        ui.Window.Right.SetNeedsDraw();
        ui.App.LayoutAndDraw();
        Assert.Equal(0, dirty);

        // A cursor move sends only the rows whose cells changed, each as a single run
        // from the left edge so it costs one cursor move and one write.
        int rows = -1;
        bool contiguous = false;
        ui.Window.DrawComplete += (_, _) => (rows, contiguous) = DirtyRows(ui.App.Driver!.Contents!);
        ui.Window.Apply(new Move(1));
        ui.App.LayoutAndDraw();
        // The old and new cursor rows, plus the pinned row describing the selection.
        Assert.Equal(3, rows);
        Assert.True(contiguous);

        // After a full clear the terminal is blank, so every cell is sent again.
        ui.App.LayoutAndDraw(forceRedraw: true);
        Assert.True(dirty > ui.Width * (ui.Height - 1));
    }

    [Fact]
    public void AnIdleLoopStopsRewritingTheCursor()
    {
        using ExplorerUiHarness ui = Open(out _);
        IDriver driver = ui.App.Driver!;

        // Browsing the tree the cursor stays hidden; once it is, nothing is rewritten.
        ui.App.Navigation!.UpdateCursor();
        Assert.True(driver.GetCursorNeedsUpdate());
        ResponsiveLoop.QuietCursor(ui.App);
        Assert.False(driver.GetCursorNeedsUpdate());

        // Focusing the search field wants a visible cursor, so it is not skipped
        // until the terminal cursor shows in the field.
        ui.Press(new Key('/'));
        ui.Type("js");
        Assert.True(ui.Window.Search.HasFocus);
        driver.SetCursorNeedsUpdate(true);
        ResponsiveLoop.QuietCursor(ui.App);
        Assert.True(driver.GetCursorNeedsUpdate());
        ui.App.Navigation.UpdateCursor();
        Assert.True(driver.GetCursor().IsVisible);
        ResponsiveLoop.QuietCursor(ui.App);
        Assert.False(driver.GetCursorNeedsUpdate());
    }

    private static (int Rows, bool Contiguous) DirtyRows(Terminal.Gui.Drawing.Cell[,] cells)
    {
        int rows = 0;
        bool contiguous = true;
        for (int r = 0; r < cells.GetLength(0); r++)
        {
            int runs = 0;
            for (int c = 0; c < cells.GetLength(1); c++)
            {
                if (cells[r, c].IsDirty && (c == 0 || !cells[r, c - 1].IsDirty))
                {
                    runs++;
                    contiguous &= c == 0;
                }
            }
            rows += runs > 0 ? 1 : 0;
            contiguous &= runs <= 1;
        }
        return (rows, contiguous);
    }

    private static int CountDirty(Terminal.Gui.Drawing.Cell[,] cells)
    {
        int n = 0;
        foreach (Terminal.Gui.Drawing.Cell c in cells)
        {
            n += c.IsDirty ? 1 : 0;
        }
        return n;
    }

    [Fact]
    public void MouseClicksDoubleClicksAndWheelsTheTree()
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host);
        ExplorerState s = ui.State;

        (int x, int y) = ui.Find("src/");
        ui.Click(x, y);
        Assert.Equal(RowOf(ui, "app/src"), s.Cursor);
        Assert.DoesNotContain("app/src", s.Expanded);
        ui.DoubleClick(x, y);
        Assert.Contains("app/src", s.Expanded);

        (int px, int py) = ui.Find("package.json");
        ui.DoubleClick(px, py);
        Assert.Equal(RightView.Inspector, s.View);
        Assert.Equal("app/package.json", s.InspectPath);
        ui.Until(() => s.Preview is not null, "the preview");
        ui.Press(Key.Esc);

        s.Expanded.Add("app/cache");
        s.Expanded.Add("app/node_modules");
        s.Expanded.Add("app/node_modules/left-pad");
        ui.Window.Presenter.Invalidate();
        ui.Window.Apply(new SetCursor(0));
        ui.Pump();
        (int tx, int ty) = ui.Find("▾ app/");
        ui.Wheel(tx, ty, down: true);
        Assert.Equal(3, s.Cursor);
        ui.Wheel(tx, ty, down: false);
        Assert.Equal(0, s.Cursor);

        // Wheeling over the layer list steps layers.
        (int lx, int ly) = ui.Find("COPY . /app");
        ui.Wheel(lx, ly, down: true);
        Assert.Equal(3, s.Layer);
    }

    [Fact]
    public void FirstUserLayerSkipsTheBaseOrExplainsWhyItCannot()
    {
        using (ExplorerUiHarness ui = Open(out _))
        {
            ui.Window.Apply(new SelectLayer(3));
            ui.Press(new Key('b'));
            Assert.Equal(1, ui.State.Layer);
        }

        using (ExplorerUiHarness ui = OpenCustom(Custom([Layer([File("a", 1, "a")]), Layer([File("b", 1, "b")])]), out _, layer: 1))
        {
            // Without a verified base the key is not offered, so it does nothing.
            Assert.DoesNotContain(ui.Window.Presenter.Hints(ui.State), h => h.Cmd is FirstUserLayer);
            ui.Press(new Key('b'));
            Assert.Equal(1, ui.State.Layer);
            Assert.Null(ui.State.Notice);
        }

        using (ExplorerUiHarness ui = OpenCustom(Custom([Layer([File("a", 1, "a")]), Layer([File("b", 1, "b")])], baseLayerCount: 2), out _))
        {
            Assert.DoesNotContain(ui.Window.Presenter.Hints(ui.State), h => h.Cmd is FirstUserLayer);
            ui.Press(new Key('b'));
            Assert.Null(ui.State.Notice);
        }
    }

    [Fact]
    public void StepLayerStopsAtTheEnds()
    {
        using ExplorerUiHarness ui = Open(out _);
        ui.Window.Apply(new SelectLayer(3));
        ui.Press(new Key(']'));
        Assert.Equal(3, ui.State.Layer);
        ui.Window.Apply(new SelectLayer(0));
        ui.Press(new Key('['));
        Assert.Equal(0, ui.State.Layer);
        ui.Press(Key.Tab);
        ui.Press(Key.End);
        Assert.Equal(3, ui.State.Layer);
        ui.Press(Key.CursorUp);
        Assert.Equal(2, ui.State.Layer);
    }

    // ───────────────────────────── tour 2: loading ─────────────────────────────

    [Fact]
    public void LoadingShowsProgressAndDefersWholeImageActions()
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host, complete: false);
        ExplorerState s = ui.State;

        Assert.Contains("2 of 4 layers indexed", ui.Row(0));
        Assert.DoesNotContain("efficient", ui.Row(0));
        Assert.Contains("1.2 KB left to download", Footer(ui));
        AssertShows(ui, "Layer 2 is next in line…", "0 B of 900 B read", "900 B download · waiting to index", "waiting");
        Assert.DoesNotContain("i  Insights", Footer(ui));

        ui.Press(new Key('x'));
        Assert.Equal("Select a file or folder to extract.", s.Notice);
        ui.Window.Apply(new SelectLayer(1));
        ui.Press(new Key('x'));
        Assert.Equal("Extract works once every layer is indexed.", s.Notice);
        ui.Window.Apply(new SetCursor(0));
        s.Expanded.Add("usr");
        s.Expanded.Add("usr/bin");
        ui.Window.Presenter.Invalidate();
        ui.Window.Apply(new SetCursor(RowOf(ui, "usr/bin/tool")));
        ui.Window.Apply(new OpenInViewer());
        Assert.Equal("The viewer works once every layer is indexed.", s.Notice);
        ui.Pump();
        AssertShows(ui, "The viewer works once every layer is indexed.");

        // Selecting an unindexed layer moves it to the front of the queue.
        ui.Window.Apply(new SelectLayer(3));
        Assert.Contains(3, host.Prioritized);
    }

    [Fact]
    public void RetryingAFailedLayerQueuesItAgain()
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host, complete: false);
        ExplorerImage img = ui.Window.Presenter.Image;
        img.States[2] = ExplorerLayerState.Failed;
        img.Errors[2] = "connection reset";
        ui.Window.ImageChanged();
        ui.Pump();

        AssertShows(ui, "This layer failed to index.", "connection reset", "↻ retry");
        ui.Press(new Key('r'));
        Assert.Equal([2], host.Retried);
        Assert.Equal("Retrying layer 2…", ui.State.Notice);
        Assert.Null(img.Errors[2]);
    }

    // ───────────────────────────── tour 3: whole filesystem ─────────────────────────────

    [Fact]
    public void WholeFilesystemMergesEarlierLayersAndShowsLinks()
    {
        ExplorerImage img = Custom(
        [
            Layer([File("etc/os-release", 10, "os"), File("usr/lib/v1.so", 10, "lib")]),
            Layer([Link("usr/lib/current.so", "v1.so"), File("app/main", 20, "main")]),
        ]);
        using ExplorerUiHarness ui = OpenCustom(img, out _, layer: 1);
        ExplorerState s = ui.State;
        AssertShows(ui, "Changes in layer 1", "+ 2 added");
        AssertHides(ui, "etc/", "unchanged");

        ui.Press(new Key('a'));
        Assert.True(s.WholeFilesystem);
        AssertShows(ui, "Filesystem at layer 1", "etc/", "· 2 unchanged", "in 4 files");
        s.Expanded.Add("usr");
        s.Expanded.Add("usr/lib");
        ui.Window.Presenter.Invalidate();
        ui.Window.Apply(new Redraw());
        ui.Pump();
        AssertShows(ui, "current.so → v1.so", "lrwxrwxrwx");

        // The unchanged filter applies to earlier layers' files.
        ui.Press(new Key('='));
        Assert.DoesNotContain(ui.Window.Presenter.Flatten(s), row => row.Path == "etc/os-release");

        ui.Window.Apply(new SetCursor(RowOf(ui, "usr/lib/current.so")));
        ui.Press(Key.Enter);
        Assert.Equal(RightView.Inspector, s.View);
        AssertShows(ui, "→ v1.so");
    }

    // ───────────────────────────── tour 4: insights ─────────────────────────────

    [Fact]
    public void InsightsRankFindingsAndExplainTheSelectedOne()
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;
        ui.Press(new Key('i'));

        AssertShows(ui, "Insights  3 findings", "2.0 MB hidden file payload · 37% efficient", "1.2 MB more potential savings",
            "■ Deleted later 2.0 MB", "■ Replaced by later layers 40 B",
            "1 Files deleted after they were shipped · hidden", "layers 2 → 3", "/app/cache/big.bin",
            "Build in a separate stage", "2 Other small overwrites · hidden",
            "3 apt package lists left in the image · potential", "RUN ... && rm -rf /var/lib/apt/lists/*",
            "Why this is reclaimable", "Layer 2 shipped 1 file under /app/cache/big.bin.", "Layer 3 deleted it, hiding 2.0 MB.");
        Assert.Contains("↑↓  Finding", Footer(ui));
        Assert.Contains("Enter  Show files", Footer(ui));

        ui.Press(Key.CursorDown);
        Assert.Equal(1, s.Finding);
        AssertShows(ui, "Paths   /app/package.json");
        ui.Press(Key.End);
        Assert.Equal(2, s.Finding);
        AssertShows(ui, "Why this may be reclaimable", "/var/lib/apt/lists/main");
        ui.Press(Key.CursorDown);
        Assert.Equal(2, s.Finding);
        ui.Press(Key.Home);
        Assert.Equal(0, s.Finding);

        // Clicking a finding selects it; Enter on a potential finding opens its layer.
        (int x, int y) = ui.Find("3 apt package lists");
        ui.Click(x + 4, y);
        Assert.Equal(2, s.Finding);
        ui.Press(Key.Enter);
        Assert.Equal(RightView.Files, s.View);
        Assert.Equal(1, s.Layer);
        Assert.True(s.FindingsOnly);
        Assert.Equal(RowOf(ui, "var/lib/apt/lists/main"), s.Cursor);
        AssertShows(ui, "main");
    }

    [Fact]
    public void BaseImageFindingsAreGroupedUntilShown()
    {
        // Layer 1 of the base overwrites a file from layer 0; the user's layer is clean.
        ExplorerImage img = Custom(
        [
            Layer([File("usr/share/big.dat", 3_000_000, "old")]),
            Layer([File("usr/share/big.dat", 3_000_000, "new")]),
            Layer([File("app/main", 10, "main")]),
        ], baseLayerCount: 2);
        using ExplorerUiHarness ui = OpenCustom(img, out _, layer: 2);
        ExplorerState s = ui.State;
        ui.Press(new Key('i'));

        AssertShows(ui, "Insights  1 finding", "1 finding inside the base image · not fixable here", "Rebuild or update the base image");
        // Only the group row (null) is listed until it is expanded.
        Assert.Equal([null], ui.Window.Presenter.VisibleFindings(s));
        AssertHides(ui, "/usr/share/big.dat");
        ui.Press(Key.Enter);
        Assert.True(s.ShowBaseFindings);
        Assert.Contains(ui.Window.Presenter.VisibleFindings(s), f => f is { FromBase: true });
        AssertShows(ui, "/usr/share/big.dat");
    }

    [Fact]
    public void FindingsOnlyHidesUninvolvedPathsAndIsDeferredWhileLoading()
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;
        ui.Press(new Key('w'));
        Assert.True(s.FindingsOnly);
        List<string> shown = ui.Window.Presenter.Flatten(s).Select(row => row.Path).ToList();
        Assert.Contains("app/package.json", shown);
        Assert.DoesNotContain("app/src", shown);
        ui.Press(Key.Esc);
        Assert.False(s.FindingsOnly);
    }

    // ───────────────────────────── tour 5: inspector ─────────────────────────────

    [Fact]
    public void InspectorShowsMetadataHistoryAndAHighlightedPreview()
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host);
        ExplorerState s = ui.State;
        ui.Window.Apply(new SetCursor(RowOf(ui, "app/package.json")));
        ui.Press(Key.Enter);
        ui.Until(() => s.Preview is not null, "the preview");

        AssertShows(ui, "package.json  /app/package.json", "-rw-r--r--   0:0   40 B   sha256:pkg1", "History",
            "layer 2  + added", "COPY . /app", "layer 3  ~ modified", "Preview  json · 3 lines",
            "1  {", "2    \"name\": \"storefront\"", "3  }");

        Assert.DoesNotContain(ui.Window.Presenter.Hints(s), hint => hint.Label == "Step layer");
        Assert.DoesNotContain("[ ]  Step layer", Footer(ui));
        int layer = s.Layer;
        ui.Press(new Key(']'));
        ui.Press(new Key('['));
        ui.Window.Apply(new StepLayer(1));
        Assert.Equal(layer, s.Layer);
        Assert.Equal(RightView.Inspector, s.View);
        Assert.True(ui.Shows("\"storefront\""), ui.Screen());

        ui.Press(Key.Esc);
        Assert.Equal(RightView.Files, s.View);
        ui.Press(new Key(']'));
        Assert.Equal(3, s.Layer);
    }

    [Theory]
    [InlineData(150, 42)]
    [InlineData(100, 40)]
    public void InspectorUsesFullWidthAndRestoresTheExplorerOnBack(int width, int height)
    {
        using ExplorerUiHarness ui = Open(out _, width: width, height: height);
        ui.Window.Apply(new SetCursor(RowOf(ui, "app/package.json")));
        ui.Press(Key.Enter);
        ui.Until(() => ui.State.Preview is not null, "the file preview");

        Assert.Equal(RightView.Inspector, ui.State.View);
        Assert.False(ui.Window.Layers.Visible);
        Assert.Equal(0, ui.Window.Right.Frame.X);
        Assert.Equal(width, ui.Window.Right.Frame.Width);
        Assert.Equal(ExplorerPresenter.HeaderHeight, ui.Window.Right.Frame.Y);
        Assert.True(ui.Window.Right.HasFocus);
        Assert.True(ui.Shows("\"storefront\""), ui.Screen());
        int selectedLayer = ui.State.Layer;
        ui.Click(width / 2, 1);
        Assert.Equal(selectedLayer, ui.State.Layer);
        Assert.Equal(RightView.Inspector, ui.State.View);

        ui.Press(Key.Esc);
        Assert.Equal(RightView.Files, ui.State.View);
        Assert.True(ui.Window.Layers.Visible);
        Assert.Equal(width < 120 ? 0 : ExplorerPresenter.LeftWidth, ui.Window.Right.Frame.X);
        Assert.Equal(width < 120 ? width : width - ExplorerPresenter.LeftWidth, ui.Window.Right.Frame.Width);
        Assert.True(ui.Shows("package.json"), ui.Screen());
    }

    [Fact]
    public void InspectorScrollsLongPreviewsAndExplainsWhatItCannotShow()
    {
        string[] lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray();
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState { Layer = 2 },
            session => new FakeExplorerHost
            {
                Baseline = session,
                Preview = path => path switch
                {
                    "app/src/index.js" => new PreviewContent(path, "javascript", [.. lines], "Showing the first 256 KB.", 900_000),
                    "app/cache/big.bin" => new PreviewContent(path, null, null, "Binary file, 2.0 MB.", 2_000_000),
                    _ => throw new IOException("blob unavailable"),
                },
            }, out _);
        ExplorerState s = ui.State;
        s.Expanded.Add("app");
        s.Expanded.Add("app/src");
        s.Expanded.Add("app/cache");
        ui.Window.Presenter.Invalidate();

        ui.Window.Apply(new SetCursor(RowOf(ui, "app/src/index.js")));
        ui.Press(Key.Enter);
        ui.Until(() => s.Preview is not null, "the preview");
        AssertShows(ui, "line 1 ", "Showing the first 256 KB.");
        ui.Press(Key.PageDown);
        Assert.True(s.PreviewScroll > 0);
        AssertHides(ui, "line 1 ");
        ui.Press(Key.End);
        AssertShows(ui, "line 200");
        ui.Press(Key.Home);
        Assert.Equal(0, s.PreviewScroll);
        ui.Press(Key.Esc);

        ui.Window.Apply(new SetCursor(RowOf(ui, "app/cache/big.bin")));
        ui.Press(Key.Enter);
        ui.Until(() => s.Preview is not null, "the preview");
        AssertShows(ui, "Binary file, 2.0 MB.");
        ui.Press(Key.Esc);

        ui.Window.Apply(new SetCursor(RowOf(ui, "app/package.json")));
        ui.Press(Key.Enter);
        ui.Until(() => s.Preview is not null, "the preview");
        AssertShows(ui, "blob unavailable");
    }

    [Fact]
    public void ViewerAndExtractNeedALiveFile()
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;
        ui.Window.Apply(new SetCursor(RowOf(ui, "app")));
        ui.Window.Apply(new OpenInViewer());
        Assert.Equal("Select a file to open in the viewer.", s.Notice);
        s.Expanded.Add("app/cache");
        ui.Window.Presenter.Invalidate();
        ui.Window.Apply(new SetCursor(RowOf(ui, "app/cache/big.bin")));
        ui.Window.Apply(new OpenInViewer());
        Assert.Equal("/app/cache/big.bin is not in the final image.", s.Notice);
        Assert.False(ui.Window.StopRequested);
    }

    // ───────────────────────────── tour 6: search ─────────────────────────────

    [Fact]
    public void SearchResultsShowLayerBadgesAndTheSelection()
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;
        ui.Press(new Key('/'));
        ui.Type("package");

        AssertShows(ui, "Search  names and paths", "Whole image", "Layer 2", "✓ Include deleted", "Exact case",
            "1 match in 9 paths", "2+ 3~", "45 B", "/app/package.json", "▲ hidden by layer 3",
            "to open it in the last layer that wrote it.");
        Assert.Contains("Alt+L  Layer 2 only", Footer(ui));
        Assert.Contains("Alt+D  Hide deleted", Footer(ui));

        ui.Window.Search.Text = "app/";
        ui.Pump();
        int hits = ui.Window.Presenter.SearchResults(s).Hits.Count;
        Assert.True(hits > 3, $"{hits} hits");
        ui.Press(Key.CursorDown);
        ui.Press(Key.CursorDown);
        Assert.Equal(2, s.SearchCursor);
        ui.Press(Key.CursorUp);
        Assert.Equal(1, s.SearchCursor);
        Assert.True(ui.Window.Search.HasFocus);

        // Deleted paths are struck through and marked with the layer that deleted them.
        AssertShows(ui, "2+ 3−", "/app/cache/big.bin");

        // Clicking the chips toggles them.
        (int dx, int dy) = ui.Find("Include deleted");
        ui.Click(dx + 2, dy);
        Assert.False(s.SearchIncludeDeleted);
        (int cx, int cy) = ui.Find("Exact case");
        ui.Click(cx + 2, cy);
        Assert.True(s.SearchExactCase);
        Assert.True(ui.Window.Search.HasFocus);

        ui.Press(Key.Esc);
        Assert.Equal(RightView.Files, s.View);
        Assert.False(ui.Window.Search.Visible && ui.Window.Search.HasFocus);
    }

    [Fact]
    public void ClickingASearchResultSelectsItAndDoubleClickOpensIt()
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;
        ui.Press(new Key('/'));
        ui.Type("index.js");
        Assert.Equal(2, ui.Window.Presenter.SearchResults(s).Hits.Count);

        (int x, int y) = ui.Find("/app/src/index.js");
        ui.Click(x, y);
        Assert.Equal(ui.Window.Presenter.SearchResults(s).Hits.FindIndex(h => h.Path == "app/src/index.js"), s.SearchCursor);
        // A second click on the same spot completes a double-click.
        ui.Click(x, y);
        Assert.Equal(RightView.Files, s.View);
        Assert.Equal(RowOf(ui, "app/src/index.js"), s.Cursor);
        Assert.True(s.WholeFilesystem);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheMouseOverAPaneLeavesFocusInTheSearchField(bool wheel)
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;
        ui.Press(new Key('/'));
        ui.Type("pack");
        ui.Window.Apply(new SelectLayer(3));

        (int x, int y) = ui.Find("COPY . /app");
        if (wheel)
        {
            ui.Wheel(x, y, down: true);
        }
        else
        {
            Assert.Equal(3, s.Layer);
            ui.Click(x, y);
            Assert.Equal(2, s.Layer);
        }
        Assert.True(ui.Window.Search.HasFocus);

        ui.Type("q");
        Assert.False(ui.Window.StopRequested);
        Assert.Equal("packq", ui.Window.Search.Text);
        Assert.Equal(RightView.Search, s.View);
    }

    [Fact]
    public void TheMouseOverAPaneLeavesFocusInTheExtractPrompt()
    {
        using ExplorerUiHarness ui = Open(out _);
        ui.Window.Apply(new SelectLayer(3));
        ui.State.Expanded.Add("app");
        ui.Window.Presenter.Invalidate();
        ui.Window.Apply(new SetCursor(RowOf(ui, "app/package.json")));
        ui.Press(new Key('x'));
        Assert.True(ui.Window.ExtractField.HasFocus);
        string before = ui.Window.ExtractField.Text;

        (int x, int y) = ui.Find("COPY . /app");
        ui.Wheel(x, y, down: false);
        Assert.True(ui.Window.ExtractField.HasFocus);
        ui.Click(x, y);
        Assert.Equal(2, ui.State.Layer);
        Assert.True(ui.Window.ExtractField.HasFocus);

        ui.Type("q");
        Assert.False(ui.Window.StopRequested);
        Assert.Equal(before + "q", ui.Window.ExtractField.Text);
    }

    // ───────────────────────────── tour 7: tag picker ─────────────────────────────

    [Fact]
    public void TagPickerListsTagsWithSharingAndCancelsOnEsc()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState { Layer = 2 },
            session => new FakeExplorerHost { Baseline = session, Tags = ["2.0", "1.0", "other-base", "broken"] }, out FakeExplorerHost host);

        bool opened = ui.InDialog(() => ui.Press(new Key('c')),
            DialogStep.When("the tag details", () => ui.Shows("4 tags.") && ui.Shows("different base image") && ui.Shows("could not read this tag") && ui.Shows("to download"), () =>
            {
                AssertShows(ui, "Compare 1.0 with", "▲ current image", "2 of 3 shared    1.0 KB to download", "Download is what pulling the tag adds to 1.0.");
                (int oneY, int twoY) = (ui.Find("▌1.0 ").Y, ui.Find(" 2.0  ").Y);
                Assert.True(oneY < twoY, "the explored tag is listed first");
                ui.Send(Key.Esc);
            }));

        Assert.True(opened);
        Assert.Empty(host.Compared);
        Assert.Null(ui.State.Compare);
    }

    [Fact]
    public void TagPickerFiltersAndAcceptsTypedTags()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState { Layer = 2 },
            session => new FakeExplorerHost { Baseline = session, Tags = ["1.0", "2.0", "2.1", "3.0"] }, out FakeExplorerHost host);

        ui.InDialog(() => ui.Press(new Key('c')),
            DialogStep.When("the tags", () => ui.Shows("4 tags."), () => ui.Send("2.")),
            DialogStep.When("the filter", () => !ui.Shows("3.0"), () => ui.Send(Key.CursorDown, Key.Enter)));
        ui.Until(() => ui.State.Compare is not null, "the comparison");
        Assert.Equal(["2.1"], host.Compared);

        ui.Press(Key.Esc);
        ui.InDialog(() => ui.Press(new Key('c')),
            DialogStep.When("the tags", () => ui.Shows("4 tags."), () => ui.Send("nightly")),
            DialogStep.When("the typed tag", () => ui.Shows("nightly"), () => ui.Send(Key.Enter)));
        ui.Until(() => host.Compared.Count == 2 && ui.State.Compare is not null, "the comparison");
        Assert.Equal("nightly", host.Compared[1]);
    }

    [Fact]
    public void TagPickerStillAcceptsATagWhenListingFails()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState { Layer = 2 },
            session => new FakeExplorerHost { Baseline = session, TagsError = new InvalidOperationException("denied") }, out FakeExplorerHost host);

        ui.InDialog(() => ui.Press(new Key('c')),
            DialogStep.When("the error", () => ui.Shows("Could not list tags: denied"), () => ui.Send("2.0")),
            DialogStep.When("the typed tag", () => ui.Shows("2.0"), () => ui.Send(Key.Enter)));
        ui.Until(() => ui.State.Compare is not null, "the comparison");
        Assert.Equal(["2.0"], host.Compared);
    }

    // ───────────────────────────── tour 8: compare ─────────────────────────────

    private static ExplorerUiHarness Compared(out FakeExplorerHost host, Func<ExplorerSession?, FakeExplorerHost>? create = null,
        ExplorerImage? img = null, string tag = "2.0")
    {
        ExplorerUiHarness ui = ExplorerWindowTests.Open(img ?? ExplorerSamples.Image(), new ExplorerState { Layer = 2 },
            create ?? (session => new FakeExplorerHost { Baseline = session }), out host);
        ui.Window.StartCompare(tag);
        ui.Until(() => ui.State.Compare is not null, "the comparison");
        return ui;
    }

    [Fact]
    public void CompareShowsBothImagesLayerBylayer()
    {
        using ExplorerUiHarness ui = Compared(out _);

        string header = ui.Row(0);
        Assert.Contains("1.0 → 2.0", header);
        Assert.Contains("2 of 4 layers shared", header);
        Assert.Contains("−2.0 MB file payload", header);
        Assert.Contains("1.0 KB to download", header);
        Assert.StartsWith(" 1.0 ", ui.Row(1));
        Assert.StartsWith(" 2.0 ", ui.Row(2));

        AssertShows(ui, "Layers  2.0 vs 1.0", "Differences  1.0 → 2.0", "~ 1 updated   + 0 added   − 0 removed",
            "3 paths differ", "▾ Installed packages  1 package differs", "npm", "left-pad", "1.0.0 → 1.1.0",
            "▾ Files  3 paths differ", "▸ app/", "−791 B",
            "2.0  sha256:t2", "1.0  sha256:l2", "Additional download  1.0 KB for this layer", "Layers 0 to 1 are shared.");
        AssertHides(ui, "1 packages");
        Assert.Contains("same", ui.Row(ui.Find("  0   1.0 KB").Y));
        Assert.Contains("gone", ui.Row(ui.Find("Layer 3").Y));
        foreach (string hint in new[] { "[ ]  Next difference", "s  Swap sides", "Enter  Collapse group", "c  Change tag…", "Esc  Leave compare" })
        {
            Assert.Contains(hint, Footer(ui));
        }
        Assert.DoesNotContain("↑↓  Move", Footer(ui));
        Assert.DoesNotContain("←→  Fold", Footer(ui));
        Assert.DoesNotContain("PgUp PgDn  Page", Footer(ui));
        Assert.DoesNotContain("Home End  First or last", Footer(ui));
    }

    [Fact]
    public void CompareStepsLayersAndDifferences()
    {
        using ExplorerUiHarness ui = Compared(out _);
        CompareState c = ui.State.Compare!;

        ui.Press(new Key('['));
        Assert.Equal(2, c.Layer);
        Assert.Equal("No earlier layer differs.", ui.State.Notice);
        ui.Press(Key.Tab);
        Assert.True(c.FocusLayers);
        Assert.Contains("Tab  Differences", Footer(ui));
        ui.Press(Key.CursorUp);
        Assert.Equal(1, c.Layer);
        AssertShows(ui, "Same digest in both images, so this layer is shared.");
        ui.Press(Key.Home);
        Assert.Equal(0, c.Layer);
        ui.Press(Key.End);
        Assert.Equal(3, c.Layer);
        ui.Press(Key.Tab);
        Assert.False(c.FocusLayers);

        // Clicking a layer row selects it.
        (int x, int y) = ui.Find("Layer 1 ");
        ui.Click(x, y);
        Assert.Equal(1, c.Layer);
    }

    [Fact]
    public void CompareWithIdenticalImagesSaysEverythingIsShared()
    {
        using ExplorerUiHarness ui = Compared(out _, session => new FakeExplorerHost { Baseline = session, Target = _ => session! }, tag: "1.0");

        ui.Press(new Key(']'));
        Assert.Equal("Every layer is shared.", ui.State.Notice);
        AssertShows(ui, "4 of 4 layers shared");
    }

    [Fact]
    public void CompareFoldsRowsAndCollapsesUnchangedPackages()
    {
        Dictionary<string, string> before = new() { ["@babel/core"] = "7.0.0", ["@babel/parser"] = "7.0.0", ["@babel/types"] = "7.0.0", ["kept"] = "1.0.0" };
        Dictionary<string, string> after = new() { ["@babel/core"] = "7.1.0", ["@babel/parser"] = "7.1.0", ["@babel/types"] = "7.1.0", ["kept"] = "1.0.0", ["added"] = "1.0.0" };
        ExplorerSession baseline = Session(["sha256:a"], [Layer([File("app/x", 1, "x")])], before);
        ExplorerSession target = Session(["sha256:b"], [Layer([File("app/x", 2, "y")])], after);
        using ExplorerUiHarness ui = Compared(out _, _ => new FakeExplorerHost { Baseline = baseline, Target = _ => target });
        CompareState c = ui.State.Compare!;

        AssertShows(ui, "~ 3 updated   + 1 added   − 0 removed", "@babel", "3 packages", "added");
        List<CompareRow> Rows() => new CompareView(ui.Window.Presenter, c).Rows();
        CompareRow scope = Rows().First(row => row.Kind == CompareRowKind.Scope);
        Assert.DoesNotContain(Rows(), row => row.Package?.Name == "@babel/core");

        ui.Window.Apply(new SetCursor(Rows().IndexOf(scope)));
        ui.Press(Key.CursorRight);
        Assert.Contains(Rows(), row => row.Package?.Name == "@babel/core");
        AssertShows(ui, "7.0.0 → 7.1.0");
        ui.Press(Key.CursorLeft);
        Assert.DoesNotContain(Rows(), row => row.Package?.Name == "@babel/core");

        ui.Window.Apply(new SetCursor(Rows().FindIndex(row => row.Kind == CompareRowKind.Section)));
        ui.Press(Key.Enter);
        Assert.DoesNotContain(Rows(), row => row.Kind == CompareRowKind.Package);
        ui.Press(Key.Enter);

        c.Expanded.Add("file:app");
        ui.Window.Apply(new Redraw());
        Assert.Contains(Rows(), row => row.Path == "app/x");
        int cursor = c.Cursor;
        ui.Press(Key.Space);
        Assert.Contains(Rows(), row => row.Path == "app/x");
        Assert.Equal(cursor, c.Cursor);
    }

    [Fact]
    public void CompareSaysWhenPackageMetadataIsUnavailable()
    {
        ExplorerSession baseline = Session(["sha256:a"], [Layer([File("app/x", 1, "x")])], new() { ["left-pad"] = "1.0.0" });
        ExplorerSession target = Session(["sha256:b"], [Layer([File("app/x", 2, "y")])], [], npmAvailable: false);
        using ExplorerUiHarness ui = Compared(out _, _ => new FakeExplorerHost { Baseline = baseline, Target = _ => target });

        AssertShows(ui, "unavailable");
        AssertHides(ui, "left-pad");
    }

    [Fact]
    public void CompareHelpReturnsToCompareAndOtherActionsAskToLeaveFirst()
    {
        using ExplorerUiHarness ui = Compared(out FakeExplorerHost host);
        ExplorerState s = ui.State;

        ui.Press(new Key('?'));
        Assert.Equal(RightView.Keys, s.View);
        AssertShows(ui, "Compare", "Swap sides", "Leave compare");
        AssertShows(ui, "Esc  Back");
        // Keys the key list does not offer do nothing; Esc returns to the comparison rather than leaving it.
        ui.Press(new Key('i'));
        ui.Press(Key.Tab);
        Assert.Equal(RightView.Keys, s.View);
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Files, s.View);
        Assert.NotNull(s.Compare);

        AssertShows(ui, "Differences  1.0 → 2.0");
        foreach (char key in "ia+x")
        {
            ui.Press(new Key(key));
            Assert.Null(s.Notice);
            Assert.Equal(RightView.Files, s.View);
        }
        Assert.NotNull(s.Compare);

        // c picks another tag without leaving compare.
        ui.InDialog(() => ui.Press(new Key('c')),
            DialogStep.When("the tags", () => ui.Shows("2 tags."), () => ui.Send(Key.Enter)));
        ui.Until(() => host.Compared.Count == 2 && s.Compare is { Busy: false }, "the second comparison");
    }

    [Fact]
    public void CompareNoticeTransitionsToPackagesAndClearsOnCompletion()
    {
        TaskCompletionSource<ExplorerComparison> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState { Layer = 2 },
            session => new FakeExplorerHost
            {
                Baseline = session,
                CompareWork = () => completion.Task,
            }, out FakeExplorerHost host);

        ui.Window.StartCompare("2.0");
        Assert.Equal("Comparing with 2.0… reading its layers", ui.State.Notice);
        ui.Until(() => ui.State.Notice == "Comparing with 2.0… reading packages", "the package scan notice");
        Assert.Null(ui.State.Compare);

        completion.SetResult(ExplorerSession.Compare(host.Baseline!, ExplorerSamples.Target()));
        ui.Until(() => ui.State.Compare is not null, "the comparison");
        Assert.Null(ui.State.Notice);
    }

    [Fact]
    public void FailedPackageScanReplacesProgressWithError()
    {
        TaskCompletionSource<ExplorerComparison> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState { Layer = 2 },
            session => new FakeExplorerHost { Baseline = session, CompareWork = () => completion.Task }, out _);

        ui.Window.StartCompare("2.0");
        ui.Until(() => ui.State.Notice == "Comparing with 2.0… reading packages", "the package scan notice");
        completion.SetException(new InvalidOperationException("package scan failed"));
        ui.Until(() => ui.State.NoticeIsError, "the comparison error");
        ui.Pump();
        Assert.Equal("Could not compare with 2.0: package scan failed", ui.State.Notice);
        Assert.Null(ui.State.Compare);
    }

    [Fact]
    public void FailedCompareShowsAnError()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState { Layer = 2 },
            session => new FakeExplorerHost { Baseline = session, CompareError = new InvalidOperationException("manifest unknown") }, out _);

        ui.Window.StartCompare("9.9");
        Assert.Equal("Comparing with 9.9… reading its layers", ui.State.Notice);
        ui.Until(() => ui.State.NoticeIsError, "the error");
        Assert.Equal("Could not compare with 9.9: manifest unknown", ui.State.Notice);
        Assert.Null(ui.State.Compare);
        AssertShows(ui, "Could not compare with 9.9: manifest unknown");
    }

    [Fact]
    public void KeysOnlyActWhenTheirContextListsThem()
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host);
        ExplorerState s = ui.State;
        s.Expanded.Add("app");
        ui.Window.Presenter.Invalidate();
        ui.Window.Apply(new Redraw());

        // Outside compare, with one platform and no failed layer, s, p and r are not offered.
        foreach (char key in "spr")
        {
            ui.Press(new Key(key));
            Assert.Null(s.Notice);
        }
        Assert.Empty(host.Retried);
        Assert.False(ui.Window.StopRequested);

        // Copy and viewer belong to the inspector, not the file tree.
        ui.Window.Apply(new SetCursor(RowOf(ui, "app/package.json")));
        ui.Press(new Key('y'));
        ui.Press(new Key('o'));
        Assert.Null(s.Notice);
        Assert.False(ui.Window.StopRequested);

        // With the layer list focused, file keys do not reach the file tree.
        ui.Press(Key.Tab);
        Assert.Equal(FocusPane.Layers, s.Focus);
        foreach (Key key in new[] { new Key('y'), new Key('x'), new Key('o'), new Key('+'), Key.Enter, Key.Space, Key.CursorLeft, Key.PageDown })
        {
            ui.Press(key);
            Assert.Null(s.Notice);
            Assert.Contains("app", s.Expanded);
            Assert.Empty(s.Hidden);
            Assert.False(ui.Window.ExtractField.Visible);
        }
        Assert.Equal(2, s.Layer);

        // The inspector does not offer Tab, so focus stays put.
        ui.Press(Key.Tab);
        ui.Window.Apply(new SetCursor(RowOf(ui, "app/package.json")));
        ui.Press(Key.Enter);
        Assert.Equal(RightView.Inspector, s.View);
        ui.Press(Key.Tab);
        Assert.Equal(FocusPane.Right, s.Focus);
        ui.Press(new Key('a'));
        Assert.False(s.WholeFilesystem);
    }

    // ───────────────────────────── tour 9: keys ─────────────────────────────

    [Fact]
    public void HelpListsEveryGroupAndMarker()
    {
        using ExplorerUiHarness ui = Open(out _);
        ui.Press(new Key('?'));

        AssertShows(ui, "Move", "Views", "Layers", "Search", "Files", "Actions", "Compare", "Change markers", "Tips",
            "Toggle whole filesystem", "First layer after base", "Compare with a tag…", "Choose platform…",
            "Retry a failed layer", "Only paths with findings", "Swap sides", "Open file in text viewer");
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Files, ui.State.View);
    }

    public static TheoryData<char, string> DefaultKeys() => new()
    {
        { '?', "keys" }, { 'i', "insights" }, { '/', "search" }, { 'a', "whole" }, { 'b', "base" }, { 'w', "findings" },
        { '[', "previous" }, { ']', "next" }, { '+', "added" }, { '~', "modified" }, { '=', "identical" }, { '-', "deleted" },
        { 'x', "extract" }, { 'y', "copy" }, { 'o', "viewer" }, { 'q', "quit" },
    };

    [Theory]
    [MemberData(nameof(DefaultKeys))]
    public void EveryDefaultKeyDrivesItsAction(char key, string action)
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host);
        ExplorerState s = ui.State;
        if (action is "extract" or "copy" or "viewer")
        {
            ui.Window.Apply(new SelectLayer(3));
            s.Expanded.Add("app");
            ui.Window.Presenter.Invalidate();
            ui.Window.Apply(new SetCursor(RowOf(ui, "app/package.json")));
            if (action is "copy" or "viewer")
            {
                ui.Press(Key.Enter);
                Assert.Equal(RightView.Inspector, s.View);
            }
        }
        ui.Press(new Key(key));
        switch (action)
        {
            case "keys": Assert.Equal(RightView.Keys, s.View); break;
            case "insights": Assert.Equal(RightView.Insights, s.View); break;
            case "search": Assert.Equal(RightView.Search, s.View); break;
            case "whole": Assert.True(s.WholeFilesystem); break;
            case "base": Assert.Equal(1, s.Layer); break;
            case "findings": Assert.True(s.FindingsOnly); break;
            case "previous": Assert.Equal(1, s.Layer); break;
            case "next": Assert.Equal(3, s.Layer); break;
            case "added": Assert.Contains(Change.Added, s.Hidden); break;
            case "modified": Assert.Contains(Change.Modified, s.Hidden); break;
            case "identical": Assert.Contains(Change.Identical, s.Hidden); break;
            case "deleted": Assert.Contains(Change.Removed, s.Hidden); break;
            case "extract": Assert.True(ui.Window.ExtractField.Visible); break;
            case "copy": Assert.Equal("$ dredge image cat registry.test/shop/storefront:1.0 /app/package.json", s.Notice); break;
            case "viewer": ui.Until(() => ui.Window.StopRequested, "the viewer"); Assert.Equal(ExplorerExitKind.Viewer, ui.Window.Exit.Kind); break;
            case "quit": Assert.True(ui.Window.StopRequested); break;
        }
    }

    [Fact]
    public void ControlAndAltChordsDoNotTriggerActions()
    {
        using ExplorerUiHarness ui = Open(out _);
        ui.Press(Key.Q.WithCtrl);
        ui.Press(Key.I.WithAlt);
        Assert.False(ui.Window.StopRequested);
        Assert.Equal(RightView.Files, ui.State.View);
    }

    // ───────────────────────────── tour 10: narrow ─────────────────────────────

    [Fact]
    public void NarrowTerminalDropsDetailsThatDoNotFit()
    {
        using ExplorerUiHarness ui = Open(out _, width: 100, height: 40);

        string header = ui.Row(0);
        Assert.Contains("3.2 MB file payload", header);
        Assert.Contains("37% efficient", header);
        Assert.DoesNotContain("sha256:manifest", header);
        Assert.DoesNotContain("download", header);
        AssertShows(ui, "▲ 2 findings involve layer 2, 2.0 MB · press i", "RUN apt-get update && apt-get install -y tool");
        AssertHides(ui, "uid:gid", "-rw-r--r--", "── registry.test/base:1");
    }

    [Fact]
    public void NoticesFromTheAppShowAsErrors()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(),
            new ExplorerState { Layer = 2, Notice = "Could not open linux/arm64: denied", NoticeIsError = true },
            session => new FakeExplorerHost { Baseline = session }, out _);

        AssertShows(ui, "Could not open linux/arm64: denied");
        ui.Press(Key.CursorDown);
        Assert.Null(ui.State.Notice);
        AssertHides(ui, "Could not open linux/arm64");
    }
}

// Theme is process-wide, so these share the UI collection and restore the default.
[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerThemeTests : IDisposable
{
    public void Dispose() => Theme.Apply(ThemeKind.Dark);

    [Fact]
    public void NoColorUsesTheTerminalsColorsAndReverseVideoForSelection()
    {
        Theme.Apply(ThemeKind.NoColor);

        Terminal.Gui.Drawing.Attribute plain = Theme.S(Theme.Foam, null, Deco.Bold).ToAttribute();
        Assert.Equal(Terminal.Gui.Drawing.Color.None, plain.Foreground);
        Assert.Equal(Terminal.Gui.Drawing.Color.None, plain.Background);
        Assert.True(plain.Style.HasFlag(Terminal.Gui.Drawing.TextStyle.Bold));
        Assert.False(plain.Style.HasFlag(Terminal.Gui.Drawing.TextStyle.Reverse));

        foreach (Rgb selection in new[] { Theme.ChannelDeep, Theme.KeycapBg, Theme.Channel })
        {
            Assert.True(Theme.S(Theme.Foam, selection).ToAttribute().Style.HasFlag(Terminal.Gui.Drawing.TextStyle.Reverse));
        }
    }

    [Fact]
    public void NoColorEnvironmentWinsOverTheSetting()
    {
        Assert.Equal(ThemeKind.NoColor, Theme.Parse("light", noColorEnvironment: true));
        Assert.Equal(ThemeKind.Light, Theme.Parse("light", noColorEnvironment: false));
    }

    [Fact]
    public void LightAndDarkPalettesDiffer()
    {
        Theme.Apply(ThemeKind.Dark);
        (Rgb ground, Rgb foam) = (Theme.Ground, Theme.Foam);
        Terminal.Gui.Drawing.Attribute dark = Theme.S(Theme.Foam).ToAttribute();
        Theme.Apply(ThemeKind.Light);

        Assert.NotEqual(ground, Theme.Ground);
        Assert.NotEqual(foam, Theme.Foam);
        Assert.NotEqual(dark, Theme.S(Theme.Foam).ToAttribute());
    }

    [Fact]
    public void SizeTextIsReadableOnEveryStratumColour()
    {
        foreach (ThemeKind kind in new[] { ThemeKind.Dark, ThemeKind.Light })
        {
            Theme.Apply(kind);
            foreach (Rgb bg in new[] { Theme.Bedrock1, Theme.Bedrock2, Theme.Sand1, Theme.Sand2, Theme.StratumWaste })
            {
                Assert.True(ContrastRatio(Theme.Foam, bg) >= 4.5, $"{bg} against {Theme.Foam} in {kind}");
            }
        }
    }

    [Fact]
    public void AdjacentStrataAndWasteRemainDistinctInEveryTheme()
    {
        foreach (ThemeKind kind in new[] { ThemeKind.Dark, ThemeKind.Light })
        {
            Theme.Apply(kind);
            Assert.True(LabDistance(Theme.Bedrock1, Theme.Bedrock2) >= 9, $"Base shades in {kind}");
            Assert.True(LabDistance(Theme.Sand1, Theme.Sand2) >= 9, $"App shades in {kind}");
            foreach (Rgb baseColor in new[] { Theme.Bedrock1, Theme.Bedrock2 })
            {
                foreach (Rgb appColor in new[] { Theme.Sand1, Theme.Sand2 })
                {
                    Assert.True(LabDistance(baseColor, appColor) >= 25, $"Base and app in {kind}");
                }
            }
            foreach (Rgb appColor in new[] { Theme.Sand1, Theme.Sand2 })
            {
                Assert.True(LabDistance(appColor, Theme.StratumWaste) >= 20, $"App and waste in {kind}");
            }
        }
    }

    private static double LabDistance(Rgb a, Rgb b)
    {
        static (double L, double A, double B) Lab(Rgb rgb)
        {
            static double Linear(int value)
            {
                double channel = value / 255.0;
                return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
            }
            static double Curve(double value) =>
                value > 0.008856 ? Math.Cbrt(value) : 7.787 * value + 16.0 / 116;

            double r = Linear(rgb.R), g = Linear(rgb.G), b = Linear(rgb.B);
            double x = Curve((0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047);
            double y = Curve(0.2126 * r + 0.7152 * g + 0.0722 * b);
            double z = Curve((0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883);
            return (116 * y - 16, 500 * (x - y), 200 * (y - z));
        }

        (double l1, double a1, double b1) = Lab(a);
        (double l2, double a2, double b2) = Lab(b);
        return Math.Sqrt(Math.Pow(l1 - l2, 2) + Math.Pow(a1 - a2, 2) + Math.Pow(b1 - b2, 2));
    }

    private static double ContrastRatio(Rgb a, Rgb b)
    {
        static double Lum(Rgb c)
        {
            static double Ch(int v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void InformationalTextAndSelectedSecondaryTextHaveReadableContrast()
    {
        foreach (ThemeKind kind in new[] { ThemeKind.Dark, ThemeKind.Light })
        {
            Theme.Apply(kind);
            Assert.True(ContrastRatio(Theme.Silt, Theme.Ground) >= 4.5);
            Assert.True(ContrastRatio(Theme.Silt, Theme.Graphite) >= 4.5);
            foreach (Line line in new[]
            {
                Line.Of("secondary", Theme.Silt).WithBackground(Theme.ChannelDeep),
                Line.Of("secondary", Theme.Silt).UnderBackground(Theme.ChannelDeep),
            })
            {
                Sty style = Assert.Single(line.Parts).Sty;
                Assert.Equal(Theme.Foam, style.Foreground);
                Assert.True(ContrastRatio(style.Foreground!.Value, style.Background!.Value) >= 4.5);
            }
            ExplorerPresenter presenter = new(ExplorerSamples.Image(), 150, 42);
            Line header = presenter.FilesPane(new ExplorerState { Layer = 2 }).Lines
                .Single(line => line.ToString().Contains("uid:gid", StringComparison.Ordinal));
            Assert.All(header.Parts.Where(part => !string.IsNullOrWhiteSpace(part.Text)),
                part => Assert.Equal(Theme.Silt, part.Sty.Foreground));
        }
    }

    [Fact]
    public void ExplorerRendersInEveryTheme()
    {
        foreach (ThemeKind kind in Enum.GetValues<ThemeKind>())
        {
            Theme.Apply(kind);
            using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
            Assert.True(ui.Shows("COPY . /app"), $"{kind}{Environment.NewLine}{ui.Screen()}");
            ui.Press(new Key('i'));
            Assert.True(ui.Shows("Insights  3 findings"), $"{kind}{Environment.NewLine}{ui.Screen()}");
        }
    }
}
