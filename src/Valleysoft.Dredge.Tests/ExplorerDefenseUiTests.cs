using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerDefenseUiTests
{
    [Fact]
    public void CopiedFileCommandsUseResolvedManifestIdentity()
    {
        ExplorerImage image = ExplorerSamples.Image(digest: "sha256:resolvedarmv7");
        ExplorerPresenter presenter = new(image, 150, 42);
        foreach (bool directory in new[] { false, true })
        {
            string command = presenter.CopyCommandText(new(), "app/item", directory);
            Assert.Contains("registry.test/shop/storefront@sha256:resolvedarmv7", command);
            Assert.DoesNotContain("storefront:1.0", command);
        }
    }

    [Fact]
    public void CopiedComparisonCommandsPreserveBothResolvedRepositoriesAfterSwap()
    {
        ExplorerImage image = ExplorerSamples.Image();
        ExplorerSession target = ExplorerSamples.Target("other.test/team/app:rolling");
        ExplorerComparison comparison = ExplorerSession.Compare(image.Session!, target);
        ExplorerState state = new() { Compare = new CompareState(comparison, "before", "after") };
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            session => new FakeExplorerHost { Baseline = session }, out _);
        string before = new ImageName(image.Session!.Image.Registry, image.Session.Image.Repo, null,
            image.Session.Resolved.ManifestInfo.DockerContentDigest).ToString();
        string after = new ImageName(target.Image.Registry, target.Image.Repo, null,
            target.Resolved.ManifestInfo.DockerContentDigest).ToString();

        ui.Window.Apply(new CopyCommand());
        Assert.Equal($"dredge image compare files {before} {after}", ui.Window.CommandText.Text);
        ui.Press(Key.Esc);
        ui.Window.Apply(new SwapSides());
        ui.Window.Apply(new CopyCommand());
        Assert.Equal($"dredge image compare files {after} {before}", ui.Window.CommandText.Text);
    }

    [Fact]
    public void LongBaseWarningLeavesFindingsVisibleAndCompleteDetailsAccessible()
    {
        ExplorerImage sample = ExplorerSamples.Image();
        string warning = string.Concat(Enumerable.Repeat("base verification failed; ", 80)) +
            new string('x', 350) + " COMPLETE-WARNING-END";
        ExplorerImage image = new(sample.Reference, sample.Platform, sample.Digest, sample.LayerDigests,
            sample.LayerDownloads, ExplorerSamples.History(), sample.BaseLayerCount, sample.BaseName, warning);
        LayerChanges[] layers = ExplorerSamples.Layers();
        for (int i = 0; i < layers.Length; i++)
        {
            image.SetIndexed(i, layers[i]);
        }
        image.SetSession(sample.Session!, sample.Insights);
        ExplorerState state = new() { View = RightView.Insights };
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            session => new FakeExplorerHost { Baseline = session }, out _, width: 80, height: 24);

        Assert.True(ui.Shows("Alt+W for full details"), ui.Screen());
        Assert.True(ui.Shows(ui.Window.Presenter.VisibleFindings(state)[0]!.Title), ui.Screen());
        ui.Press(Key.CursorDown);
        int finding = state.Finding;
        Assert.True(finding > 0);
        ui.Press(new Key('w').WithAlt);
        Assert.Equal(RightView.Warning, state.View);
        Assert.Equal(warning, string.Concat(ui.Window.Presenter.WarningLines().Select(line => line.ToString())));
        Assert.All(ui.Window.Presenter.WarningLines(), line => Assert.True(line.Length <= ui.Window.Presenter.RightInner));
        ui.Press(Key.End);
        Assert.True(state.WarningScroll > 0);
        Assert.True(ui.Shows("WARNING-END"), ui.Screen());
        ui.Press(Key.Home);
        Assert.Equal(0, state.WarningScroll);
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Insights, state.View);
        Assert.Equal(finding, state.Finding);
    }

    [Fact]
    public void PayloadAccountingIsNotLabeledAsDiskAllocation()
    {
        ExplorerImage image = ExplorerSamples.Image();
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new ExplorerState { Layer = 2 },
            session => new FakeExplorerHost { Baseline = session }, out _);
        Assert.Contains("file payload", ui.Screen());
        Assert.DoesNotContain("on disk", ui.Screen());
        CompareState compare = new(ExplorerSession.Compare(image.Session!, ExplorerSamples.Target()), "before", "after");
        string header = string.Join("\n", new CompareView(ui.Window.Presenter, compare).Header());
        Assert.Contains("file payload", header);
        Assert.Contains("to download", header);
        Assert.DoesNotContain("on disk", header);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyFindingsDistinguishBaseChurnFromNoHiddenBytes(bool churn)
    {
        ExplorerImage image = ExplorerSamples.Custom(churn
            ? [ExplorerSamples.Layer([ExplorerSamples.File("etc/passwd", 100, "a")]),
                ExplorerSamples.Layer([ExplorerSamples.File("etc/passwd", 120, "b")])]
            : [ExplorerSamples.Layer([ExplorerSamples.File("etc/passwd", 100, "a")])], baseLayerCount: 1);
        Assert.Empty(image.Findings);
        ExplorerPresenter presenter = new(image, 80, 24) { FullWidthContent = true };
        string text = string.Join("\n", presenter.InsightsPane(new()).Lines);
        Assert.Contains(churn ? "No actionable findings" : "No hidden bytes.", text);
        if (churn)
        {
            Assert.Contains("100 B", text);
            Assert.DoesNotContain("Every shipped file is visible", text);
        }
    }

    [Fact]
    public void EmptyImageAuxiliaryKeysAreDispatched()
    {
        ExplorerImage image = ExplorerSamples.Custom([]);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new ExplorerState(),
            session => new FakeExplorerHost { Baseline = session }, out _, width: 80, height: 24);
        ui.Press(new Key('?'));
        Assert.Equal(RightView.Keys, ui.State.View);
        ui.Press(Key.CursorDown);
        Assert.True(ui.State.KeysScroll > 0);
        ui.Press(Key.End);
        Assert.True(ui.State.KeysScroll > 1);
        ui.Press(Key.Esc);
        ui.Press(new Key('/'));
        bool deleted = ui.State.SearchIncludeDeleted;
        bool exact = ui.State.SearchExactCase;
        ui.Press(new Key('d').WithAlt);
        ui.Press(new Key('c').WithAlt);
        Assert.Equal(!deleted, ui.State.SearchIncludeDeleted);
        Assert.Equal(!exact, ui.State.SearchExactCase);
        Assert.DoesNotContain(ui.Window.Presenter.Hints(ui.State), hint => hint.Cmd is SetSearchScope);
    }

    [Fact]
    public void EmptyImageRetainsPlatformPickerHint()
    {
        ExplorerPresenter presenter = new(ExplorerSamples.Custom([]), 80, 24) { MultiPlatform = true };
        Assert.Contains(presenter.Hints(new()), hint => hint.Cmd is PickPlatform);
        Assert.DoesNotContain(presenter.Hints(new()), hint => hint.Cmd is SetWhole or FirstUserLayer or RetryLayer);
        ExplorerPlatform amd = new("linux", "amd64", null, null);
        ExplorerPlatform arm = new("linux", "arm64", null, null);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Custom([]), new ExplorerState(),
            session => new FakeExplorerHost { Baseline = session, Platforms = [amd, arm], Platform = amd }, out _);
        char key = KeyMap.Default.Label(KeyAction.Platform)[0];
        Assert.True(ui.AnswerDialog(() => ui.Press(new Key(key)), Key.Esc));
    }

    [Theory]
    [InlineData("""{"key":"escaped \" quote","slash":"\\"}""")]
    [InlineData("""{"escaped \" key":true,"n":-1.25e+3}""")]
    [InlineData("""{"key": "unfinished\""")]
    [InlineData("\"")]
    [InlineData("\"abc\\")]
    [InlineData(" \t{\"a\":null} trailing \"")]
    [InlineData("{\"emoji\":\"\ud83d\udc1f\",\"wide\":\"\u754c\"}")]
    public void JsonHighlightingPreservesEverySourceCharacter(string source) =>
        Assert.Equal(source, Syntax.Json(source).ToString());

    [Theory]
    [InlineData("RUN echo \"unterminated")]
    [InlineData("RUN echo 'unterminated")]
    [InlineData("RUN echo \"escaped \\\" quote\" && echo \ud83d\udc1f")]
    [InlineData("RUN  --mount=type=cache,target=/cache \t echo [x]; a | b \\\n next")]
    public void DockerfileHighlightingPreservesEverySourceCharacter(string source) =>
        Assert.Equal(source, string.Concat(Syntax.Dockerfile(source, Theme.Foam).Select(token => token.Text)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FilteredReplacementRetainsVisibleRemovedDescendants(bool whole)
    {
        ExplorerImage image = ReplacementImage();
        ExplorerState state = new() { Layer = 1, WholeFilesystem = whole };
        state.Expanded.UnionWith(["app", "app/item"]);
        state.Hidden.UnionWith([Change.Added, Change.Modified]);
        ExplorerPresenter presenter = new(image, 100, 30);

        List<FlatRow> rows = presenter.Flatten(state);

        Assert.Contains(rows, row => row.Path == "app/item/old" && row.Node.Change == Change.Removed);
        Node replacement = Assert.Single(rows, row => row.Path == "app/item").Node;
        Assert.Equal(Kind.File, replacement.Kind);
        Assert.Equal(2, replacement.Size);
    }

    [Fact]
    public void ComparisonReplacementKeepsOwnIdentitySearchAndDiffAlongsideHistoricalChildren()
    {
        ExplorerImage baseline = ExplorerSamples.Custom(
            [ExplorerSamples.Layer([ExplorerSamples.File("app/item/old", 7, "old")])]);
        ExplorerImage target = ReplacementImage();
        ExplorerComparison comparison = ExplorerSession.Compare(baseline.Session!, target.Session!);
        ExplorerState state = new() { Compare = new CompareState(comparison, "before", "after") };
        CompareState compare = state.Compare;
        compare.Expanded.Add("file:app");
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(target, state,
            session => new FakeExplorerHost { Baseline = session }, out FakeExplorerHost host);
        CompareView view = new(ui.Window.Presenter, compare);
        List<CompareRow> rows = view.Rows();
        CompareRow replacement = Assert.Single(rows, row => row.Path == "app/item");
        ExplorerFileDifference difference = Assert.Single(comparison.Files, file => file.Path == "app/item");
        Assert.Equal(CompareRowKind.File, replacement.Kind);
        Assert.Equal(ExplorerImage.ToChange(difference.Kind), replacement.Change);
        Assert.Equal(difference.Baseline?.Size, replacement.Before);
        Assert.Equal(2, replacement.After);
        Assert.True(replacement.Expandable);
        compare.Cursor = rows.IndexOf(replacement);

        ui.Press(Key.CursorRight);
        Assert.Contains("file:app/item", compare.Expanded);
        rows = new CompareView(ui.Window.Presenter, compare).Rows();
        Assert.Contains(rows, row => row.Path == "app/item/old" && row.Change == Change.Removed);
        CompareRow parent = Assert.Single(rows, row => row.Path == "app");
        Assert.Equal(7, parent.Before);
        Assert.Equal(2, parent.After);
        ui.Press(Key.Enter);
        ui.Until(() => compare.Diff is not null, "replacement file diff");
        Assert.Equal("app/item", Assert.Single(host.Diffed));
        ui.Press(Key.Esc);
        compare.SearchQuery = "app/item";
        rows = new CompareView(ui.Window.Presenter, compare).Rows();
        Assert.Contains(rows, row => row.Path == "app/item" && row.Kind == CompareRowKind.File);
        Assert.Contains(rows, row => row.Path == "app/item/old");
    }

    private static ExplorerImage ReplacementImage() => ExplorerSamples.Custom(
    [
        ExplorerSamples.Layer([ExplorerSamples.File("app/item/old", 7, "old")]),
        ExplorerSamples.Layer([ExplorerSamples.File("app/item", 2, "new")], ["app/item"])
    ]);
}
