using System.Collections;
using System.Collections.Specialized;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Valleysoft.Dredge.Explorer.Tui;

// Shared styling for the explorer's stock dialogs.
internal static class Dialogs
{
    public static Dialog Create(string title, int width, int height)
    {
        Terminal.Gui.Drawing.Attribute panel = Paint.Attr(Theme.Foam, Theme.Graphite);
        Dialog dialog = new()
        {
            Title = title,
            Width = width,
            Height = height,
            BorderStyle = LineStyle.Rounded,
            ShadowStyle = ShadowStyles.None,
        };
        dialog.SetScheme(new Scheme(Paint.Attr(Theme.Channel, Theme.Graphite))
        {
            Focus = Paint.Attr(Theme.Foam, Theme.Graphite, Deco.Bold),
            HotNormal = panel,
            HotFocus = panel,
            Active = panel,
        });
        return dialog;
    }

    public static Scheme Panel()
    {
        Terminal.Gui.Drawing.Attribute panel = Paint.Attr(Theme.Foam, Theme.Graphite);
        return new Scheme(panel) { Focus = panel, Active = panel, HotNormal = panel, HotFocus = panel, Highlight = panel };
    }

    public static Scheme Input()
    {
        Terminal.Gui.Drawing.Attribute input = Paint.Attr(Theme.Foam, Theme.KeycapBg, Deco.Bold);
        return new Scheme(input) { Focus = input, Editable = input, Active = input, HotNormal = input, HotFocus = input };
    }

    // Flat keycaps instead of the stock decorations; focus inverts to the accent.
    public static void AddButtons(Dialog dialog, string primary)
    {
        Button ok = new() { Text = $" {primary} ", ShadowStyle = ShadowStyles.None, NoDecorations = true };
        Button cancel = new() { Text = " Cancel ", ShadowStyle = ShadowStyles.None, NoDecorations = true };
        foreach ((Button b, Sty normal) in new[] { (ok, Theme.S(Theme.Foam, Theme.ChannelDeep, Deco.Bold)), (cancel, Theme.S(Theme.Foam, Theme.KeycapBg)) })
        {
            Terminal.Gui.Drawing.Attribute rest = Paint.ToAttribute(normal);
            Terminal.Gui.Drawing.Attribute focus = Paint.Attr(Theme.Ground, Theme.Channel, Deco.Bold);
            b.SetScheme(new Scheme(rest) { HotNormal = rest, Focus = focus, HotFocus = focus, Active = focus });
        }
        // Dialog makes the last button the default, so the primary action sits on the right.
        dialog.AddButton(cancel);
        dialog.AddButton(ok);
    }

    public const int PrimaryButton = 1;
}

// `c`: choose a tag to compare with. Tags stream in from the registry, then a
// bounded background pass fills in shared layers and download size per tag.
internal static class TagPicker
{
    private const int StatsConcurrency = 4;

    public static string? Show(IApplication app, IExplorerHost host, ExplorerImage img, string? filter, CancellationToken lifetime)
    {
        lifetime.ThrowIfCancellationRequested();
        (Dialog dialog, Func<string?> chosen, Action<IReadOnlyList<TagChoice>?, string?> fill, Action redraw) =
            Create(img, filter ?? "");
        Loading loading = new(host, img, lifetime, app.Invoke, fill, redraw);
        try
        {
            using CancellationTokenRegistration registration = lifetime.Register(() =>
                loading.Post(() => dialog.RequestStop(), allowCancellation: true));
            app.Run(dialog);
            lifetime.ThrowIfCancellationRequested();
            return !dialog.Canceled && dialog.Result == Dialogs.PrimaryButton ? chosen() : null;
        }
        finally
        {
            try
            {
                // Loading never captures the UI context; draining it needs no further modal iterations.
                loading.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                dialog.Dispose();
            }
        }
    }

    internal sealed class Loading
    {
        private readonly CancellationTokenSource source;
        private readonly CancellationToken token;
        private readonly Action<Action> enqueue;
        private readonly Task work;
        private Task? stopping;
        private int closed;

