using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerReviewRegressionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Could not open the requested platform.")]
    public void BaseVerificationWarningRemainsVisibleWithoutReplacingStartupNotice(string? notice)
    {
        const string warning = "Annotated base could not be verified: connection refused";
        ExplorerImage image = new(ExplorerSamples.Reference, "linux/amd64", "sha256:m",
            ["sha256:a"], [100], null, null, null, baseWarning: warning);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new ExplorerState { Notice = notice },
            session => new FakeExplorerHost { Baseline = session }, out _);
        Assert.Equal(notice ?? warning, ui.State.Notice);
        if (notice is null)
        {
            Assert.True(ui.Shows(warning), ui.Screen());
        }
        ui.Window.Apply(new ShowView(RightView.Insights));
        ui.Pump();
        Assert.True(ui.Shows("Base verification warning - Alt+W for full details"), ui.Screen());
        ui.Press(new Key('w').WithAlt);
        Assert.Equal(RightView.Warning, ui.State.View);
        Assert.Equal(warning, ui.State.WarningText);
        Assert.True(ui.Shows("connection refused"), ui.Screen());
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Insights, ui.State.View);
    }

    [Fact]
    public void NormalSearchRestoresItsQueryAfterComparison()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
        ui.Press(new Key('/'));
        ui.Type("package.json");
        ui.Press(Key.Esc);
        ui.Window.StartCompare("2.0");
        ui.Until(() => ui.State.Compare is not null, "comparison");
        ui.Press(new Key('/'));
        ui.Type("left-pad");
        Assert.Equal("left-pad", ui.State.Compare!.SearchQuery);
        ui.Press(Key.Esc);
        ui.Press(Key.Esc);
        ui.Press(Key.Esc);
        Assert.Null(ui.State.Compare);
        ui.Press(new Key('/'));
        Assert.Equal("package.json", ui.State.SearchQuery);
        Assert.Equal(ui.State.SearchQuery, ui.Window.Search.Text);
        Assert.Contains(ui.Window.Presenter.SearchResults(ui.State).Hits, hit => hit.Path == "app/package.json");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiedPathsWithSpacesAreSingleQuoted(bool directory)
    {
        ExplorerPresenter presenter = new(ExplorerSamples.Image(), 150, 42);
        Assert.Equal($"dredge image {(directory ? "ls" : "cat")} {presenter.Image.ResolvedReference} '/app/a file.txt'" +
            (directory ? " --recursive" : ""), presenter.CopyCommandText(new ExplorerState(), "app/a file.txt", directory));
    }

    [Theory]
    [InlineData("/app/file", true, "/app/file")]
    [InlineData("/app/file", false, "/app/file")]
    [InlineData("", true, "''")]
    [InlineData("a'b", true, "'a''b'")]
    [InlineData("a'b", false, "'a'\\''b'")]
    [InlineData("$HOME; & `echo` \"x\"", true, "'$HOME; & `echo` \"x\"'")]
    [InlineData("$HOME; & `echo` \"x\"", false, "'$HOME; & `echo` \"x\"'")]
    public void ShellQuotingPreservesLiteralArguments(string value, bool powerShell, string expected) =>
        Assert.Equal(expected, ShellCommand.Quote(value, powerShell));

    [Fact]
    public async Task CopiedCommandRoundTripsThroughShellStringParsing()
    {
        const string path = "app/a 'file' \"$HOME\"; & `text`.txt";
        ExplorerPresenter presenter = new(ExplorerSamples.Image(), 150, 42);
        string command = presenter.CopyCommandText(new ExplorerState(), path, false);
        bool windows = OperatingSystem.IsWindows();
        System.Diagnostics.ProcessStartInfo start = new(windows ? "powershell.exe" : "/bin/sh")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (windows)
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
                "function dredge { foreach ($arg in $args) { [Console]::Write($arg); [Console]::Write([char]0) } }; " + command)));
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("dredge() { printf '%s\\000' \"$@\"; }; " + command);
        }
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await errors);
        Assert.Equal(["image", "cat", presenter.Image.ResolvedReference, "/" + path, ""], (await output).Split('\0'));
    }

    [Theory]
    [InlineData("2.0", "registry.test/shop/storefront:2.0")]
    [InlineData("other.test/team/app:2", "other.test/team/app:2")]
    [InlineData("other.test:5000/team/app@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "other.test:5000/team/app@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void CopiedComparisonUsesResolvedReferencesOnBothSides(string input, string target)
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out FakeExplorerHost host, clipboard: true);
        ui.Window.StartCompare(input);
        ui.Until(() => ui.State.Compare is not null, "comparison");
        ExplorerComparison comparison = ui.State.Compare!.Comparison;
        string before = ExplorerImage.DigestReference(comparison.Baseline.Image, comparison.Baseline.Resolved.ManifestInfo.DockerContentDigest);
        string after = ExplorerImage.DigestReference(ImageName.Parse(target), comparison.Target.Resolved.ManifestInfo.DockerContentDigest);
        ui.Press(new Key('y'));
        Assert.Equal($"dredge image compare files {before} {after}", host.Clipboard[0]);
        ui.Window.Apply(new SwapSides());
        ui.Press(new Key('y'));
        Assert.Equal($"dredge image compare files {after} {before}", host.Clipboard[1]);
    }

    [Theory]
    [InlineData("\u754c\u754cTAIL")]
    [InlineData("e\u0301TAIL")]
    [InlineData("\U0001F469\u200d\U0001F4BBTAIL")]
    public void UnicodePreviewPreservesTrailingText(string text)
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState
        {
            Layer = 2, View = RightView.Inspector, InspectPath = "app/package.json", Focus = FocusPane.Right
        }, session => new FakeExplorerHost
        {
            Baseline = session, Preview = path => new PreviewContent(path, null, [text], null, 20)
        }, out _);
        ui.Until(() => ui.State.Preview is not null, "Unicode preview");
        Assert.True(ui.Shows("TAIL"), ui.Screen());
    }

    [Fact]
    public void StyledLinesMeasureAndSliceDisplayCellsWithoutSplittingGraphemes()
    {
        Assert.Equal(8, Line.Of("\u754c\u754cTAIL").Length);
        Assert.Equal(" \u754cTA\u2026", Line.Of("\u754c\u754cTAIL").Slice(1, 6).ToString());
        Assert.Equal("e\u0301T\u2026", Line.Of("e\u0301TAIL").Truncate(3).ToString());
        Assert.Equal("", Line.Of("\u754c").Truncate(0).ToString());
        Assert.Equal(2, new Line().Add("\U0001F469\u200d").Add("\U0001F4BB").Length);
        Assert.Equal("\u754c  ", Line.Of("\u754c").Pad(4).ToString());
    }

    [Fact]
    public void WidePreviewCanPanAllTheWayToItsEnd()
    {
        string text = new string('\u754c', 100) + "TAIL";
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState
        {
            Layer = 2, View = RightView.Inspector, InspectPath = "app/package.json", Focus = FocusPane.Right
        }, session => new FakeExplorerHost
        {
            Baseline = session, Preview = path => new PreviewContent(path, null, [text], null, 304)
        }, out _, width: 80, height: 24);
        ui.Until(() => ui.State.Preview is not null, "wide preview");
        ui.Window.Apply(new PanText(1000));
        ui.Pump();
        Assert.Equal(204 - (ui.Window.Presenter.RightInner - 6), ui.State.PreviewColumn);
        Assert.True(ui.Shows("TAIL"), ui.Screen());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryReplacementFileCanBeInspectedWithoutLosingDeletedChildren(bool whole)
    {
        ExplorerImage image = ExplorerSamples.Custom(
        [
            ExplorerSamples.Layer([ExplorerSamples.File("app/item/old", 1, "old")]),
            ExplorerSamples.Layer([ExplorerSamples.File("app/item", 2, "new")], ["app/item"])
        ]);
        ExplorerState state = new() { Layer = 1, WholeFilesystem = whole, Focus = FocusPane.Right };
        state.Expanded.Add("app");
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            session => new FakeExplorerHost { Baseline = session }, out _);
        Node node = ExplorerImage.Find(ui.Window.Presenter.Tree(state), "app/item")!;
        Assert.Equal(Kind.File, node.Kind);
        Assert.Equal(2, node.Size);
        Assert.Equal(Change.Removed, Assert.Single(node.Children).Change);
        state.Cursor = ui.Window.Presenter.IndexOf(state, "app/item");
        ui.Window.Apply(new Activate());
        Assert.Equal(RightView.Inspector, state.View);
        Assert.Equal("app/item", state.InspectPath);
    }

    [Fact]
    public void EmptyImageRendersAndLayerCommandsRemainSafe()
    {
        ExplorerImage image = ExplorerSamples.Custom([]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new ExplorerState(),
            session => new FakeExplorerHost { Baseline = session }, out _);
        Assert.True(ui.Shows("empty filesystem"), ui.Screen());
        foreach (Cmd command in new Cmd[] { new StepLayer(1), new StepLayer(-1), new SelectLayer(0),
            new RetryLayer(0), new Jump(true), new SetWhole(true) })
        {
            ui.Window.Apply(command);
            ui.Pump();
        }
        Assert.DoesNotContain(ui.Window.Presenter.Hints(ui.State), hint => hint.Label == "Step layer");
        ui.Press(new Key('/'));
        ui.Type("nothing");
        ui.Press(Key.Esc);
        ui.Press(new Key('q'));
        Assert.True(ui.Window.StopRequested);
    }

    [Fact]
    public void EmptyImageComparisonHasNoLayerNavigation()
    {
        ExplorerImage image = ExplorerSamples.Custom([]);
        ExplorerState state = new()
        {
            Compare = new CompareState(ExplorerSession.Compare(image.Session!, image.Session!), "a", "b")
            {
                FocusLayers = true
            }
        };
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            session => new FakeExplorerHost { Baseline = session }, out _);
        foreach (Cmd command in new Cmd[] { new SelectLayer(0), new Move(1), new Jump(true), new Move(-1) })
        {
            ui.Window.Apply(command);
            ui.Pump();
            Assert.Equal(0, state.Compare!.Layer);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LifetimeCancellationStopsTheScreen(bool canceledBeforeOpen)
    {
        using CancellationTokenSource cts = new();
        if (canceledBeforeOpen)
        {
            cts.Cancel();
        }
        ExplorerImage image = ExplorerSamples.Image();
        using ExplorerUiHarness ui = new(150, 42, _ => new ExplorerWindow(image,
            new ExplorerState { Layer = 2 }, new FakeExplorerHost { Baseline = image.Session }, cts.Token));
        cts.Cancel();
        ui.Until(() => ui.Window.StopRequested, "lifetime cancellation");
        Assert.True(ui.Window.StopRequested);
    }
}
