using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Valleysoft.Dredge.Explorer.Tui;

// Composes the explorer from custom panes and stock Terminal.Gui widgets. It is
// also the controller: every key, click, and footer shortcut becomes a Cmd, and
// Apply is the only method that changes ExplorerState.
internal sealed class ExplorerWindow : Window
{
    private readonly ExplorerImage img;
    private readonly IExplorerHost host;
    private readonly ExplorerPresenter ex;
    private readonly ExplorerState s;
    private readonly CancellationToken lifetime;
    private CancellationTokenRegistration lifetimeRegistration;
    private bool lifetimeRegistered;
    private bool windowDisposed;
    private readonly Action<string>? openWindowedViewer;
    private readonly HeaderView header;
    private readonly PaneView layers, details, right;
    private readonly Label searchKey, matches, extractLabel;
    private readonly TextField search, extractField;
    private readonly TextField commandText;
    private readonly FooterView footer;
    private int footerLeft;
    private bool? narrow;
    private bool? tooSmall;
    private bool? inspecting;
    private bool focusSynced;
    private CompareView? compareView;
    private Cell[,]? shown;
    private IDriver? watched;
    private bool clearedThisFrame;
    private CancellationTokenSource? previewLoad;
    private int compareGeneration;
    private CancellationTokenSource? compareLoad;
    private (RightView View, FocusPane Focus, bool CompareLayers)? helpReturn;
    private (RightView View, FocusPane Focus, bool CompareLayers)? commandReturn;

    public ExplorerWindow(ExplorerImage img, ExplorerState state, IExplorerHost host, CancellationToken lifetime,
        Action<string>? openWindowedViewer = null)
    {
        this.img = img;
        this.host = host;
        this.lifetime = lifetime;
        this.openWindowedViewer = openWindowedViewer;
        s = state;
        if (s.Notice is null && img.BaseWarning is not null)
        {
            s.Notice = img.BaseWarning;
            s.NoticeIsError = true;
        }
        ex = new ExplorerPresenter(img, 150, 42, host.Keys) { Copies = host.ClipboardEnabled, MultiPlatform = host.Platforms.Count > 1 };
        BorderStyle = LineStyle.None;
        SetScheme(new Scheme(Paint.Attr(Theme.Foam)));

        header = new HeaderView(HeaderLines, col => Comparing || ex.FullWidthContent || ex.TooSmall
            ? null : ex.LayerAtColumn(col))
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = ExplorerPresenter.HeaderHeight,
        };
        layers = new PaneView(() => Comparing ? Compare.Layers() : ex.LayersPane(s), focusable: true) { KeepsFocusAway = Typing, BufferIntact = BufferIntact };
        details = new PaneView(() => Comparing ? Compare.Details() : ex.DetailsPane(s), focusable: false) { BufferIntact = BufferIntact };
        right = new PaneView(() => Comparing && s.View is not (RightView.Keys or RightView.Command) ? Compare.Diff() : ex.RightPane(s), focusable: true) { ActivateOnDoubleClick = true, KeepsFocusAway = Typing, BufferIntact = BufferIntact };
        header.Command += Apply;
        layers.Command += Apply;
        details.Command += Apply;
        right.Command += Apply;

        layers.HasFocusChanged += (_, _) => FocusMoved(layers, FocusPane.Layers);
        right.HasFocusChanged += (_, _) => FocusMoved(right, FocusPane.Right);

        Terminal.Gui.Drawing.Attribute field = Paint.Attr(Theme.Foam, Theme.Graphite, Deco.Bold);
        Scheme fieldScheme = new(field) { Focus = field, Editable = field, Active = field, HotNormal = field, HotFocus = field };

        searchKey = new Label { X = 1, Y = Pos.AnchorEnd(1), Text = $" {host.Keys.Label(KeyAction.Search)} ", Visible = false };
        searchKey.SetScheme(new Scheme(Paint.Attr(Theme.Foam, Theme.KeycapBg, Deco.Bold)));
        search = new TextField { X = 5, Y = Pos.AnchorEnd(1), Width = 26, Text = s.SearchQuery, Visible = false };
        search.SetScheme(fieldScheme);
        search.TextChanged += (_, _) => Apply(new SetQuery(search.Text));
        search.Accepting += (_, e) =>
        {
            Apply(new Activate());
            e.Handled = true;
        };
        matches = new Label { X = Pos.Right(search) + 2, Y = Pos.AnchorEnd(1), Width = 14, Visible = false };
        matches.SetScheme(new Scheme(Paint.Attr(Theme.Silt)));

        extractLabel = new Label { X = 1, Y = Pos.AnchorEnd(1), Text = " Extract to ", Visible = false };
        extractLabel.SetScheme(new Scheme(Paint.Attr(Theme.Foam, Theme.KeycapBg, Deco.Bold)));
        extractField = new TextField { X = 13, Y = Pos.AnchorEnd(1), Width = Dim.Fill(20), Visible = false };
        extractField.SetScheme(fieldScheme);
        extractField.Accepting += (_, e) =>
        {
            e.Handled = true;
            FinishExtract(extractField.Text);
        };

        footer = new FooterView { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        footer.Command += Apply;

        commandText = new TextField
        {
            X = 2, Y = ExplorerPresenter.HeaderHeight + 2, Width = Dim.Fill(2),
            ReadOnly = true, Visible = false,
        };
        commandText.SetScheme(Dialogs.Input());
        Add(header, layers, details, right, searchKey, search, matches, extractLabel, extractField, commandText, footer);
        FrameChanged += (_, _) => Relayout();
        Relayout();
    }

    public ExplorerState State => s;
    public ExplorerPresenter Presenter => ex;
    public PaneView Layers => layers;
    public PaneView Right => right;
    public TextField Search => search;
    public TextField ExtractField => extractField;
    internal TextField CommandText => commandText;
    public ExplorerExit Exit { get; private set; } = new(ExplorerExitKind.Quit);
    internal void ViewerFailed(string error) => Notice(error, error: true);
    private bool Comparing => s.Compare is not null;
    private CompareView Compare => compareView ??= new CompareView(ex, s.Compare!);
    private string? pendingExtract;

    private List<Line> HeaderLines() =>
        ex.TooSmall ? ex.TooSmallMessage() : Comparing ? Compare.Header() : ex.Header(s);