        public Loading(IExplorerHost host, ExplorerImage img, CancellationToken lifetime, Action<Action> enqueue,
            Action<IReadOnlyList<TagChoice>?, string?> fill, Action redraw)
        {
            source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            token = source.Token;
            this.enqueue = enqueue;
            work = Task.Run(async () =>
            {
                IReadOnlyList<string> tags;
                try
                {
                    tags = await host.ListTagsAsync(token).ConfigureAwait(false);
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    Post(() => fill(null, exception.Message));
                    return;
                }
                token.ThrowIfCancellationRequested();
                List<TagChoice> choices = Order(tags, ExplorerTags.Label(img.Reference)).Select(tag => new TagChoice(tag)).ToList();
                Post(() => fill(choices, null));
                using SemaphoreSlim gate = new(StatsConcurrency);
                await Task.WhenAll(choices.Select(async choice =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await host.DescribeTagAsync(choice, token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!token.IsCancellationRequested)
                    {
                        choice.Failed = true;
                        choice.Note = "could not read this tag: " + exception.Message;
                    }
                    finally
                    {
                        gate.Release();
                    }
                    Post(redraw);
                })).ConfigureAwait(false);
            }, token);
        }

        public void Post(Action action, bool allowCancellation = false)
        {
            bool Active() => Volatile.Read(ref closed) == 0 && (allowCancellation || !token.IsCancellationRequested);
            if (!Active())
            {
                return;
            }
            try
            {
                enqueue(() =>
                {
                    if (Active())
                    {
                        action();
                    }
                });
            }
            catch (ObjectDisposedException) when (!Active())
            {
            }
        }

        public Task StopAsync() => stopping ??= StopCoreAsync();

        private async Task StopCoreAsync()
        {
            Interlocked.Exchange(ref closed, 1);
            try
            {
                try
                {
                    source.Cancel();
                }
                finally
                {
                    try
                    {
                        await work.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                }
            }
            finally
            {
                source.Dispose();
            }
        }
    }

