using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerDefenseUiTests
{
    [Fact]
    public async Task RealHostPickerStatisticsRemainVisibleAlongsideSnapshotAnnotations()
    {
        await using ExplorerDefenseHostTests fixture = new();
        var (image, choices) = await fixture.PickerChoicesAsync();
        Assert.All(choices, choice => Assert.Contains("cached session snapshot", choice.Note));
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _, width: 80, height: 24);
        var (dialog, _, fill, _) = TagPicker.Create(image, "");
        using (dialog)
        {
            fill(choices, null);
            Assert.Contains(dialog.SubViews.OfType<Terminal.Gui.Views.Label>(),
                label => label.Text.Contains("cached session snapshot", StringComparison.Ordinal));
            Assert.True(ui.InDialog(() => ui.App.Run(dialog),
                DialogStep.When("picker rows", () => ui.Shows("compatible"), () =>
                {
                    int first = ui.Find("compatible").Y;
                    for (int i = 0; i < choices.Count; i++)
                    {
                        TagChoice choice = choices[i];
                        string row = ui.Row(first + i);
                        Assert.Contains($"{choice.Shared} of {choice.LayerCount} shared", row);
                        Assert.Contains(Fmt.SizeShort(choice.AdditionalDownload!.Value), row);
                        Assert.Contains("to download", row);
                    }
                    Assert.True(ui.Shows("cached session snapshot"), ui.Screen());
                    Assert.True(ui.Shows("reopen explorer to refresh"), ui.Screen());
                    ui.Send(Key.CursorDown);
                }),
                DialogStep.When("current identity annotation", () => ui.Shows("▌current"), () =>
                {
                    Assert.True(ui.Shows("same digest"), ui.Screen());
                    Assert.True(ui.Shows("cached session snapshot"), ui.Screen());
                    ui.Send(Key.CursorDown);
                }),
                DialogStep.When("unrelated image annotation", () => ui.Shows("▌unrelated"), () =>
                {
                    Assert.True(ui.Shows("different base image"), ui.Screen());
                    Assert.True(ui.Shows("reopen explorer to refresh"), ui.Screen());
                    ui.Send(Key.Esc);
                })));
        }
    }

    [Fact]
    public void TagPickerLabelsCurrentImageByDigestRatherThanTagSpelling()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
        ExplorerImage image = ui.Window.Presenter.Image;
        var (dialog, _, fill, _) = TagPicker.Create(image, "");
        using (dialog)
        {
            fill(
            [
                new TagChoice("1.0") { Digest = "sha256:moved", Shared = 0, LayerCount = 2, AdditionalDownload = 100 },
                new TagChoice("alias") { Digest = image.Digest },
            ], null);
            Assert.True(ui.InDialog(() => ui.App.Run(dialog),
                DialogStep.When("resolved tag identities", () => ui.Shows("current image"), () =>
                {
                    int moved = ui.Find("▌1.0 ").Y;
                    int alias = ui.Find(" alias ").Y;
                    Assert.True(moved >= 0 && alias >= 0, ui.Screen());
                    Assert.DoesNotContain("current image", ui.Row(moved));
                    Assert.Contains("current image", ui.Row(alias));
                    ui.Send(Key.Esc);
                })));
        }
    }

    [Fact]
    public void ComparisonSnapshotShowsAuthoritativeIdentitiesAndRefreshPolicy()
    {
        ExplorerImage image = ExplorerSamples.Image();
        ExplorerSession target = ExplorerSamples.Target("other.test/team/app:rolling");
        CompareState compare = new(ExplorerSession.Compare(image.Session!, target), "before", "after");
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new ExplorerState { Compare = compare },
            session => new FakeExplorerHost { Baseline = session }, out _, width: 80, height: 24);
        ui.Press(new Key('i').WithAlt);
        Assert.Equal(RightView.Warning, ui.State.View);
        Assert.Equal("Comparison snapshot", ui.State.WarningTitle);
        Assert.Contains(ExplorerImage.DigestReference(image.Session!.Image,
            image.Session.Resolved.ManifestInfo.DockerContentDigest), ui.State.WarningText);
        Assert.Contains(ExplorerImage.DigestReference(target.Image,
            target.Resolved.ManifestInfo.DockerContentDigest), ui.State.WarningText);
        Assert.True(ui.Shows("Reopen explorer to refresh tags."), ui.Screen());
        ui.Press(Key.Esc);
        Assert.Same(compare, ui.State.Compare);
        Assert.Equal(RightView.Files, ui.State.View);
        ui.Window.Apply(new SwapSides());
        ui.Press(new Key('i').WithAlt);
        Assert.StartsWith("Baseline (after)\nother.test/team/app@", ui.State.WarningText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialAndUnavailablePackageOwnershipExposeCompleteWarnings(bool unavailable)
    {
        ExplorerImage image = ExplorerSamples.Image();
        CompareState compare = new(ExplorerSession.Compare(image.Session!, ExplorerSamples.Target()), "before", "after");
        ExplorerPackageDifference package = Assert.Single(compare.Comparison.Packages);
        string warning = "Baseline /var/lib/dpkg/info/package.list: " + new string('x', 300) + " OWNERSHIP-END";
        PackageFilesContent result = new(package, unavailable ? null : [("app/package.json", Change.Modified)],
            unavailable ? "Package file ownership is unavailable." : "Ownership is incomplete; showing readable metadata.",
            unavailable ? 0 : 1, [warning]);
        compare.PackageFiles = result;
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new ExplorerState { Compare = compare },
            session => new FakeExplorerHost { Baseline = session }, out _, width: 80, height: 24);
        Assert.True(ui.Shows(unavailable ? "ownership is unavailable" : "Ownership is incomplete"), ui.Screen());
        Assert.True(ui.Shows("1 metadata warning"), ui.Screen());
        if (!unavailable)
        {
            Assert.True(ui.Shows("app/package.json"), ui.Screen());
        }
        ui.Press(new Key('w').WithAlt);
        Assert.Equal(warning, ui.State.WarningText);
        ui.Press(Key.End);
        Assert.True(ui.Shows("OWNERSHIP-END"), ui.Screen());
        ui.Press(Key.Esc);
        Assert.Same(result, compare.PackageFiles);
        Assert.True(ui.Shows("1 metadata warning"), ui.Screen());
    }

    [Fact]
    public void PackageMetadataWarningsRemainAvailableAfterNavigation()
    {
        ExplorerImage image = ExplorerSamples.Image();
        string message = string.Concat(Enumerable.Repeat("Skipped unreadable manifest; ", 100)) + "DIAGNOSTIC-END";
        ExplorerSession baseline = ExplorerSamples.Session(ExplorerSamples.Digests, ExplorerSamples.Layers(), [],
            diagnostics: [new("app/node_modules/pkg/package.json", message)]);
        CompareState compare = new(ExplorerSession.Compare(baseline, ExplorerSamples.Target()), "before", "after");
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new ExplorerState { Compare = compare },
            session => new FakeExplorerHost { Baseline = session }, out _, width: 80, height: 24);
        Assert.True(ui.Shows("1 metadata warning"), ui.Screen());
        ui.Press(Key.CursorDown);
        Assert.True(ui.Shows("1 metadata warning"), ui.Screen());
        int cursor = compare.Cursor;
        ui.Press(new Key('w').WithAlt);
        Assert.Equal(RightView.Warning, ui.State.View);
        Assert.Contains("Baseline /app/node_modules/pkg/package.json: " + message, ui.State.WarningText);
        ui.Press(Key.End);
        Assert.True(ui.Shows("DIAGNOSTIC-END"), ui.Screen());
        ui.Press(Key.Esc);
        Assert.Same(compare, ui.State.Compare);
        Assert.Equal(cursor, compare.Cursor);
        Assert.True(ui.Shows("1 metadata warning"), ui.Screen());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClosedWindowsRejectQueuedAndLateActionCallbacks(bool dispose, bool queued)
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
        TaskCompletionSource<string> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        bool delivered = false;
        Task work = ui.Window.RunAsync(ct =>
        {
            token = ct;
            started.SetResult(true);
            return pending.Task;
        }, _ => delivered = true);
        Assert.True(SpinWait.SpinUntil(() => started.Task.IsCompleted, TimeSpan.FromSeconds(10)));
        if (queued)
        {
            pending.SetResult("queued");
            Assert.True(SpinWait.SpinUntil(() => work.IsCompleted, TimeSpan.FromSeconds(10)));
        }
        if (dispose)
        {
            ui.Window.Dispose();
        }
        else
        {
            ui.Window.Apply(new Quit());
        }
        if (!queued)
        {
            pending.SetResult("late");
            Assert.True(SpinWait.SpinUntil(() => work.IsCompleted, TimeSpan.FromSeconds(10)));
        }
        bool drained = false;
        ui.App.Invoke(() => drained = true);
        ui.Input.ProcessQueue();
        ui.App.TimedEvents?.RunTimers();
        Assert.True(drained);
        Assert.False(delivered);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void NewerDiffWinsWhenEarlierRequestFinishesLast()
    {
        ExplorerImage image = ExplorerSamples.Image();
        CompareState compare = new(ExplorerSession.Compare(image.Session!, ExplorerSamples.Target()), "before", "after");
        compare.Expanded.UnionWith(["file:app", "file:app/dist"]);
        TaskCompletionSource<TextDiffContent> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<TextDiffContent> second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        System.Collections.Concurrent.ConcurrentDictionary<string, CancellationToken> tokens = new();
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new ExplorerState { Compare = compare },
            session => new FakeExplorerHost
            {
                Baseline = session,
                DiffWork = (_, path, token) =>
                {
                    tokens[path] = token;
                    return path == "app/package.json" ? first.Task : second.Task;
                }
            }, out _);
        StartDiff(ui, "app/package.json");
        Task firstWork = ui.Window.DiffTask;
        ui.Until(() => tokens.Count == 1, "first diff request");
        StartDiff(ui, "app/dist/main.js");
        Task secondWork = ui.Window.DiffTask;
        ui.Until(() => tokens.Count == 2, "second diff request");
        second.SetResult(new("app/dist/main.js", [], null));
        Drain(ui, secondWork);
        Assert.Equal("app/dist/main.js", compare.Diff?.Path);
        first.SetResult(new("app/package.json", [], null));
        Drain(ui, firstWork);
        Assert.Equal("app/dist/main.js", compare.Diff?.Path);
        Assert.True(tokens["app/package.json"].IsCancellationRequested);
    }

    [Fact]
    public void BackCancelsPendingDiffWithoutLeavingComparison()
    {
        ExplorerImage image = ExplorerSamples.Image();
        CompareState compare = new(ExplorerSession.Compare(image.Session!, ExplorerSamples.Target()), "before", "after");
        compare.Expanded.Add("file:app");
        TaskCompletionSource<TextDiffContent> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new ExplorerState { Compare = compare },
            session => new FakeExplorerHost { Baseline = session, DiffWork = (_, _, _) => pending.Task }, out FakeExplorerHost host);
        StartDiff(ui, "app/package.json");
        Task work = ui.Window.DiffTask;
        ui.Until(() => host.Diffed.Count == 1, "pending diff");
        ui.Press(Key.Esc);
        pending.SetResult(new("app/package.json", [], null));
        Drain(ui, work);
        Assert.Same(compare, ui.State.Compare);
        Assert.Null(compare.Diff);
    }

    [Theory]
    [InlineData("back")]
    [InlineData("move")]
    [InlineData("swap")]
    [InlineData("search")]
    public void NavigationRejectsAlreadyQueuedDiffCallbacks(string navigation)
    {
        ExplorerImage image = ExplorerSamples.Image();
        CompareState compare = new(ExplorerSession.Compare(image.Session!, ExplorerSamples.Target()), "before", "after");
        compare.Expanded.Add("file:app");
        TaskCompletionSource<TextDiffContent> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new ExplorerState { Compare = compare },
            session => new FakeExplorerHost
            {
                Baseline = session,
                DiffWork = (_, _, ct) => { token = ct; return pending.Task; }
            }, out FakeExplorerHost host);
        StartDiff(ui, "app/package.json");
        ui.Until(() => host.Diffed.Count == 1 && token.CanBeCanceled, "pending diff");
        pending.SetResult(new("app/package.json", [], null));
        // Keep the UI callback queued until navigation has invalidated its request.
        Assert.True(SpinWait.SpinUntil(() => ui.Window.DiffTask.IsCompleted, TimeSpan.FromSeconds(10)));
        ui.Window.Apply(navigation switch
        {
            "back" => new Back(),
            "move" => new Move(1),
            "swap" => new SwapSides(),
            _ => new ShowView(RightView.Search),
        });
        ui.Pump();
        Assert.True(token.IsCancellationRequested);
        Assert.Null(compare.Diff);
    }

    private static void StartDiff(ExplorerUiHarness ui, string path)
    {
        List<CompareRow> rows = new CompareView(ui.Window.Presenter, ui.State.Compare!).Rows();
        int cursor = rows.FindIndex(row => row.Path == path);
        Assert.True(cursor >= 0);
        ui.Window.Apply(new SetCursor(cursor));
        ui.Window.Apply(new Activate());
    }

    private static void Drain(ExplorerUiHarness ui, Task work)
    {
        ui.Wait(async () => { await work; return true; }, "queued diff completion");
        ui.Pump();
    }

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
    public void EmptyImageRetainsPackagesHint()
    {
        ExplorerPresenter presenter = new(ExplorerSamples.Custom([]), 80, 24);
        Assert.Contains(presenter.Hints(new()), hint => hint.Key == "p" && hint.Cmd is ShowView { View: RightView.Packages });
        Assert.DoesNotContain(presenter.Hints(new()), hint => hint.Cmd is SetWhole or RetryLayer);
        ExplorerPlatform amd = new("linux", "amd64", null, null);
        ExplorerPlatform arm = new("linux", "arm64", null, null);
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Custom([]), new ExplorerState(),
            session => new FakeExplorerHost { Baseline = session, Platforms = [amd, arm], Platform = amd }, out _);
        ui.Press(new Key('p'));
        Assert.Equal(RightView.Packages, ui.State.View);
        Assert.False(ui.Window.StopRequested);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComparisonReplacementKeepsOwnIdentitySearchAndDiffAlongsideHistoricalChildren(bool fileToDirectory)
    {
        ExplorerImage baseline = ExplorerSamples.Custom(
            [ExplorerSamples.Layer([
                ExplorerSamples.File("app/item", 0, "") with { Type = ImageFileType.Directory, ContentHash = null },
                ExplorerSamples.File("app/item/old", 7, "old")])]);
        ExplorerImage target = ReplacementImage();
        if (fileToDirectory)
        {
            (baseline, target) = (target, baseline);
        }
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
        Assert.Equal(fileToDirectory ? 2L : (long?)null, replacement.Before);
        Assert.Equal(fileToDirectory ? (long?)null : 2L, replacement.After);
        Assert.True(replacement.Expandable);
        compare.Cursor = rows.IndexOf(replacement);

        ui.Press(Key.CursorRight);
        Assert.Contains("file:app/item", compare.Expanded);
        rows = new CompareView(ui.Window.Presenter, compare).Rows();
        Assert.Contains(rows, row => row.Path == "app/item/old" &&
            row.Change == (fileToDirectory ? Change.Added : Change.Removed));
        CompareRow parent = Assert.Single(rows, row => row.Path == "app");
        Assert.Equal(fileToDirectory ? 2 : 7, parent.Before);
        Assert.Equal(fileToDirectory ? 7 : 2, parent.After);
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
