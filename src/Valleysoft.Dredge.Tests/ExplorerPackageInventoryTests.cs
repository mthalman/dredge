using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerPackageInventoryTests
{
    private static InstalledPackageMetadata Inventory(string version = "1.0", bool warnings = false) =>
        new(Enum.GetValues<InstalledPackageEcosystem>().ToDictionary(ecosystem => ecosystem,
            ecosystem => new InstalledPackageEcosystemMetadata(InstalledPackageMetadataAvailability.Available,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    [ecosystem == InstalledPackageEcosystem.NuGet
                        ? "Microsoft.Extensions.Configuration.Binder" : ecosystem + "-example"] = [version, "extra-version"],
                })))
        {
            Diagnostics = warnings ? [new("broken/package.json", "Invalid package metadata")] : [],
        };

    private static ExplorerUiHarness Open(out FakeExplorerHost host,
        Func<int, CancellationToken, Task<InstalledPackageMetadata>>? work = null, int width = 150) =>
        ExplorerWindowTests.Open(ExplorerSamples.Image(), new ExplorerState { Layer = 0 },
            session => new FakeExplorerHost
            {
                Baseline = session,
                PackagesWork = work ?? ((layer, _) => Task.FromResult(Inventory($"{layer}.0"))),
            }, out host, width: width, height: 40);

    [Fact]
    public void PackagesLoadOnDemandAndFollowTheSelectedLayer()
    {
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host);
        Assert.Empty(host.PackageRequests);
        ui.Press(new Key('i'));
        Assert.Empty(host.PackageRequests);
        ui.Press(Key.Esc);
        ui.Press(new Key('k'));
        ui.Until(() => ui.State.Packages is not null, "layer package inventory");
        Assert.Equal([0], host.PackageRequests);
        Assert.Equal(RightView.Packages, ui.State.View);
        Assert.True(ui.Shows("through layer 0"), ui.Screen());
        Assert.Empty(host.Compared);

        ui.Press(new Key(']'));
        ui.Until(() => ui.State.Packages?.Ecosystems[InstalledPackageEcosystem.Npm].Packages["Npm-example"][0] == "1.0",
            "next layer inventory");
        Assert.Equal([0, 1], host.PackageRequests);
        Assert.True(ui.Shows("through layer 1"), ui.Screen());
        ui.Press(Key.Tab);
        ui.Press(Key.CursorDown);
        ui.Until(() => ui.State.PackagesLayer == 2 && ui.State.Packages is not null, "layer-list package navigation");
        Assert.Equal(2, ui.State.Layer);
        ui.Press(Key.Tab);
        ui.Press(new Key('?'));
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Packages, ui.State.View);
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Files, ui.State.View);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(150)]
    public void PackagesCollapseFilterAndShowFullNameAndVersions(int width)
    {
        using ExplorerUiHarness ui = Open(out _, width: width);
        ui.Press(new Key('k'));
        ui.Until(() => ui.State.Packages is not null, "packages");
        List<PackageInventoryRow> Rows() => ui.Window.Presenter.PackageRows(ui.State);
        ui.Window.Apply(new SetCursor(Rows().FindIndex(row => !row.IsGroup && row.Ecosystem == InstalledPackageEcosystem.NuGet)));
        Assert.True(ui.Shows("Microsoft.Extensions.Configuration.Binder"), ui.Screen());
        ui.Window.Apply(new SetCursor(Rows().FindIndex(row => row.IsGroup && row.Ecosystem == InstalledPackageEcosystem.NuGet)));
        ui.Press(Key.Enter);
        Assert.DoesNotContain(Rows(), row => !row.IsGroup && row.Ecosystem == InstalledPackageEcosystem.NuGet);
        ui.Press(Key.CursorRight);
        Assert.Contains(Rows(), row => !row.IsGroup && row.Ecosystem == InstalledPackageEcosystem.NuGet);
        ui.Press(Key.CursorLeft);
        ui.Press(new Key('/'));
        ui.Type("Configuration.Binder");
        Assert.Single(Rows(), row => !row.IsGroup);
        ui.Press(Key.Enter);
        ui.Window.Apply(new SetCursor(Rows().FindIndex(row => !row.IsGroup)));
        ui.Press(Key.Enter);
        Assert.Equal(RightView.Warning, ui.State.View);
        Assert.True(ui.Shows("Microsoft.Extensions.Configuration.Binder"), ui.Screen());
        Assert.True(ui.Shows("0.0, extra-version"), ui.Screen());
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Packages, ui.State.View);
        ui.Press(Key.Esc);
        Assert.Empty(ui.State.PackageQuery);
        Assert.DoesNotContain(Rows(), row => !row.IsGroup && row.Ecosystem == InstalledPackageEcosystem.NuGet);
        ui.Press(new Key('/'));
        ui.Type("extra-version");
        Assert.Equal(5, Rows().Count(row => !row.IsGroup));
    }

    [Fact]
    public void PackageErrorsCanBeRetriedAndPartialWarningsRemainAccessible()
    {
        int attempts = 0;
        using ExplorerUiHarness ui = Open(out _, (_, _) => ++attempts == 1
            ? Task.FromException<InstalledPackageMetadata>(new IOException("registry offline"))
            : Task.FromResult(Inventory(warnings: true)));
        ui.Press(new Key('k'));
        ui.Until(() => ui.State.PackagesError is not null, "package failure");
        Assert.True(ui.Shows("registry offline"), ui.Screen());
        ui.Press(new Key('r'));
        ui.Until(() => ui.State.Packages is not null, "package retry");
        Assert.Null(ui.State.PackagesError);
        Assert.True(ui.Shows("metadata warning"), ui.Screen());
        ui.Press(new Key('w').WithAlt);
        Assert.True(ui.Shows("/broken/package.json: Invalid package metadata"), ui.Screen());
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Packages, ui.State.View);
        Assert.True(ui.Shows("metadata warning"), ui.Screen());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LatePackageResultsCannotReplaceAnotherLayerOrReopenTheView(bool leave)
    {
        TaskCompletionSource<InstalledPackageMetadata> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken original = default;
        using ExplorerUiHarness ui = Open(out FakeExplorerHost host, (layer, token) =>
        {
            if (layer == 0)
            {
                original = token;
                return pending.Task;
            }
            return Task.FromResult(Inventory("new"));
        });
        ui.Press(new Key('k'));
        ui.Until(() => host.PackageRequests.Count == 1, "pending inventory");
        Task first = ui.Window.PackageTask;
        ui.Press(leave ? Key.Esc : new Key(']'));
        Assert.True(original.IsCancellationRequested);
        if (!leave)
        {
            ui.Until(() => ui.State.Packages is not null, "new inventory");
        }
        pending.SetResult(Inventory("old"));
        ui.Until(() => first.IsCompleted, "abandoned inventory completion");
        ui.Pump();
        Assert.Equal(leave ? RightView.Files : RightView.Packages, ui.State.View);
        if (!leave)
        {
            Assert.Equal("new", ui.State.Packages!.Ecosystems[InstalledPackageEcosystem.Npm].Packages["Npm-example"][0]);
        }
        else
        {
            Assert.Null(ui.State.Packages);
        }
    }

    [Fact]
    public void PackageKeyCanBeRemapped()
    {
        KeyMap keys = KeyMap.FromSettings(new ExploreKeysSettings { Packages = "K" });
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out FakeExplorerHost host, keys: keys);
        ui.Press(new Key('k'));
        Assert.Equal(RightView.Files, ui.State.View);
        ui.Press(new Key('K'));
        ui.Until(() => ui.State.Packages is not null, "remapped inventory");
        Assert.Single(host.PackageRequests);
    }

    [Fact]
    public void IncompleteImagesDoNotScanUntilTheSessionIsReady()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out FakeExplorerHost host, complete: false);
        ui.Press(new Key('k'));
        Assert.True(ui.Shows("once every layer is indexed"), ui.Screen());
        Assert.Empty(host.PackageRequests);
        Assert.Equal(RightView.Packages, ui.State.View);
    }

    [Fact]
    public void RestoredPackagesViewStartsLoadingAfterAttachment()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(ExplorerSamples.Image(),
            new ExplorerState { Layer = 0, View = RightView.Packages },
            session => new FakeExplorerHost { Baseline = session }, out FakeExplorerHost host);
        ui.Until(() => ui.State.Packages is not null, "restored package inventory");
        Assert.Single(host.PackageRequests);
    }

    [Fact]
    public void PackageRowsPageAndRetainSelectionAcrossDetails()
    {
        InstalledPackageMetadata metadata = new(Enum.GetValues<InstalledPackageEcosystem>().ToDictionary(
            ecosystem => ecosystem, ecosystem => new InstalledPackageEcosystemMetadata(
                InstalledPackageMetadataAvailability.Available,
                Enumerable.Range(0, 100).ToDictionary(i => $"package-{i:D3}", _ => (IReadOnlyList<string>)["1.0"]))));
        using ExplorerUiHarness ui = Open(out _, (_, _) => Task.FromResult(metadata), width: 80);
        ui.Press(new Key('k'));
        ui.Until(() => ui.State.Packages is not null, "large inventory");
        ui.Press(Key.End);
        Assert.True(ui.State.PackageScroll > 0);
        Assert.True(ui.Shows("package-099"), ui.Screen());
        int cursor = ui.State.PackageCursor;
        ui.Press(Key.Enter);
        ui.Press(Key.Esc);
        Assert.Equal(cursor, ui.State.PackageCursor);
        ui.Press(Key.PageUp);
        Assert.True(ui.State.PackageCursor < cursor);
        ui.Press(Key.Home);
        Assert.Equal(0, ui.State.PackageCursor);
        Assert.Equal(0, ui.State.PackageScroll);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyPackageTypesAreHidden(bool includePackages)
    {
        InstalledPackageMetadata metadata = new(Enum.GetValues<InstalledPackageEcosystem>().ToDictionary(
            ecosystem => ecosystem, ecosystem => new InstalledPackageEcosystemMetadata(
                ecosystem == InstalledPackageEcosystem.NuGet || includePackages && ecosystem == InstalledPackageEcosystem.Npm
                    ? InstalledPackageMetadataAvailability.Available : InstalledPackageMetadataAvailability.Unavailable,
                includePackages && ecosystem == InstalledPackageEcosystem.Npm
                    ? new Dictionary<string, IReadOnlyList<string>> { ["example"] = ["1.0"] }
                    : new Dictionary<string, IReadOnlyList<string>>())));
        using ExplorerUiHarness ui = Open(out _, (_, _) => Task.FromResult(metadata));
        ui.Press(new Key('k'));
        ui.Until(() => ui.State.Packages is not null, "empty inventory");
        List<PackageInventoryRow> rows = ui.Window.Presenter.PackageRows(ui.State);
        Assert.DoesNotContain(rows, row => row.Ecosystem != InstalledPackageEcosystem.Npm);
        if (includePackages)
        {
            Assert.Single(rows, row => row.IsGroup);
            Assert.True(ui.Shows("example"), ui.Screen());
        }
        else
        {
            Assert.Empty(rows);
            Assert.True(ui.Shows("No packages detected at this layer."), ui.Screen());
            Assert.False(ui.Shows("Esc clears the filter"), ui.Screen());
        }
        ui.Press(new Key('/'));
        ui.Type("NuGet");
        Assert.Empty(ui.Window.Presenter.PackageRows(ui.State));
        Assert.True(ui.Shows("No matching packages"), ui.Screen());
    }
}