    private bool Searching => Comparing ? s.Compare!.Searching && s.View != RightView.Keys : s.View == RightView.Search;
    private bool Typing() => pendingExtract is not null || Searching;

    // Puts Terminal.Gui focus where the state says it is.
    public void SyncFocus()
    {
        if (!lifetimeRegistered && App is IApplication application)
        {
            lifetimeRegistered = true;
            lifetimeRegistration = lifetime.Register(() => application.Invoke(() =>
            {
                if (!windowDisposed)
                {
                    focusSynced = false;
                    application.RequestStop();
                    RequestStop();
                }
            }));
        }
        focusSynced = true;
        if (s.View == RightView.Command)
        {
            commandText.SetFocus();
        }
        else if (pendingExtract is not null)
        {
            extractField.SetFocus();
        }
        else if (Searching)
        {
            search.SetFocus();
        }
        else if (Comparing ? s.Compare!.FocusLayers : s.Focus == FocusPane.Layers)
        {
            layers.SetFocus();
        }
        else
        {
            right.SetFocus();
        }
        EnsurePreview();
    }

    // Tearing down the screen moves Terminal.Gui focus; the state outlives this
    // window (the viewer reopens it), so stop mirroring focus into it first.
    private void Stop(ExplorerExit exit)
    {
        focusSynced = false;
        Exit = exit;
        App?.RequestStop();
    }

    private void FocusMoved(PaneView pane, FocusPane which)
    {
        if (!focusSynced || !pane.HasFocus)
        {
            return;
        }
        if (Comparing)
        {
            s.Compare!.FocusLayers = which == FocusPane.Layers;
            Refresh();
        }
        else if (s.Focus != which)
        {
            s.Focus = which;
            Refresh();
        }
    }

    // Called by the app when indexing, analysis, or the session changed the image.
    public void ImageChanged()
    {
        ex.Invalidate();
        EnsurePreview();
        Refresh();
    }

    public void Tick()
    {
        s.Spinner++;
        Refresh();
    }

    // Terminal.Gui marks every cell a view paints as dirty and sends all of them to
    // the terminal, so moving a cursor re-sends the whole pane in 24-bit colour.
    // Cells identical to what the terminal already shows are marked clean here,
    // after this window has drawn and before the frame is flushed.
    // The window blanks its whole area before its panes draw, so they must repaint in full.
    protected override bool OnClearingViewport()
    {
        clearedThisFrame = true;
        return base.OnClearingViewport();
    }

    protected override void OnDrawComplete(DrawContext? context)
    {
        base.OnDrawComplete(context);
        clearedThisFrame = false;
        IDriver? driver = App?.Driver;
        if (driver != watched)
        {
            if (watched is not null)
            {
                watched.ClearedContents -= ForgetShown;
            }
            watched = driver;
            shown = null;
            if (driver is not null)
            {
                driver.ClearedContents += ForgetShown;
            }
        }
        Cell[,]? cells = driver?.Contents;
        // Dialogs and popovers draw over this window after it, so the frame isn't only ours.
        if (cells is null || App!.TopRunnableView != this || App.Popovers?.GetActivePopover() is View { Visible: true })
        {
            shown = null;
            return;
        }
        int rows = cells.GetLength(0), cols = cells.GetLength(1);
        bool[]? dirtyLines = driver!.GetOutputBuffer()?.DirtyLines;
        if (shown is not null && shown.GetLength(0) == rows && shown.GetLength(1) == cols)
        {
            for (int r = 0; r < rows; r++)
            {
                int last = -1;
                for (int c = cols - 1; c >= 0; c--)
                {
                    ref Cell cell = ref cells[r, c];
                    if (cell.IsDirty && (cell.Grapheme != shown[r, c].Grapheme || cell.Attribute != shown[r, c].Attribute))
                    {
                        last = c;
                        break;
                    }
                }
                // Every cursor move and every run of dirty cells is a separate write
                // to the console, so a changed row is sent as one run from its first
                // column. Clean cells already match the screen, so resending them is harmless.
                for (int c = 0; c < cols; c++)
                {
                    cells[r, c].IsDirty = c <= last;
                }
                if (last < 0 && dirtyLines is not null && r < dirtyLines.Length)
                {
                    dirtyLines[r] = false;
                }
            }
        }
        else
        {
            shown = new Cell[rows, cols];
        }
        Array.Copy(cells, shown, cells.Length);
    }

    private void ForgetShown(object? sender, EventArgs e) => shown = null;