    // The explored tag first, then by name.
    internal static IEnumerable<string> Order(IEnumerable<string> tags, string current) =>
        tags.Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag == current ? 0 : 1)
            .ThenBy(tag => tag, StringComparer.Ordinal);

    public static (Dialog Dialog, Func<string?> Chosen, Action<IReadOnlyList<TagChoice>?, string?> Fill, Action Redraw) Create(
        ExplorerImage img, string filter)
    {
        string current = ExplorerTags.Label(img.Reference);
        Dialog dialog = Dialogs.Create($"Compare {current} with", 80, 20);

        Label prompt = new() { X = 1, Y = 1, Text = "Image or tag" };
        prompt.SetScheme(new Scheme(Paint.Attr(Theme.Silt, Theme.Graphite)));
        TextField field = new() { X = 15, Y = 1, Width = Dim.Fill(2), Text = filter };
        field.SetScheme(Dialogs.Input());

        TagSource source = new(img.LayerCount, img.Digest);
        ListView list = new() { X = 1, Y = 3, Width = Dim.Fill(2), Height = 7, Source = source };
        list.SetScheme(Dialogs.Panel());

        Label hint = new() { X = 1, Y = Pos.Bottom(list) + 1, Width = Dim.Fill(2), Text = "Loading tags…" };
        hint.SetScheme(new Scheme(Paint.Attr(Theme.Silt, Theme.Graphite)));
        Label annotationFirst = new() { X = 1, Y = Pos.Bottom(hint), Width = Dim.Fill(2), Height = 1 };
        Label annotationLast = new() { X = 1, Y = Pos.Bottom(annotationFirst), Width = Dim.Fill(2), Height = 1 };
        annotationFirst.SetScheme(new Scheme(Paint.Attr(Theme.Ochre, Theme.Graphite)));
        annotationLast.SetScheme(new Scheme(Paint.Attr(Theme.Ochre, Theme.Graphite)));
        string summary = "Loading tags…";

        void UpdateHint()
        {
            TagChoice? selected = list.SelectedItem is int i && i < source.Count ? source[i] : null;
            string? annotation = selected?.Digest == img.Digest
                ? "current image; " + selected.Note : selected?.Note;
            List<Line> lines = annotation is null ? [] : Syntax.Wrap([(annotation, new Sty(Theme.Silt))], 74, 2);
            hint.Text = summary;
            annotationFirst.Text = lines.ElementAtOrDefault(0)?.ToString() ?? "";
            annotationLast.Text = lines.ElementAtOrDefault(1)?.ToString() ?? "";
        }
        list.ValueChanged += (_, _) => UpdateHint();

        void Filter()
        {
            source.Filter(field.Text);
            list.SelectedItem = source.Count > 0 ? 0 : null;
            UpdateHint();
            list.SetNeedsDraw();
        }
        field.TextChanged += (_, _) => Filter();
        field.KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.CursorDown or KeyCode.CursorUp or KeyCode.PageDown or KeyCode.PageUp && source.Count > 0)
            {
                int step = key.KeyCode switch { KeyCode.CursorDown => 1, KeyCode.CursorUp => -1, KeyCode.PageDown => 10, _ => -10 };
                list.SelectedItem = Math.Clamp((list.SelectedItem ?? 0) + step, 0, source.Count - 1);
                key.Handled = true;
            }
        };

        dialog.Add(prompt, field, list, hint, annotationFirst, annotationLast);
        Dialogs.AddButtons(dialog, "Compare");
        field.SetFocus();

        void Fill(IReadOnlyList<TagChoice>? choices, string? error)
        {
            if (choices is null)
            {
                summary = "Could not list tags: " + error + " Type a tag and press Enter.";
            }
            else
            {
                source.Set(choices);
                summary = choices.Count <= 1
                    ? "No other tags. Type a tag and press Enter."
                    : $"{Fmt.N(choices.Count)} tags.";
            }
            Filter();
            dialog.SetNeedsDraw();
        }
        return (dialog, () =>
        {
            if (list.SelectedItem is int i && i < source.Count)
            {
                return source[i].Tag;
            }
            string typed = field.Text.Trim();
            return typed.Length > 0 ? typed : null;
        }, Fill, () =>
        {
            UpdateHint();
            list.SetNeedsDraw();
        });
    }

    private sealed class TagSource : IListDataSource
    {
        private readonly int layerCount;
        private readonly string currentDigest;
        private List<TagChoice> all = [];
        private List<TagChoice> shown = [];

        public TagSource(int layerCount, string currentDigest)
        {
            this.layerCount = layerCount;
            this.currentDigest = currentDigest;
        }

        public TagChoice this[int i] => shown[i];

        public void Set(IReadOnlyList<TagChoice> choices) => all = [.. choices];

        public void Filter(string text) =>
            shown = all.Where(t => t.Tag.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Tag == text.Trim() ? 0 : 1).ToList();

        public event NotifyCollectionChangedEventHandler? CollectionChanged { add { } remove { } }
        public int Count => shown.Count;
        public int MaxItemLength => 74;
        public bool SuspendCollectionChangedEvent { get; set; }
        public bool IsMarked(int item) => false;
        public void SetMark(int item, bool value) { }
        public bool RenderMark(ListView container, int item, int row, bool isMarked, bool allowsMarking) => false;
        public IList ToList() => shown;
        public void Dispose() { }

        public void Render(ListView container, bool selected, int item, int col, int row, int width, int viewportX)
        {
            TagChoice t = shown[item];
            Rgb bg = selected ? Theme.ChannelDeep : Theme.Graphite;
            string? note = t.Digest == currentDigest ? "current image" : t.Note;
            Line stats = new();
            if (t.Shared is int shared)
            {
                stats.Add($"{shared} of {t.LayerCount ?? layerCount}", Theme.Foam).Add(" shared   ", Theme.Silt)
                    .Add(Fmt.SizeShort(t.AdditionalDownload ?? 0).PadLeft(7), Theme.Foam).Add(" to download", Theme.Silt);
            }
            int nameWidth = Math.Min(26, Math.Max(10, width - 1 - stats.Length -
                (note is null ? 0 : Math.Min(DisplayText.Width(note) + 3, 23))));
            Line line = new Line()
                .Add(selected ? "▌" : " ", Theme.Channel)
                .Add(Fmt.Fit(t.Tag, nameWidth - 2).PadRight(nameWidth), Theme.S(Theme.Foam, null, Deco.Bold))
                .Append(stats);
            if (note is not null)
            {
                line.Add((stats.Length > 0 ? " " : "") + "▲ " + note, Theme.Ochre);
            }
            else if (t.Failed)
            {
                line.Add("could not read this tag", Theme.Garnet);
            }
            else if (stats.Length == 0)
            {
                line.Add("…", Theme.Shale);
            }
            Paint.Draw(container, col, row, line.Truncate(width).Pad(width).WithBackground(bg), width);
        }
    }
}

