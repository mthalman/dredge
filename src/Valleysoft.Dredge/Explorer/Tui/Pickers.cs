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
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        (Dialog dialog, Func<string?> chosen, Action<IReadOnlyList<TagChoice>?, string?> fill, Action redraw) =
            Create(img, filter ?? "");
        Task.Run(async () =>
        {
            IReadOnlyList<string> tags;
            try
            {
                tags = await host.ListTagsAsync(cts.Token);
            }
            catch (Exception exception) when (!cts.IsCancellationRequested)
            {
                app.Invoke(() => fill(null, exception.Message));
                return;
            }
            List<TagChoice> choices = Order(tags, ExplorerTags.Label(img.Reference)).Select(tag => new TagChoice(tag)).ToList();
            app.Invoke(() => fill(choices, null));
            using SemaphoreSlim gate = new(StatsConcurrency);
            await Task.WhenAll(choices.Select(async choice =>
            {
                await gate.WaitAsync(cts.Token);
                try
                {
                    await host.DescribeTagAsync(choice, cts.Token);
                }
                catch (Exception) when (!cts.IsCancellationRequested)
                {
                    choice.Failed = true;
                }
                finally
                {
                    gate.Release();
                }
                app.Invoke(redraw);
            }));
        }, cts.Token);
        app.Run(dialog);
        string? tag = !dialog.Canceled && dialog.Result == Dialogs.PrimaryButton ? chosen() : null;
        cts.Cancel();
        dialog.Dispose();
        return tag;
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

        Label prompt = new() { X = 1, Y = 1, Text = "Tag" };
        prompt.SetScheme(new Scheme(Paint.Attr(Theme.Silt, Theme.Graphite)));
        TextField field = new() { X = 6, Y = 1, Width = Dim.Fill(2), Text = filter };
        field.SetScheme(Dialogs.Input());

        TagSource source = new(img.LayerCount, current);
        ListView list = new() { X = 1, Y = 3, Width = Dim.Fill(2), Height = 9, Source = source };
        list.SetScheme(Dialogs.Panel());

        Label hint = new() { X = 1, Y = Pos.Bottom(list) + 1, Width = Dim.Fill(2), Text = "Loading tags…" };
        hint.SetScheme(new Scheme(Paint.Attr(Theme.Silt, Theme.Graphite)));

        void Filter()
        {
            source.Filter(field.Text);
            list.SelectedItem = source.Count > 0 ? 0 : null;
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

        dialog.Add(prompt, field, list, hint);
        Dialogs.AddButtons(dialog, "Compare");
        field.SetFocus();

        void Fill(IReadOnlyList<TagChoice>? choices, string? error)
        {
            if (choices is null)
            {
                hint.Text = "Could not list tags: " + error + " Type a tag and press Enter.";
            }
            else
            {
                source.Set(choices);
                hint.Text = choices.Count <= 1
                    ? "No other tags. Type a tag and press Enter."
                    : $"{Fmt.N(choices.Count)} tags. Download is what pulling the tag adds to {current}.";
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
        }, Fill, () => list.SetNeedsDraw());
    }

    private sealed class TagSource : IListDataSource
    {
        private readonly int layerCount;
        private readonly string current;
        private List<TagChoice> all = [];
        private List<TagChoice> shown = [];

        public TagSource(int layerCount, string current)
        {
            this.layerCount = layerCount;
            this.current = current;
        }

        public TagChoice this[int i] => shown[i];

        public void Set(IReadOnlyList<TagChoice> choices) => all = [.. choices];

        public void Filter(string text) =>
            shown = all.Where(t => t.Tag.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

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
            Line line = new Line()
                .Add(selected ? "▌" : " ", Theme.Channel)
                .Add(Fmt.Fit(t.Tag, 24).PadRight(26), Theme.S(Theme.Foam, null, Deco.Bold));
            string? note = t.Tag == current ? "current image" : t.Note;
            if (note is not null)
            {
                line.Add("▲ " + note, Theme.Ochre);
            }
            else if (t.Failed)
            {
                line.Add("could not read this tag", Theme.Garnet);
            }
            else if (t.Shared is int shared)
            {
                line.Add($"{shared} of {t.LayerCount ?? layerCount}", Theme.Foam).Add(" shared   ", Theme.Silt)
                    .Add(Fmt.SizeShort(t.AdditionalDownload ?? 0).PadLeft(7), Theme.Foam).Add(" to download", Theme.Silt);
            }
            else
            {
                line.Add("…", Theme.Shale);
            }
            Paint.Draw(container, col, row, line.Truncate(width).Pad(width).WithBackground(bg), width);
        }
    }
}

// `p`: choose another platform from a multi-platform image. The explorer restarts on it.
internal static class PlatformPicker
{
    public static ExplorerPlatform? Show(IApplication app, IReadOnlyList<ExplorerPlatform> platforms, ExplorerPlatform? current)
    {
        Dialog dialog = Create(platforms, current, out Func<ExplorerPlatform?> chosen);
        app.Run(dialog);
        ExplorerPlatform? result = !dialog.Canceled && dialog.Result == Dialogs.PrimaryButton ? chosen() : null;
        dialog.Dispose();
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
