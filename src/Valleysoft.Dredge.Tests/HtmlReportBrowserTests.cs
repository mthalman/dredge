using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Valleysoft.Dredge.Explorer;
using static Microsoft.Playwright.Assertions;

namespace Valleysoft.Dredge.Tests;

[Trait("Category", "Browser")]
public sealed class HtmlReportBrowserTests : IClassFixture<HtmlReportBrowserFixture>, IAsyncLifetime
{
    private readonly HtmlReportBrowserFixture fixture;
    private readonly List<string> errors = [];
    private IPage? page;

    public HtmlReportBrowserTests(HtmlReportBrowserFixture fixture)
    {
        this.fixture = fixture;
    }

    private IPage Page
    {
        get
        {
            Assert.SkipUnless(HtmlReportBrowserFixture.Enabled,
                "Set DREDGE_HTML_BROWSER_TESTS=1 to run the HTML report browser tests.");
            return page ?? throw new InvalidOperationException("The browser test page was not initialized.");
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (!HtmlReportBrowserFixture.Enabled) return;

        page = await fixture.Browser.NewPageAsync(new()
        {
            ViewportSize = new() { Width = 1280, Height = 900 }
        });
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(fixture.ReportUri, new() { WaitUntil = WaitUntilState.NetworkIdle });
    }

    public async ValueTask DisposeAsync()
    {
        if (page is not null) await page.CloseAsync();
        Assert.Empty(errors);
    }

    [Fact]
    public async Task EmbeddedFaviconRendersDredgeLogo()
    {
        ILocator favicon = Page.Locator("link[rel=\"icon\"]");
        await Expect(favicon).ToHaveAttributeAsync("type", "image/svg+xml");
        string? source = await favicon.GetAttributeAsync("href");
        Assert.NotNull(source);
        Assert.StartsWith("data:image/svg+xml;base64,", source);

        JsonElement image = await Page.EvaluateAsync<JsonElement>(
            """
            source => new Promise(resolve => {
                const image = new Image();
                image.onload = () => {
                    const canvas = document.createElement("canvas");
                    canvas.width = image.naturalWidth;
                    canvas.height = image.naturalHeight;
                    const context = canvas.getContext("2d");
                    context.drawImage(image, 0, 0);
                    resolve({
                        width: image.naturalWidth,
                        height: image.naturalHeight,
                        border: [...context.getImageData(16, 1, 1, 1).data],
                        cornerAlpha: context.getImageData(0, 0, 1, 1).data[3]
                    });
                };
                image.onerror = () => resolve(null);
                image.src = source;
            })
            """, source);
        Assert.Equal(JsonValueKind.Object, image.ValueKind);
        Assert.Equal(32, image.GetProperty("width").GetInt32());
        Assert.Equal(32, image.GetProperty("height").GetInt32());
        Assert.Equal([47, 88, 120, 255],
            image.GetProperty("border").EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal(0, image.GetProperty("cornerAlpha").GetInt32());
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task SingleImageInitialLayerMatchesExplorer(int? initialLayer, int expectedLayer)
    {
        JsonNode data = HtmlImageReportTests.Data(fixture.Data.Html);
        data["images"]!.AsArray().RemoveAt(1);
        data["differences"] = null;
        data["initialLayer"] = initialLayer;
        await LoadReportAsync(data, $"initial-layer-{initialLayer?.ToString() ?? "default"}.html");

        await Expect(Page.Locator("#layers")).ToHaveAttributeAsync("aria-activedescendant", $"layer-{expectedLayer}");
        await Expect(Page.Locator("#details-title")).ToHaveTextAsync($"Layer {expectedLayer}");
    }

    [Fact]
    public async Task ComparisonOpensImageAtLastLayer()
    {
        await Button("base").ClickAsync();
        await Expect(Page.Locator("#layers")).ToHaveAttributeAsync("aria-activedescendant", "layer-1");
    }

    [Fact]
    public async Task ComparisonSwitchToShorterImageSelectsLastLayer()
    {
        JsonNode data = HtmlImageReportTests.Data(fixture.Data.Html);
        JsonNode baseline = data["images"]![0]!;
        JsonNode lastLayer = baseline["layers"]![1]!.DeepClone();
        lastLayer["index"] = 2;
        baseline["layers"]!.AsArray().Add(lastLayer);
        JsonNode inventory = baseline["packageInventories"]!.AsArray().First(
            item => item!["layers"]!.AsArray().Any(layer => layer!.GetValue<int>() == 1))!;
        inventory["layers"]!.AsArray().Add(2);
        await LoadReportAsync(data, "shorter-target.html");

        await Button("target").ClickAsync();
        await Expect(Page.Locator("#layers")).ToHaveAttributeAsync("aria-activedescendant", "layer-1");
    }

    [Fact]
    public async Task ComparisonUnicodeSearchCountAndSwap()
    {
        await Button("Differences").ClickAsync();
        await Page.Locator("#filter").FillAsync("\u03c3-unicodeprobe");
        await Expect(Page.Locator(".search-count")).ToHaveTextAsync("1 match");
        await Expect(Page.Locator("#right .controls.silt")).ToContainTextAsync("1 match");
        ILocator row = Page.Locator(".comparison-search.has-sizes");
        await Expect(row).ToHaveCountAsync(1);
        await Expect(row.Locator(".name")).ToContainTextAsync("\u03c2-unicodeprobe");
        IReadOnlyList<string> before = await row.Locator(":scope > span").AllTextContentsAsync();
        await row.ClickAsync();
        await Expect(Page.Locator("#right .info")).ToContainTextAsync("/app/\u03c2-unicodeprobe");
        await Button("Swap sides").ClickAsync();
        IReadOnlyList<string> after = await Page.Locator(".comparison-search.has-sizes > span").AllTextContentsAsync();
        Assert.Equal(before[1], after[3]);
        Assert.Equal(before[3], after[1]);
        await Page.Locator("#filter").FillAsync("\u03c3-unicodepkg");
        await Expect(Page.Locator(".search-count")).ToHaveTextAsync("1 match");
        await Expect(Page.Locator(".package-diff-row")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".package-diff-row .name")).ToContainTextAsync("\u03c2-unicodepkg");
    }

    [Fact]
    public async Task ComparisonSearchPlaceholderFits()
    {
        await Button("Differences").ClickAsync();
        ILocator search = Page.GetByRole(AriaRole.Searchbox, new() { Name = "Search packages or paths" });
        foreach (int width in new[] { 320, 761, 1280 })
        {
            await Page.SetViewportSizeAsync(width, 900);
            JsonElement metrics = await search.EvaluateAsync<JsonElement>(
                """
                field => {
                    const style = getComputedStyle(field);
                    const canvas = document.createElement('canvas');
                    const context = canvas.getContext('2d');
                    context.font = `${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
                    const fieldRect = field.getBoundingClientRect();
                    const toolbarRect = field.closest('.toolbar').getBoundingClientRect();
                    return {
                        textWidth: context.measureText(field.placeholder).width,
                        availableWidth: fieldRect.width - parseFloat(style.paddingLeft) -
                            parseFloat(style.paddingRight) - parseFloat(style.borderLeftWidth) -
                            parseFloat(style.borderRightWidth),
                        rightGap: toolbarRect.right - fieldRect.right,
                        overflow: document.documentElement.scrollWidth > innerWidth
                    };
                }
                """);
            double textWidth = metrics.GetProperty("textWidth").GetDouble();
            double availableWidth = metrics.GetProperty("availableWidth").GetDouble();
            Assert.True(availableWidth > textWidth,
                $"At viewport width {width}, the placeholder needs {textWidth}px but has {availableWidth}px.");
            Assert.True(metrics.GetProperty("rightGap").GetDouble() >= 0, $"Search field overflows at {width}px.");
            Assert.False(metrics.GetProperty("overflow").GetBoolean(), $"Page overflows at {width}px.");
        }
    }

    [Fact]
    public async Task WholeFilesystemMatchesTuiTree()
    {
        await Button("base").ClickAsync();
        await Page.Locator("#layer-1").ClickAsync();
        await Button("Whole filesystem").ClickAsync();
        List<Node> tree = fixture.Data.Tree;
        Node app = Assert.Single(tree, node => node.Path == "app");
        await Expect(Page.Locator("#right .controls .push")).ToHaveTextAsync(
            $"{Fmt.Size(Node.TotalSize(tree))} in {Fmt.Count(ExplorerPresenter.Count(tree).Values.Sum(), "file")}");
        ILocator folder = Page.Locator(".trow[data-path=\"app\"]");
        await Expect(folder.Locator(".file-size")).ToHaveTextAsync(Fmt.Size(app.Size));
        await Expect(folder.Locator(".name")).ToContainTextAsync($"{app.FileCount} files");
        await Expect(folder.Locator(".name")).ToContainTextAsync(Assert.IsType<string>(app.Sharing).ToLowerInvariant());
        await Expect(folder.Locator(".tree-note")).ToContainTextAsync(Assert.IsType<string>(app.Note));
        await folder.Locator(".tree-toggle").ClickAsync();
        await Expect(Page.Locator(".trow[data-path^=\"app/\"]")).ToHaveCountAsync(app.Children.Count);
        foreach (Node item in app.Children)
        {
            ILocator row = Page.Locator($".trow[data-path=\"{item.Path}\"]");
            await Expect(row).ToHaveCountAsync(1);
            string[] changes = (await row.GetAttributeAsync("class") ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(value => value is "Added" or "Modified" or "Identical" or "Deleted")
                .ToArray();
            if (item.Change == Change.None)
            {
                Assert.Empty(changes);
            }
            else
            {
                Assert.Equal(item.Change == Change.Removed ? "Deleted" : item.Change.ToString(), Assert.Single(changes));
            }
            await Expect(row.Locator(".file-size")).ToHaveTextAsync(
                Fmt.Size(item.Change == Change.Removed ? item.ShippedSize : item.Size));
            if (item.Note is not null) await Expect(row.Locator(".tree-note")).ToContainTextAsync(item.Note);
        }
        await Page.Locator(".trow[data-path=\"app/link\"]").ClickAsync();
        await Expect(Page.Locator("#right .info")).ToContainTextAsync("/app/link");
    }

    [Fact]
    public async Task InventoryUnicodeFilterMatchesTui()
    {
        await Button("base").ClickAsync();
        await Page.Locator("#layer-1").ClickAsync();
        await Button("Packages").ClickAsync();
        await Page.GetByRole(AriaRole.Searchbox, new() { Name = "Filter packages" }).FillAsync("\u03c3-unicodepkg");
        await Expect(Page.Locator(".search-count")).ToHaveTextAsync("1 match");
        await Expect(Page.Locator(".package-row")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".package-row")).ToContainTextAsync("\u03c2-unicodepkg");
    }

    [Fact]
    public async Task FindingNavigationExplainsAndCanClearFilter()
    {
        await Button("base").ClickAsync();
        await Button("Insights").ClickAsync();
        await Expect(Page.Locator("#right")).ToContainTextAsync("No potential savings detected by heuristics.");
        await Button("Show files").ClickAsync();
        await Expect(Page.Locator("#right-title")).ToContainTextAsync("only paths with findings");
        await Expect(Page.Locator(".trow[data-path=\"node_modules\"]")).ToHaveCountAsync(0);
        await Button("Show all paths").ClickAsync();
        await Expect(Page.Locator("#right-title")).Not.ToContainTextAsync("only paths with findings");
        await Expect(Page.Locator(".trow[data-path=\"node_modules\"]")).ToHaveCountAsync(1);
        await Button("Back to insights").ClickAsync();
        await Expect(Page.Locator("#right-title")).ToContainTextAsync("Insights");
    }

    [Fact]
    public async Task InsightKeyboardSelectionAndActivation()
    {
        string html = fixture.Data.Html;
        JsonNode data = HtmlImageReportTests.Data(html);
        JsonArray findings = data["images"]![0]!["findings"]!.AsArray();
        JsonNode finding = findings[0]!.DeepClone();
        finding["title"] = "Second finding";
        finding["explain"] = new JsonArray("Second finding explanation.");
        findings.Add(finding);
        data["images"]![0]!["potentialBytes"] = 42;
        await LoadReportAsync(data, "two-findings.html");

        await Button("base").ClickAsync();
        await Expect(Page.Locator("#details")).ToContainTextAsync("2 findings involve this layer");
        await Button("Insights").ClickAsync();
        await Expect(Page.Locator("#right")).ToContainTextAsync(
            "42 B more potential savings \u00b7 verify your app does not need these files");
        ILocator pane = Page.Locator("#right-pane");
        await pane.FocusAsync();
        await pane.PressAsync("ArrowDown");
        ILocator first = Page.Locator(".insight-group[data-finding-index=\"0\"]");
        ILocator second = Page.Locator(".insight-group[data-finding-index=\"1\"]");
        await Expect(first).Not.ToHaveAttributeAsync("open", "");
        await Expect(second).ToHaveAttributeAsync("open", "");
        await Expect(second.Locator("summary")).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(Page.Locator("#details")).ToContainTextAsync("Second finding explanation.");
        await pane.PressAsync("Enter");
        await Expect(Page.Locator("#right-title")).ToContainTextAsync("only paths with findings");
        await Button("Back to insights").ClickAsync();
        await Expect(Page.Locator("#right-title")).ToContainTextAsync("Insights");
    }

    [Fact]
    public async Task MobileCanSwitchToLayerDetails()
    {
        await Page.SetViewportSizeAsync(320, 900);
        await Button("base").ClickAsync();
        await Expect(Page.Locator("#details-pane")).ToBeHiddenAsync();
        ILocator detailsToggle = Page.Locator("#header-actions .mobile-pane-toggle");
        await detailsToggle.ClickAsync();
        await Expect(Page.Locator("#details-pane")).ToBeVisibleAsync();
        await Expect(detailsToggle).ToBeFocusedAsync();
        await Expect(Page.Locator("#details")).ToContainTextAsync("file payload");
        await Expect(Page.Locator("#details")).ToContainTextAsync("sha256:");
        await Page.Locator("#layer-1").ClickAsync();
        await Expect(Page.Locator("#details-title")).ToHaveTextAsync("Layer 1");
        ILocator filesToggle = Page.Locator("#header-actions .mobile-pane-toggle");
        await Expect(filesToggle).ToHaveTextAsync("Files");
        await filesToggle.ClickAsync();
        await Expect(filesToggle).ToBeFocusedAsync();
        await Expect(Page.Locator("#details-pane")).ToBeHiddenAsync();
        await Expect(Page.Locator("#right-pane")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ComparisonLayoutHasGutterAtDesktopAndMobileWidths()
    {
        await Button("Differences").ClickAsync();
        foreach (int width in new[] { 320, 761, 1280 })
        {
            await Page.SetViewportSizeAsync(width, 900);
            JsonElement metrics = await Page.EvaluateAsync<JsonElement>(
                """
                async () => {
                    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
                    const pane = document.querySelector('#right-pane').getBoundingClientRect();
                    const summary = document.querySelector('.comparison-summary .push').getBoundingClientRect();
                    return { gap: pane.right - summary.right,
                      overflow: document.documentElement.scrollWidth > innerWidth };
                }
                """);
            Assert.True(metrics.GetProperty("gap").GetDouble() >= 7, $"Insufficient gutter at {width}px.");
            Assert.False(metrics.GetProperty("overflow").GetBoolean(), $"Page overflows at {width}px.");
            await Expect(Page.Locator(".comparison-file-body .tree-toggle").First).ToBeVisibleAsync();
        }
    }

    private async Task LoadReportAsync(JsonNode data, string fileName)
    {
        string html = fixture.Data.Html;
        using MemoryStream compressed = new();
        using (GZipStream gzip = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            JsonSerializer.Serialize(gzip, data);
        }
        const string prefix = "<script type=\"application/octet-stream\" id=\"report-data\">";
        int start = html.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        int end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        Assert.True(start >= prefix.Length && end > start);
        string modified = html[..start] + Convert.ToBase64String(compressed.ToArray()) + html[end..];
        string path = Path.Combine(fixture.DirectoryPath, fileName);
        await File.WriteAllTextAsync(path, modified, TestContext.Current.CancellationToken);
        await Page.GotoAsync(HtmlReportBrowserFixture.FileUri(path), new() { WaitUntil = WaitUntilState.NetworkIdle });
    }

    private ILocator Button(string name) => Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
}

public sealed class HtmlReportBrowserFixture : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"dredge-html-browser-{Guid.NewGuid():N}");
    private IPlaywright? playwright;
    private IBrowser? browser;
    private (string Html, List<Node> Tree)? data;

    internal static bool Enabled => Environment.GetEnvironmentVariable("DREDGE_HTML_BROWSER_TESTS") == "1";
    internal string DirectoryPath => directory;
    internal string ReportUri => FileUri(Path.Combine(directory, "report.html"));
    internal IBrowser Browser => browser ?? throw new InvalidOperationException("The browser was not initialized.");
    internal (string Html, List<Node> Tree) Data =>
        data ?? throw new InvalidOperationException("The report fixture was not initialized.");

    internal static string FileUri(string path) => new UriBuilder
    {
        Scheme = Uri.UriSchemeFile,
        Host = "",
        Path = path
    }.Uri.AbsoluteUri;

    public async ValueTask InitializeAsync()
    {
        if (!Enabled) return;

        try
        {
            data = await HtmlImageReportTests.CreateBrowserFixtureAsync(TestContext.Current.CancellationToken);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "report.html"), Data.Html,
                TestContext.Current.CancellationToken);
            playwright = await Playwright.CreateAsync();
            browser = await playwright.Chromium.LaunchAsync(new()
            {
                Headless = true,
                ExecutablePath = Environment.GetEnvironmentVariable("DREDGE_BROWSER_EXECUTABLE")
            });
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (browser is not null) await browser.CloseAsync();
        }
        finally
        {
            browser = null;
            playwright?.Dispose();
            playwright = null;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
