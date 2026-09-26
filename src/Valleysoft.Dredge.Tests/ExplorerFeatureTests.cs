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
    public void HeaderSummarizesTheImage()
    {
        using ExplorerUiHarness ui = Open(out _);

        string header = ui.Row(0);
        Assert.Contains("registry.test/shop/storefront:1.0", header);
        Assert.Contains("linux/amd64", header);
        Assert.Contains("sha256:manifest", header);
        Assert.Contains("3.2 MB on disk", header);
        Assert.Contains("1.7 KB download", header);
        Assert.Contains("37% efficient", header);
        // The strip under the core bar names the base and the user's layers.
        Assert.Contains("storefront", ui.Row(2));
    }

    [Fact]
    public void LayerListGroupsBaseAndUserLayersWithSizes()
    {
        using ExplorerUiHarness ui = Open(out _);

        AssertShows(ui, "Layers  4 with files", "── registry.test/base:1", "── storefront");
        string selected = ui.Row(ui.Find("COPY . /app").Y);
        Assert.Contains("▌ 2 ", selected);
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

        AssertShows(ui, "Layer 2 ", "2.0 MB on disk   900 B download", "+ 4 added", "sha256:l2",
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

        // Space folds everything below the selection's folder.
        s.Expanded.Add("app/cache");
        ui.Window.Presenter.Invalidate();
        ui.Window.Apply(new SetCursor(RowOf(ui, "app/src/index.js")));
        ui.Press(Key.Space);
        Assert.DoesNotContain("app/src", s.Expanded);
        Assert.Contains("app/cache", s.Expanded);
        Assert.Equal(RowOf(ui, "app/src"), s.Cursor);
        ui.Window.Apply(new SetCursor(RowOf(ui, "app")));
        ui.Press(Key.Space);
        Assert.Empty(s.Expanded);
        Assert.Equal(0, s.Cursor);
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
        foreach (string hint in new[] { "Tab  Layers", "↑↓  Move", "[ ]  Step layer", "a  Whole filesystem", "+ ~ = -  Filter",
            "Enter  Inspect", "/  Search", "i  Insights", "?  Keys", "q  Quit" })
        {
            Assert.Contains(hint, footer);
        }

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
            ui.Press(new Key('b'));
            Assert.Equal(1, ui.State.Layer);
            Assert.Equal("No verified base image, so every layer is shown as yours.", ui.State.Notice);
            AssertShows(ui, "No verified base image");
        }

        using (ExplorerUiHarness ui = OpenCustom(Custom([Layer([File("a", 1, "a")]), Layer([File("b", 1, "b")])], baseLayerCount: 2), out _))
        {
            ui.Press(new Key('b'));
            Assert.Equal("Every layer is part of the base image.", ui.State.Notice);
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
        ui.Press(new Key('o'));
        Assert.Equal("The pager works once every layer is indexed.", s.Notice);
        AssertShows(ui, "The pager works once every layer is indexed.");

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

        AssertShows(ui, "Insights  3 findings", "2.0 MB hidden bytes · 37% efficient", "1.2 MB more potential savings",
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

        // [ and ] leave the inspector for the tree of the next layer.
        ui.Press(new Key(']'));
        Assert.Equal(RightView.Files, s.View);
        Assert.Equal(3, s.Layer);
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
    public void PagerAndExtractNeedALiveFile()
    {
        using ExplorerUiHarness ui = Open(out _);
        ExplorerState s = ui.State;
        ui.Window.Apply(new SetCursor(RowOf(ui, "app")));
        ui.Press(new Key('o'));
        Assert.Equal("Select a file to open in the pager.", s.Notice);
        s.Expanded.Add("app/cache");
        ui.Window.Presenter.Invalidate();
        ui.Window.Apply(new SetCursor(RowOf(ui, "app/cache/big.bin")));
        ui.Press(new Key('o'));
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
        Assert.Contains("−2.0 MB on disk", header);
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
        foreach (string hint in new[] { "[ ]  Next difference", "s  Swap sides", "Enter  Diff a file", "c  Change tag…", "Esc  Leave compare" })
        {
            Assert.Contains(hint, Footer(ui));
        }
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
        ui.Press(Key.Space);
        Assert.DoesNotContain(Rows(), row => row.Path == "app/x");
        Assert.Equal(0, c.Cursor);
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
        // Any view key closes the key list and returns to the comparison rather than leaving it.
        ui.Press(new Key('i'));
        Assert.Equal(RightView.Files, s.View);
        Assert.NotNull(s.Compare);
        AssertShows(ui, "Differences  1.0 → 2.0");
        ui.Press(new Key('?'));
        ui.Press(Key.Tab);
        Assert.Equal(RightView.Keys, s.View);
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Files, s.View);
        Assert.NotNull(s.Compare);

        foreach (char key in "ia/+x")
        {
            ui.Press(new Key(key));
            Assert.Equal("Press Esc to leave compare first.", s.Notice);
        }
        Assert.NotNull(s.Compare);

        // c picks another tag without leaving compare.
        ui.InDialog(() => ui.Press(new Key('c')),
            DialogStep.When("the tags", () => ui.Shows("2 tags."), () => ui.Send(Key.Enter)));
        ui.Until(() => host.Compared.Count == 2 && s.Compare is { Busy: false }, "the second comparison");
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
    public void SwapAndDifferenceStepsOnlyWorkInCompare()
    {
        using ExplorerUiHarness ui = Open(out _);
        ui.Press(new Key('s'));
        Assert.Equal("Swap and difference stepping work in the compare view.", ui.State.Notice);
    }

    // ───────────────────────────── tour 9: keys ─────────────────────────────

    [Fact]
    public void HelpListsEveryGroupAndMarker()
    {
        using ExplorerUiHarness ui = Open(out _);
        ui.Press(new Key('?'));

        AssertShows(ui, "Move", "Views", "Layers", "Search", "Files", "Actions", "Compare", "Change markers", "Tips",
            "Toggle whole filesystem", "First layer after base", "Compare with a tag…", "Choose platform…",
            "Retry a failed layer", "Fold everything below", "Only paths with findings", "Swap sides");
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Files, ui.State.View);
    }

    public static TheoryData<char, string> DefaultKeys() => new()
    {
        { '?', "keys" }, { 'i', "insights" }, { '/', "search" }, { 'a', "whole" }, { 'b', "base" }, { 'w', "findings" },
        { '[', "previous" }, { ']', "next" }, { '+', "added" }, { '~', "modified" }, { '=', "identical" }, { '-', "deleted" },
        { 'p', "platform" }, { 'x', "extract" }, { 'y', "copy" }, { 'o', "pager" }, { 's', "swap" }, { 'r', "retry" }, { 'q', "quit" },
    };

    [Theory]
    [MemberData(nameof(DefaultKeys))]
    public void EveryDefaultKeyDrivesItsAction(char key, string action)
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host);
        ExplorerState s = ui.State;
        if (action is "extract" or "copy" or "pager")
        {
            ui.Window.Apply(new SelectLayer(3));
            s.Expanded.Add("app");
            ui.Window.Presenter.Invalidate();
            ui.Window.Apply(new SetCursor(RowOf(ui, "app/package.json")));
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
            case "platform": Assert.Equal("This image has only one platform.", s.Notice); break;
            case "extract": Assert.True(ui.Window.ExtractField.Visible); break;
            case "copy": Assert.Equal("$ dredge image cat registry.test/shop/storefront:1.0 /app/package.json", s.Notice); break;
            case "pager": ui.Until(() => ui.Window.StopRequested, "the pager"); Assert.Equal(ExplorerExitKind.Pager, ui.Window.Exit.Kind); break;
            case "swap": Assert.Equal("Swap and difference stepping work in the compare view.", s.Notice); break;
            case "retry": Assert.Equal("Only a failed layer can be retried.", s.Notice); break;
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
        Assert.Contains("3.2 MB on disk", header);
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