    // True while drawing a frame whose buffer still holds the last frame this
    // window drew, which is exactly when the shadow copy is valid.
    private bool BufferIntact()
    {
        Cell[,]? cells = App?.Driver?.Contents;
        return !clearedThisFrame && shown is not null && cells is not null && App!.Driver == watched
            && cells.GetLength(0) == shown.GetLength(0) && cells.GetLength(1) == shown.GetLength(1)
            && App.TopRunnableView == this && App.Popovers?.GetActivePopover() is not View { Visible: true };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            windowDisposed = true;
            lifetimeRegistration.Dispose();
            CancelComparison();
        }
        if (disposing && watched is not null)
        {
            watched.ClearedContents -= ForgetShown;
            watched = null;
        }
        base.Dispose(disposing);
    }

    // ───────────────────────────── layout ─────────────────────────────

    private void Relayout()
    {
        int w = Math.Max(Viewport.Width, 1), h = Math.Max(Viewport.Height, 1);
        ex.Width = w;
        ex.Height = h;
        bool inspector = UsesFullWidth();
        ex.FullWidthContent = inspector;
        compareView = null;
        if (tooSmall != ex.TooSmall)
        {
            tooSmall = ex.TooSmall;
            layers.Visible = right.Visible = !ex.TooSmall;
            header.Height = ex.TooSmall ? Dim.Fill() : ExplorerPresenter.HeaderHeight;
            narrow = null;
            inspecting = null;
        }
        if (!ex.TooSmall && (narrow != ex.Narrow || inspecting != inspector))
        {
            narrow = ex.Narrow;
            inspecting = inspector;
            layers.Visible = !inspector;
            if (inspector)
            {
                details.Visible = false;
                right.X = 0;
                right.Y = ExplorerPresenter.HeaderHeight;
            }
            else if (ex.Narrow)
            {
                layers.X = 0;
                layers.Y = ExplorerPresenter.HeaderHeight;
                layers.Width = Dim.Fill();
                layers.Height = ExplorerPresenter.NarrowLayersHeight;
                details.Visible = false;
                right.X = 0;
                right.Y = Pos.Bottom(layers);
            }
            else
            {
                layers.X = 0;
                layers.Y = ExplorerPresenter.HeaderHeight;
                layers.Width = ExplorerPresenter.LeftWidth;
                layers.Height = Dim.Fill(ExplorerPresenter.DetailsHeight + 1);
                details.Visible = true;
                details.X = 0;
                details.Y = Pos.AnchorEnd(ExplorerPresenter.DetailsHeight + 1);
                details.Width = ExplorerPresenter.LeftWidth;
                details.Height = ExplorerPresenter.DetailsHeight;
                right.X = ExplorerPresenter.LeftWidth;
                right.Y = ExplorerPresenter.HeaderHeight;
            }
            right.Width = Dim.Fill();
            right.Height = Dim.Fill(1);
        }
        if (ex.TooSmall)
        {
            details.Visible = false;
        }
        Refresh();
    }

    // Brings the footer and every pane up to date with the state; each repaints only if it changed.
    private void Refresh()
    {
        if (!ex.TooSmall && inspecting != UsesFullWidth())
        {
            Relayout();
            return;
        }
        compareView = null;
        bool extracting = pendingExtract is not null;
        commandText.Visible = s.View == RightView.Command && !ex.TooSmall;
        bool searching = Searching && !ex.TooSmall && !extracting;
        searchKey.Visible = search.Visible = matches.Visible = searching;
        extractLabel.Visible = extractField.Visible = extracting;
        if (searching)
        {
            int n = Comparing ? Compare.Rows().Count : ex.SearchResults(s).Total;
            string count = n == 1 ? "1 match" : $"{Fmt.N(n)} matches";
            if (matches.Text != count)
            {
                matches.Text = count;
            }
        }
        string? note = extracting ? "Enter extract · Esc cancel" : ex.TooSmall ? null
            : s.Notice ?? s.ComparisonStatus ?? (Comparing ? (s.Compare!.Busy ? "Loading…" : null) : ex.Status(s));
        string status = note is null ? "" : Fmt.Fit(note, Math.Max(10, Viewport.Width / 2)) + " ";
        int statusWidth = DisplayText.Width(status);

        // Searching, the field and match count own the left of the row; extracting, the prompt owns all but the note.
        int left = searching ? 46 : extracting ? Math.Max(0, Viewport.Width - statusWidth) : 0;
        if (footerLeft != left)
        {
            footerLeft = left;
            footer.X = left;
        }
        List<Hint> hints = [];
        if (!extracting)
        {
            int room = Math.Max(0, Viewport.Width - left - statusWidth - 1);
            List<Hint> source = ex.TooSmall ? [new(host.Keys.Label(KeyAction.Quit), "Quit", new Quit())] : ContextHints();
            hints = ex.Fit(source.Where(h => h.ShowInFooter).ToList(), room,
                h => DisplayText.Width(h.Key) + DisplayText.Width(h.Label) + 5);
        }
        footer.Show(hints, status, s.NoticeIsError && s.Notice is not null, Math.Max(0, Viewport.Width - left));

        header.Update();
        layers.Update();
        details.Update();
        right.Update();
    }

    // ───────────────────────────── input ─────────────────────────────

    private static bool IsTab(Key key) => key.NoShift.KeyCode == KeyCode.Tab && !key.IsCtrl && !key.IsAlt;

    private List<Hint> ContextHints()
    {
        List<Hint> hints = Comparing && s.View is not (RightView.Keys or RightView.Command) ? Compare.Hints() : ex.Hints(s);
        if (ex.FullWidthContent)
        {
            hints = hints.Where(h => h.Key != "Tab").ToList();
        }
        return compareLoad is null ? hints
            : [new("Esc", "Cancel comparison", new Back()), .. hints.Where(h => h.Key != "Esc")];
    }

    private bool UsesFullWidth() => s.View is RightView.Keys or RightView.Command || s.Compare?.Diff is not null ||
        !Comparing && (s.View == RightView.Inspector || ex.Narrow && s.View is RightView.Search or RightView.Insights);

    // A key acts only when the current context lists it; hints trimmed from a narrow footer still count.
    private bool Listed(Key key)
    {
        string? token = IsTab(key) ? "Tab" : key.KeyCode switch
        {
            KeyCode.CursorUp or KeyCode.CursorDown => "↑↓",
            KeyCode.CursorLeft or KeyCode.CursorRight => "←→",
            KeyCode.PageUp => "PgUp",
            KeyCode.PageDown => "PgDn",
            KeyCode.Home => "Home",
            KeyCode.End => "End",
            KeyCode.Enter => "Enter",
            KeyCode.Esc => "Esc",
            _ when key.IsAlt && !key.IsCtrl => "Alt+" + key.NoAlt.KeyCode,
            _ when key.AsRune.Value is int ch and > 32 and < 127 => ((char)ch).ToString(),
            _ => null,
        };
        return token is not null && ContextHints().Any(h => h.Key.Split(' ').Contains(token));
    }

    protected override bool OnKeyDown(Key key)
    {
        if (commandText.HasFocus)
        {
            if (IsTab(key))
            {
                return true;
            }
            if (key == Key.Esc)
            {
                Apply(new Back());
                return true;
            }
            if (!key.IsCtrl && !key.IsAlt && host.Keys.Lookup((char)key.AsRune.Value) is KeyAction commandAction)
            {
                if (commandAction is KeyAction.Help or KeyAction.Quit)
                {
                    Apply(commandAction == KeyAction.Help ? new ShowView(RightView.Keys) : new Quit());
                    return true;
                }
            }
            return false;
        }
        if (extractField.HasFocus || pendingExtract is not null)
        {
            if (key.KeyCode == KeyCode.Esc)
            {
                CancelExtract();
                return true;
            }
            return false;
        }
        if (search.HasFocus)
        {
            Cmd? inField = key.KeyCode switch
            {
                KeyCode.Esc => new Back(),
                KeyCode.CursorUp => new Move(-1),
                KeyCode.CursorDown => new Move(1),
                KeyCode.PageUp => new Move(-ex.SearchRows),
                KeyCode.PageDown => new Move(ex.SearchRows),
                _ => null,
            };
            if (inField is null && key.IsAlt && !key.IsCtrl)
            {
                inField = key.NoAlt.KeyCode switch
                {
                    KeyCode.L => new SetSearchScope(!s.SearchLayerOnly),
                    KeyCode.D => new ToggleIncludeDeleted(),
                    KeyCode.C => new ToggleExactCase(),
                    _ => null,
                };
            }
            if (inField is null)
            {
                return false;
            }
            if (Listed(key))
            {
                Apply(inField);
            }
            return true;
        }

        if (IsTab(key) && !ex.TooSmall && !Listed(key))
        {
            return true;
        }
        int page = Comparing ? Compare.DiffRows : ex.TreeRows;
        Cmd? cmd = key.KeyCode switch
        {
            KeyCode.Esc => new Back(),
            KeyCode.Enter => new Activate(),
            KeyCode.CursorUp => new Move(-1),
            KeyCode.CursorDown => new Move(1),
            KeyCode.PageUp => new Move(-page),
            KeyCode.PageDown => new Move(page),
            KeyCode.Home => new Jump(false),
            KeyCode.End => new Jump(true),
            KeyCode.CursorLeft => s.View == RightView.Inspector || s.Compare?.Diff is not null ? new PanText(-8) : new Fold(false),
            KeyCode.CursorRight => s.View == RightView.Inspector || s.Compare?.Diff is not null ? new PanText(8) : new Fold(true),
            _ => null,
        };
        if (ex.TooSmall && cmd is not null)
        {
            return true;
        }
        if (cmd is null && !key.IsCtrl && !key.IsAlt && key.AsRune.Value is int ch and > 32 and < 127
            && host.Keys.Lookup((char)ch) is KeyAction action)
        {
            if (ex.TooSmall && action != KeyAction.Quit)
            {
                return true;
            }
            cmd = action switch
            {
                KeyAction.Quit => new Quit(),
                KeyAction.Help => new ShowView(RightView.Keys),
                KeyAction.Insights => new ShowView(RightView.Insights),
                KeyAction.Search => new ShowView(RightView.Search),
                KeyAction.WholeFilesystem => new SetWhole(!s.WholeFilesystem),
                KeyAction.FirstUserLayer => new FirstUserLayer(),
                KeyAction.Compare => new PickTag(),
                KeyAction.FindingsOnly => new ToggleFindingsOnly(),
                KeyAction.PreviousLayer => Comparing ? new StepDifference(-1) : new StepLayer(-1),
                KeyAction.NextLayer => Comparing ? new StepDifference(1) : new StepLayer(1),
                KeyAction.ToggleAdded => new ToggleChange(Change.Added),
                KeyAction.ToggleModified => new ToggleChange(Change.Modified),
                KeyAction.ToggleIdentical => new ToggleChange(Change.Identical),
                KeyAction.ToggleDeleted => new ToggleChange(Change.Removed),
                KeyAction.Platform => new PickPlatform(),
                KeyAction.Extract => new ExtractSelected(),
                KeyAction.CopyCommand => new CopyCommand(),
                KeyAction.Viewer => new OpenInViewer(),
                KeyAction.SwapSides => new SwapSides(),
                KeyAction.Retry => new RetryLayer(s.Layer),
                _ => null,
            };
        }
        if (cmd is null)
        {
            return false;
        }
        if (Listed(key))
        {
            Apply(cmd);
        }
        return true;
    }

    // ───────────────────────────── state ─────────────────────────────

    private void Notice(string text, bool error = false)
    {
        s.Notice = text;
        s.NoticeIsError = error;
    }

    private void SelectLayerCore(int layer)
    {
        if (img.LayerCount == 0)
        {
            Notice("This image has no layers.");
            return;
        }
        s.Layer = Math.Clamp(layer, 0, img.LayerCount - 1);
        s.Cursor = s.Scroll = 0;
        s.PreviewScroll = 0;
        if (!img.IsIndexed(s.Layer))
        {
            host.Prioritize(s.Layer);
        }
        if (s.View == RightView.Inspector)
        {
            s.View = RightView.Files;
        }
        ex.Invalidate();
    }

    public void Apply(Cmd cmd)
    {
        if (cmd is not (Notify or Redraw))
        {
            s.Notice = null;
            s.NoticeIsError = false;
            ex.Invalidate();
        }
        if (cmd is ShowView { View: RightView.Keys } && s.View != RightView.Keys)
        {
            helpReturn = (s.View, s.Focus, s.Compare?.FocusLayers ?? false);
        }
        if (cmd is Back && s.View == RightView.Keys)
        {
            (RightView View, FocusPane Focus, bool CompareLayers) previous =
                helpReturn ?? (RightView.Files, FocusPane.Right, false);
            s.View = previous.View;
            s.Focus = previous.Focus;
            if (s.Compare is not null)
            {
                s.Compare.FocusLayers = previous.CompareLayers;
            }
            helpReturn = null;
            Relayout();
            SyncFocus();
            Refresh();
            return;
        }
        if (cmd is Back && s.View == RightView.Command)
        {
            (RightView View, FocusPane Focus, bool CompareLayers) previous =
                commandReturn ?? (RightView.Files, FocusPane.Right, false);
            s.View = previous.View;
            s.Focus = previous.Focus;
            if (s.Compare is not null)
            {
                s.Compare.FocusLayers = previous.CompareLayers;
            }
            commandReturn = null;
            Relayout();
            SyncFocus();
            Refresh();
            return;
        }
        if (cmd is Back && compareLoad is not null)
        {
            CancelComparison();
            Notice("Comparison canceled.");
            Refresh();
            return;
        }
        if (Comparing && ApplyCompare(cmd))
        {
            Refresh();
            return;
        }
        List<FlatRow> rows = s.View == RightView.Files && img.IsIndexed(s.Layer) ? ex.Flatten(s) : [];
        FlatRow? row = s.Cursor >= 0 && s.Cursor < rows.Count ? rows[s.Cursor] : null;
        int findingCount = ex.VisibleFindings(s).Count;

        switch (cmd)
        {
            case Quit:
                Stop(new(ExplorerExitKind.Quit));
                return;
            case Redraw:
                break;
            case Back when s.View is RightView.Files && (s.FindingsOnly || s.Hidden.Count > 0):
                s.FindingsOnly = false;
                s.Hidden.Clear();
                s.Cursor = s.Scroll = 0;
                break;
            case Back:
                s.View = RightView.Files;
                right.SetFocus();
                break;
            case ShowView v:
                s.View = v.View;
                if (v.View == RightView.Search && !Comparing)
                {
                    search.Text = s.SearchQuery;
                    search.Visible = true;
                    search.SetFocus();
                }
                else
                {
                    right.SetFocus();
                }
                break;
            case SelectLayer l:
                SelectLayerCore(l.Layer);
                break;
            case StepLayer d when s.View != RightView.Inspector:
                SelectLayerCore(s.Layer + d.Delta);
                break;
            case FirstUserLayer when img.BaseLayerCount is not null && img.FirstUserLayer is int first:
                SelectLayerCore(first);
                break;
            case FirstUserLayer:
                Notice(img.BaseLayerCount is null ? "No verified base image, so every layer is shown as yours." : "Every layer is part of the base image.");
                break;
            case RetryLayer r when r.Layer >= 0 && r.Layer < img.LayerCount && img.States[r.Layer] == ExplorerLayerState.Failed:
                host.Retry(r.Layer);
                img.States[r.Layer] = ExplorerLayerState.Waiting;
                img.Errors[r.Layer] = null;
                Notice($"Retrying layer {r.Layer}…");
                break;
            case RetryLayer:
                Notice("Only a failed layer can be retried.");
                break;
            case Move m when s.Focus == FocusPane.Layers && s.View is RightView.Files or RightView.Insights:
                SelectLayerCore(s.Layer + Math.Sign(m.Delta));
                break;
            case Move m when s.View == RightView.Insights:
                s.Finding = Math.Clamp(s.Finding + Math.Sign(m.Delta), 0, Math.Max(0, findingCount - 1));
                break;
            case Move m when s.View == RightView.Search:
                s.SearchCursor = Math.Clamp(s.SearchCursor + m.Delta, 0, Math.Max(0, ex.SearchResults(s).Hits.Count - 1));
                break;
            case Move m when s.View == RightView.Inspector:
                s.PreviewScroll = Math.Max(0, s.PreviewScroll + m.Delta);
                break;
            case PanText pan when s.View == RightView.Inspector:
                s.PreviewColumn = Math.Max(0, s.PreviewColumn + pan.Delta);
                break;
            case Move m when s.View == RightView.Keys:
                s.KeysScroll = Math.Max(0, s.KeysScroll + m.Delta);
                break;
            case Move m when s.View == RightView.Files:
                s.Cursor = Math.Clamp(s.Cursor + m.Delta, 0, Math.Max(0, rows.Count - 1));
                break;
            case Jump j when s.Focus == FocusPane.Layers && s.View is RightView.Files or RightView.Insights:
                SelectLayerCore(j.ToEnd ? img.LayerCount - 1 : 0);
                break;
            case Jump j when s.View == RightView.Insights:
                s.Finding = j.ToEnd ? Math.Max(0, findingCount - 1) : 0;
                break;
            case Jump j when s.View == RightView.Search:
                s.SearchCursor = j.ToEnd ? Math.Max(0, ex.SearchResults(s).Hits.Count - 1) : 0;
                break;
            case Jump j when s.View == RightView.Inspector:
                s.PreviewScroll = j.ToEnd ? int.MaxValue : 0;
                break;
            case Jump j when s.View == RightView.Keys:
                s.KeysScroll = j.ToEnd ? int.MaxValue : 0;
                break;
            case Jump j when s.View == RightView.Files:
                s.Cursor = j.ToEnd ? Math.Max(0, rows.Count - 1) : 0;
                break;
            case SetCursor c:
                s.Cursor = Math.Clamp(c.Row, 0, Math.Max(0, rows.Count - 1));
                break;
            case Activate when s.View == RightView.Insights:
                ActivateFinding();
                break;
            case Activate when s.View == RightView.Search:
                OpenSearchHit();
                break;
            case Activate when row is not null && row.Node.Kind == Kind.Dir && row.Node.Children.Count > 0:
                if (!s.Expanded.Remove(row.Path))
                {
                    s.Expanded.Add(row.Path);
                }
                break;
            case Activate when row?.Node.Kind is Kind.File or Kind.Link:
                s.InspectPath = row.Path;
                s.View = RightView.Inspector;
                s.PreviewScroll = 0;
                s.PreviewColumn = 0;
                s.Preview = null;
                EnsurePreview();
                break;
            case Fold f when row is not null && s.View == RightView.Files:
                if (f.Open && row.Expandable)
                {
                    s.Expanded.Add(row.Path);
                }
                else if (!f.Open && row.Expanded)
                {
                    s.Expanded.Remove(row.Path);
                }
                else if (!f.Open && row.Path.Contains('/'))
                {
                    string parent = row.Path[..row.Path.LastIndexOf('/')];
                    s.Cursor = Math.Max(0, rows.FindIndex(r => r.Path == parent));
                }
                break;
            case ToggleFindingsOnly when !img.Complete:
                Notice("Findings appear once every layer is indexed.");
                break;
            case ToggleFindingsOnly:
                s.FindingsOnly = !s.FindingsOnly;
                s.View = RightView.Files;
                s.Cursor = s.Scroll = 0;
                right.SetFocus();
                break;
            case ToggleChange t:
                if (!s.Hidden.Remove(t.Change))
                {
                    s.Hidden.Add(t.Change);
                }
                s.View = RightView.Files;
                s.Cursor = s.Scroll = 0;
                break;
            case SetWhole w:
                s.WholeFilesystem = w.On;
                s.View = s.View == RightView.Inspector ? RightView.Files : s.View;
                s.Cursor = s.Scroll = 0;
                break;
            case SelectFinding f:
                s.Finding = f.Index;
                break;
            case ToggleBaseFindings:
                s.ShowBaseFindings = !s.ShowBaseFindings;
                break;
            case SelectSearchHit h:
                s.SearchCursor = h.Index;
                search.SetFocus();
                break;
            case SetQuery q:
                s.SearchQuery = q.Text;
                s.SearchCursor = s.SearchScroll = 0;
                break;
            case SetSearchScope sc:
                s.SearchLayerOnly = sc.LayerOnly;
                s.SearchCursor = 0;
                search.SetFocus();
                break;
            case ToggleIncludeDeleted:
                s.SearchIncludeDeleted = !s.SearchIncludeDeleted;
                s.SearchCursor = 0;
                search.SetFocus();
                break;
            case ToggleExactCase:
                s.SearchExactCase = !s.SearchExactCase;
                s.SearchCursor = 0;
                search.SetFocus();
                break;
            case Notify n:
                Notice(n.Text);
                break;
            case PickTag:
                PickTagAndCompare();
                break;
            case CompareWith c:
                StartCompare(c.Tag);
                break;
            case PickPlatform:
                ChoosePlatform();
                break;
            case ExtractSelected:
                BeginExtract(SelectedPath());
                break;
            case CopyCommand:
                CopySelected();
                break;
            case OpenInViewer:
                OpenViewer();
                break;
            case FocusOn f:
                (f.Pane == FocusPane.Layers ? layers : right).SetFocus();
                break;
        }
        ex.Invalidate();
        Refresh();
    }

    private void ActivateFinding()
    {
        ExplorerFinding? finding = ex.SelectedFinding(s);
        if (finding is null)
        {
            if (ex.VisibleFindings(s).Count > 0)
            {
                s.ShowBaseFindings = !s.ShowBaseFindings;
            }
            return;
        }
        SelectLayerCore(finding.Layers[^1]);
        s.View = RightView.Files;
        s.FindingsOnly = true;
        foreach (string root in finding.Roots)
        {
            ExpandTo(root);
        }
        ex.Invalidate();
        if (finding.Roots.Count > 0)
        {
            s.Cursor = Math.Max(0, ex.IndexOf(s, finding.Roots[0]));
        }
        right.SetFocus();
    }

    private void ExpandTo(string path)
    {
        StringBuilder prefix = new();
        foreach (string part in path.Split('/')[..^1])
        {
            prefix.Append(prefix.Length > 0 ? "/" : "").Append(part);
            s.Expanded.Add(prefix.ToString());
        }
    }

    private void OpenSearchHit()
    {
        List<SearchHit> hits = ex.SearchResults(s).Hits;
        if (hits.Count == 0)
        {
            return;
        }
        SearchHit hit = hits[Math.Clamp(s.SearchCursor, 0, hits.Count - 1)];
        (int Layer, Change Change) last = hit.Layers.LastOrDefault(l => l.Change != Change.Removed);
        SelectLayerCore(last == default ? hit.Layers[^1].Layer : last.Layer);
        s.View = RightView.Files;
        s.WholeFilesystem = img.IsAnalyzed(s.Layer) && hit.Layers[^1].Change != Change.Removed;
        s.Hidden.Clear();
        s.FindingsOnly = false;
        ExpandTo(hit.Path);
        ex.Invalidate();
        s.Cursor = Math.Max(0, ex.IndexOf(s, hit.Path));
        right.SetFocus();
    }

    private string? SelectedPath()
    {
        if (s.View == RightView.Inspector)
        {
            return s.InspectPath;
        }
        if (s.View == RightView.Search)
        {
            List<SearchHit> hits = ex.SearchResults(s).Hits;
            return hits.Count == 0 ? null : hits[Math.Clamp(s.SearchCursor, 0, hits.Count - 1)].Path;
        }
        if (s.View != RightView.Files || !img.IsIndexed(s.Layer))
        {
            return null;
        }
        List<FlatRow> rows = ex.Flatten(s);
        return s.Cursor < rows.Count ? rows[s.Cursor].Path : null;
    }

    private bool IsDirectory(string path) =>
        img.Analysis?.LiveEntries.TryGetValue(path, out ScannedEntry? entry) == true
            ? entry.Type == ImageFileType.Directory
            : ExplorerImage.Find(ex.Tree(s), path)?.Kind == Kind.Dir;

    private bool Live(string path) =>
        img.Analysis?.LiveEntries.ContainsKey(path) == true ||
        img.Analysis?.LiveEntries.Keys.Any(key => key.StartsWith(path + "/", StringComparison.Ordinal)) == true;

    // ───────────────────────────── actions ─────────────────────────────

    private void RunAsync<T>(Func<CancellationToken, Task<T>> work, Action<T> done, Action<Exception>? failed = null,
        CancellationToken token = default)
    {
        IApplication? app = App;
        CancellationToken ct = token == default ? lifetime : token;
        Task.Run(() => work(ct), ct).ContinueWith(task =>
        {
            if (ct.IsCancellationRequested || app is null)
            {
                return;
            }
            app.Invoke(() =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    done(task.Result);
                }
                else
                {
                    Exception error = task.Exception?.GetBaseException() ?? new OperationCanceledException();
                    if (failed is not null)
                    {
                        failed(error);
                    }
                    else
                    {
                        Notice(error.Message, error: true);
                    }
                }
                ex.Invalidate();
                Refresh();
            });
        }, TaskScheduler.Default);
    }

    private void EnsurePreview()
    {
        if (s.View != RightView.Inspector || Comparing || !img.Complete || s.InspectPath.Length == 0
            || s.Preview?.Path == s.InspectPath)
        {
            return;
        }
        previewLoad?.Cancel();
        previewLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        string path = s.InspectPath;
        int layer = s.Layer;
        RunAsync(ct => host.PreviewAsync(path, layer, ct), preview =>
        {
            if (s.InspectPath == path)
            {
                s.Preview = preview;
            }
        }, error => s.Preview = new PreviewContent(path, null, null, error.Message, 0), previewLoad.Token);
    }

    private void BeginExtract(string? path)
    {
        if (path is null)
        {
            Notice("Select a file or folder to extract.");
            return;
        }
        if (!img.Complete)
        {
            Notice("Extract works once every layer is indexed.");
            return;
        }
        if (!Live(path))
        {
            Notice($"/{path} is not in the final image, so it can't be extracted.");
            return;
        }
        pendingExtract = path;
        extractField.Text = "./" + path.Split('/')[^1];
        extractField.Visible = extractLabel.Visible = true;
        extractField.SetFocus();
        extractField.MoveEnd();
    }

    private void CancelExtract()
    {
        pendingExtract = null;
        extractField.Visible = extractLabel.Visible = false;
        SyncFocus();
        Refresh();
    }

    private void FinishExtract(string destination)
    {
        string? path = pendingExtract;
        CancelExtract();
        if (path is null || string.IsNullOrWhiteSpace(destination))
        {
            return;
        }
        Notice($"Extracting /{path}…");
        RunAsync(ct => host.ExtractAsync(path, destination.Trim(), ct), message => Notice(message));
        Refresh();
    }

    private void CopySelected()
    {
        string? path = SelectedPath();
        if (path is null)
        {
            Notice("Select a file or folder first.");
            return;
        }
        Copy(ex.CopyCommandText(s, path, IsDirectory(path)));
    }

    private void Copy(string command)
    {
        if (host.ClipboardEnabled && host.WriteClipboard(command))
        {
            Notice("Copied: " + command);
        }
        else if (host.ClipboardEnabled)
        {
            Notice("Couldn't reach the clipboard. $ " + command);
            ShowCommand(command);
        }
        else
        {
            Notice("$ " + command);
            ShowCommand(command);
        }
    }

    private void ShowCommand(string command)
    {
        commandReturn = (s.View, s.Focus, s.Compare?.FocusLayers ?? false);
        commandText.Text = command;
        s.View = RightView.Command;
        Relayout();
        commandText.SetFocus();
    }

    private void OpenViewer()
    {
        string? path = SelectedPath();
        if (path is null || IsDirectory(path))
        {
            Notice("Select a file to open in the viewer.");
            return;
        }
        if (!img.Complete)
        {
            Notice("The viewer works once every layer is indexed.");
            return;
        }
        if (!Live(path))
        {
            Notice($"/{path} is not in the final image.");
            return;
        }
        Notice($"Opening /{path}…");
        RunAsync(ct => host.PrepareForViewerAsync(path, ct), file =>
        {
            if (openWindowedViewer is null)
            {
                Stop(new(ExplorerExitKind.Viewer, file));
            }
            else
            {
                openWindowedViewer(file);
                Notice($"Starting viewer for /{path}…");
            }
        });
    }

    private void ChoosePlatform()
    {
        if (host.Platforms.Count <= 1)
        {
            Notice("This image has only one platform.");
            return;
        }
        if (App is not IApplication app)
        {
            return;
        }
        ExplorerPlatform? chosen = PlatformPicker.Show(app, host.Platforms, host.Platform);
        if (chosen is not null && chosen != host.Platform)
        {
            Stop(new(ExplorerExitKind.Platform, Platform: chosen));
        }
    }

    private void PickTagAndCompare()
    {
        if (!img.Complete)
        {
            Notice("Compare works once every layer is indexed.");
            return;
        }
        if (App is not IApplication app)
        {
            return;
        }
        string? tag = TagPicker.Show(app, host, img, s.Compare?.TargetLabel, lifetime);
        if (tag is not null)
        {
            StartCompare(tag);
        }
    }

    public void StartCompare(string tag)
    {
        if (!img.Complete)
        {
            Notice("Compare works once every layer is indexed.");
            return;
        }
        CancelComparison();
        compareLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        s.ComparisonStatus = $"Comparing with {tag}… reading its layers";
        Notice(s.ComparisonStatus);
        if (s.Compare is not null)
        {
            s.Compare.Busy = true;
        }
        int generation = ++compareGeneration;
        IApplication? app = App;
        RunAsync(ct => host.CompareAsync(tag, () => app?.Invoke(() =>
        {
            if (compareGeneration == generation)
            {
                s.ComparisonStatus = $"Comparing with {tag}… reading packages";
                Notice(s.ComparisonStatus);
                ex.Invalidate();
                Refresh();
            }
        }), ct), comparison =>
        {
            if (compareGeneration != generation)
            {
                return;
            }
            compareGeneration++;
            CompleteComparison();
            s.Compare = new CompareState(comparison, ExplorerTags.Label(img.Reference), tag);
            s.View = RightView.Files;
            s.Notice = null;
            right.SetFocus();
        }, error =>
        {
            if (compareGeneration != generation)
            {
                return;
            }
            compareGeneration++;
            CompleteComparison();
            if (s.Compare is not null)
            {
                s.Compare.Busy = false;
            }
            Notice($"Could not compare with {tag}: {error.Message}", error: true);
        }, compareLoad.Token);
    }

    private void CompleteComparison()
    {
        compareLoad?.Dispose();
        compareLoad = null;
        s.ComparisonStatus = null;
        if (s.Compare is not null)
        {
            s.Compare.Busy = false;
        }
    }

    private void CancelComparison()
    {
        compareGeneration++;
        compareLoad?.Cancel();
        CompleteComparison();
    }

    // ───────────────────────────── compare ─────────────────────────────

    private bool ApplyCompare(Cmd cmd)
    {
        CompareState c = s.Compare!;
        if (s.View == RightView.Keys)
        {
            if (cmd is Move m)
            {
                s.KeysScroll = Math.Max(0, s.KeysScroll + m.Delta);
                return true;
            }
            if (cmd is Jump j)
            {
                s.KeysScroll = j.ToEnd ? int.MaxValue : 0;
                return true;
            }
            if (cmd is Back or ShowView)
            {
                s.View = RightView.Files;
                return true;
            }
            return cmd is not (Quit or Redraw or Notify);
        }
        List<CompareRow> rows = Compare.Rows();
        CompareRow? row = c.Cursor >= 0 && c.Cursor < rows.Count ? rows[c.Cursor] : null;
        if (cmd is Activate && c.Searching)
        {
            c.Searching = false;
            right.SetFocus();
        }
        switch (cmd)
        {
            case Quit or Redraw or Notify or PickTag or CompareWith:
                return false;
            case ShowView { View: RightView.Keys }:
                s.View = RightView.Keys;
                return true;
            case ShowView { View: RightView.Search }:
                c.Searching = true;
                c.FocusLayers = false;
                search.Visible = true;
                search.Text = c.SearchQuery;
                search.SetFocus();
                search.MoveEnd();
                return true;
            case SetQuery query:
                c.SearchQuery = query.Text;
                c.Cursor = c.Scroll = 0;
                return true;
            case Back when c.Searching:
                c.Searching = false;
                right.SetFocus();
                return true;
            case Back when c.Diff is not null:
                c.Diff = null;
                return true;
            case Back when c.SearchQuery.Length > 0:
                c.SearchQuery = "";
                c.Cursor = c.Scroll = 0;
                return true;
            case Back when c.PackageFiles is not null:
                c.PackageFiles = null;
                (c.Cursor, c.Scroll, c.SearchQuery) = c.PackageReturn;
                return true;
            case Back:
                s.Compare = null;
                right.SetFocus();
                return true;
            case SelectLayer when c.LayerCount == 0:
            case Move { } or Jump { } when c.LayerCount == 0 && c.FocusLayers:
                Notice("Neither image has layers.");
                return true;
            case SelectLayer l:
                c.Layer = Math.Clamp(l.Layer, 0, c.LayerCount - 1);
                return true;
            case StepDifference d:
                List<int> diffs = c.Differences().ToList();
                int? next = d.Delta > 0 ? diffs.Cast<int?>().FirstOrDefault(l => l > c.Layer) : diffs.Cast<int?>().LastOrDefault(l => l < c.Layer);
                if (next is int n)
                {
                    c.Layer = n;
                }
                else
                {
                    Notice(diffs.Count == 0 ? "Every layer is shared." : d.Delta > 0 ? "No later layer differs." : "No earlier layer differs.");
                }
                return true;
            case SwapSides:
                c.Comparison = ExplorerSession.Compare(c.Comparison.Target, c.Comparison.Baseline);
                (c.BaselineLabel, c.TargetLabel) = (c.TargetLabel, c.BaselineLabel);
                c.Cursor = c.Scroll = 0;
                c.Diff = null;
                c.PackageFiles = null;
                Notice($"Now showing {c.BaselineLabel} → {c.TargetLabel}");
                return true;
            case Move m when c.Diff is not null:
                c.DiffScroll = Math.Max(0, c.DiffScroll + m.Delta);
                return true;
            case PanText pan when c.Diff is not null:
                c.DiffColumn = Math.Max(0, c.DiffColumn + pan.Delta);
                return true;
            case Move m when c.FocusLayers:
                c.Layer = Math.Clamp(c.Layer + Math.Sign(m.Delta), 0, c.LayerCount - 1);
                return true;
            case Move m:
                c.Cursor = Math.Clamp(c.Cursor + m.Delta, 0, Math.Max(0, rows.Count - 1));
                return true;
            case Jump j when c.Diff is not null:
                c.DiffScroll = j.ToEnd ? int.MaxValue : 0;
                return true;
            case Jump j when c.FocusLayers:
                c.Layer = j.ToEnd ? c.LayerCount - 1 : 0;
                return true;
            case Jump j:
                c.Cursor = j.ToEnd ? Math.Max(0, rows.Count - 1) : 0;
                return true;
            case SetCursor sc:
                c.Cursor = Math.Clamp(sc.Row, 0, Math.Max(0, rows.Count - 1));
                return true;
            case Fold f when row is not null && row.Expandable:
                if (f.Open)
                {
                    c.Expanded.Add(row.Key);
                }
                else
                {
                    c.Expanded.Remove(row.Key);
                }
                return true;
            case Fold { Open: false } when row is not null:
                // Jump to the parent row.
                int depth = row.Branch.Length;
                for (int i = c.Cursor - 1; i >= 0; i--)
                {
                    if (rows[i].Branch.Length < depth && rows[i].Expandable)
                    {
                        c.Cursor = i;
                        break;
                    }
                }
                return true;
            case Activate when row is null:
                return true;
            case Activate when row.Expandable && row.Kind != CompareRowKind.File:
                if (!c.Expanded.Remove(row.Key))
                {
                    c.Expanded.Add(row.Key);
                }
                return true;
            case Activate when row.Kind == CompareRowKind.File && row.Path is string path:
                Notice($"Diffing /{path}…");
                ExplorerComparison comparison = c.Comparison;
                RunAsync(ct => host.DiffAsync(comparison, path, ct), diff =>
                {
                    if (s.Compare?.Comparison == comparison)
                    {
                        s.Compare.Diff = diff;
                        s.Compare.DiffScroll = 0;
                        s.Compare.DiffColumn = 0;
                        s.Notice = null;
                        right.SetFocus();
                    }
                });
                return true;
            case Activate when row.Kind == CompareRowKind.Package && row.Package is ExplorerPackageDifference package:
                PackageFilesContent pending = new(package, null, "Reading package files…", 0);
                c.PackageFiles = pending;
                c.PackageReturn = (c.Cursor, c.Scroll, c.SearchQuery);
                c.Cursor = c.Scroll = 0;
                c.SearchQuery = "";
                ExplorerComparison current = c.Comparison;
                RunAsync(ct => host.PackageFilesAsync(current, package, ct), files =>
                {
                    if (s.Compare?.PackageFiles == pending)
                    {
                        s.Compare.PackageFiles = files;
                    }
                }, error =>
                {
                    if (s.Compare?.PackageFiles == pending)
                    {
                        s.Compare.PackageFiles = pending with { Message = "Could not read package files: " + error.Message };
                    }
                });
                return true;
            case Activate:
                return true;
            case CopyCommand:
                string platform = img.PlatformArguments.Length > 0 ? " " + img.PlatformArguments : "";
                Copy($"dredge image compare files {ShellCommand.Quote(c.Comparison.Baseline.Image.ToString())} {ShellCommand.Quote(c.Comparison.Target.Image.ToString())}{platform}");
                return true;
            case FocusOn f:
                (f.Pane == FocusPane.Layers ? layers : right).SetFocus();
                return true;
            default:
                Notice("Press Esc to leave compare first.");
                return true;
        }
    }
}

internal static class ExplorerTags
{
    // The tag (or short digest) that names an image reference in compare labels.
    public static string Label(string reference)
    {
        int at = reference.IndexOf('@');
        if (at >= 0)
        {
            string digest = reference[(at + 1)..];
            return digest.Length > 19 ? digest[..19] : digest;
        }
        int slash = reference.LastIndexOf('/');
        int colon = reference.LastIndexOf(':');
        return colon > slash ? reference[(colon + 1)..] : "latest";
    }

    public static string Repository(string reference)
    {
        int at = reference.IndexOf('@');
        string name = at >= 0 ? reference[..at] : reference;
        int slash = name.LastIndexOf('/');
        int colon = name.LastIndexOf(':');
        return colon > slash ? name[..colon] : name;
    }

    public static string WithTag(string reference, string label) =>
        label.StartsWith("sha256:", StringComparison.Ordinal) ? reference : $"{Repository(reference)}:{label}";
}