internal static class PlatformPicker
{
    public static ExplorerPlatform? ShowInitial(IReadOnlyList<ExplorerPlatform> platforms, bool mouse,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using ResponsiveLoop loop = new();
        using IApplication app = Application.Create();
        app.Init();
        if (!mouse)
        {
            app.Mouse.IsMouseDisabled = true;
            app.Driver?.WriteRaw(EscSeqUtils.CSI_DisableMouseEvents);
        }
        app.Iteration += (_, _) => ResponsiveLoop.QuietCursor(app);
        return Show(app, platforms, null, cancellationToken);
    }

    public static ExplorerPlatform? Show(IApplication app, IReadOnlyList<ExplorerPlatform> platforms,
        ExplorerPlatform? current, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Dialog dialog = Create(platforms, current, out Func<ExplorerPlatform?> chosen);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            app.Invoke(() => app.RequestStop()));
        app.Run(dialog);
        cancellationToken.ThrowIfCancellationRequested();
        ExplorerPlatform? result = !dialog.Canceled && dialog.Result == Dialogs.PrimaryButton ? chosen() : null;
        return result;
    }

    public static Dialog Create(IReadOnlyList<ExplorerPlatform> platforms, ExplorerPlatform? current, out Func<ExplorerPlatform?> chosen)
    {
        Dialog dialog = Dialogs.Create("Choose platform", 60, Math.Min(22, platforms.Count + 8));
        List<ExplorerPlatform> items = platforms.ToList();
        ListView list = new()
        {
            X = 1, Y = 1, Width = Dim.Fill(2), Height = Math.Min(14, items.Count),
            Source = new ListWrapper<string>(new(items.Select(p => (p == current ? "● " : "  ") + p))),
        };
        list.SetScheme(new Scheme(Paint.Attr(Theme.Foam, Theme.Graphite))
        {
            Focus = Paint.Attr(Theme.Foam, Theme.ChannelDeep, Deco.Bold),
            Active = Paint.Attr(Theme.Foam, Theme.ChannelDeep, Deco.Bold),
            HotNormal = Paint.Attr(Theme.Foam, Theme.Graphite),
            HotFocus = Paint.Attr(Theme.Foam, Theme.ChannelDeep, Deco.Bold),
            Highlight = Paint.Attr(Theme.Foam, Theme.ChannelDeep, Deco.Bold),
        });
        list.SelectedItem = Math.Max(0, current is null ? 0 : items.IndexOf(current));
        Label hint = new() { X = 1, Y = Pos.Bottom(list) + 1, Width = Dim.Fill(2), Text = "The explorer reloads on the chosen platform." };
        hint.SetScheme(new Scheme(Paint.Attr(Theme.Silt, Theme.Graphite)));
        dialog.Add(list, hint);
        Dialogs.AddButtons(dialog, "Open");
        list.SetFocus();
        chosen = () => list.SelectedItem is int i && i < items.Count ? items[i] : null;
        return dialog;
    }
}
