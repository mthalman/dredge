using Terminal.Gui.Input;
using Valleysoft.Dredge.Commands;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;
using static Valleysoft.Dredge.Tests.WindowsImageTestData;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public sealed class ExplorerPresentationRegressionTests : IAsyncDisposable
{
    private readonly LayerCacheTestContext cache = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask DisposeAsync() => cache.DisposeAsync();

    private async Task<ExplorerImage> Load(Mock<IDockerRegistryClient> client)
    {
        ExplorerSource source = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), Name, new(), null, Token);
        ExplorerSession session = await ExplorerSession.CreateAsync(client.Object, source, cache.Store, null, Token);
        await session.EnsurePackagesAsync(Token);
        ExplorerImage image = ExplorerImage.FromSource(source);
        image.SetSession(session, ExplorerInsightsResult.Empty);
        return image;
    }

    [Fact]
    public async Task LeftArrowSelectsWindowsParentWithDifferentPathCasing()
    {
        ExplorerImage image = await Load(Client(
            Layer(File("Files/Aardvark/value", "first"), File("Files/App/old", "old")),
            Layer(File("Files/app/new", "new"))));
        await using ImageFileSystem files = image.Session!.Files;
        ExplorerState state = new() { Layer = 1, WholeFilesystem = true };
        state.Expanded.Add("App");
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, state,
            session => new FakeExplorerHost { Baseline = session }, out _);
        List<FlatRow> rows = ui.Window.Presenter.Flatten(state);
        int parent = ui.Window.Presenter.IndexOf(state, "App");
        Assert.True(parent > 0);
        Assert.Equal("App", rows[parent].Path);
        Assert.Contains(rows, row => row.Path == "app/new");
        ui.Window.Apply(new SetCursor(ui.Window.Presenter.IndexOf(state, "app/new")));

        ui.Press(Key.CursorLeft);

        Assert.Equal(parent, state.Cursor);
        Assert.Equal("App", ui.Window.Presenter.Flatten(state)[state.Cursor].Path);
        ui.Press(Key.CursorLeft);
        Assert.DoesNotContain("App", state.Expanded);
    }

    [Fact]
    public async Task UnsupportedWindowsParentNeverPublishesAnEffectiveTreeButRawLayersRemainAccessible()
    {
        Mock<IDockerRegistryClient> client = Client(
            Layer(Link("Files/App", @"D:\outside")),
            Layer(File("Files/App/value", "value")));
        ExplorerSource source = await ExplorerSource.OpenAsync(client.Object,
            Mock.Of<IDockerRegistryClientFactory>(), Name, new(), null, Token);
        ExplorerOptions options = new(null, null, false, ClipboardMode.Off, "unused", "");
        await using ExplorerApp explorer = new(client.Object, Mock.Of<IDockerRegistryClientFactory>(),
            source, cache.Store, options, Token);
        ExplorerState state = explorer.Start();
        using ExplorerUiHarness ui = new(150, 42, app => explorer.Attach(app, state));

        ui.Until(() =>
        {
            Assert.False(explorer.Image.IsAnalyzed(1));
            Assert.Null(explorer.Image.WholeTree(1));
            return explorer.Image.IsIndexed(0) && explorer.Image.IsIndexed(1) &&
                explorer.Image.SessionError is not null;
        }, "unsupported Windows parent failure and raw layer indexes");

        Assert.False(explorer.Image.Complete);
        Assert.Contains("App", explorer.Image.SessionError);
        Assert.Null(explorer.Image.Session);
        ui.Window.Apply(new SelectLayer(0));
        Assert.Equal("App", Assert.Single(ui.Window.Presenter.Flatten(state)).Path);
        ui.Press(Key.CursorDown);
        Assert.Equal(1, state.Layer);
        ui.Press(Key.Tab);
        ui.Press(Key.CursorRight);
        Assert.Contains(ui.Window.Presenter.Flatten(state), row => row.Path == "App/value");
        Assert.True(ui.Shows("value"), ui.Screen());
        ui.Window.Apply(new SetWhole(true));
        Assert.False(ui.Window.Presenter.WholeAvailable(state));
        Assert.Contains(ui.Window.Presenter.Flatten(state), row => row.Path == "App/value");
        Assert.False(explorer.Image.IsAnalyzed(1));
        Assert.Null(explorer.Image.WholeTree(1));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(150)]
    public async Task WindowsPackageInventoryIsExplicitlyUnavailableAndWarningsRemainAccessible(int width)
    {
        ExplorerImage image = await Load(Client(Layer(File("Files/App/value", "value"))));
        await using ImageFileSystem files = image.Session!.Files;
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(image, new() { Layer = 0 },
            session => new FakeExplorerHost
            {
                Baseline = session,
                PackagesWork = (_, _) => Task.FromResult(session!.Packages),
            }, out _, width: width);

        ui.Press(new Key('p'));
        ui.Until(() => ui.State.Packages is not null, "Windows package inventory");

        Assert.True(ui.Shows("Package inventory is"), ui.Screen());
        Assert.Contains("Windows installed-software", ui.State.Packages!.UnavailableReason);
        Assert.False(ui.Shows("0 packages detected"), ui.Screen());
        Assert.False(ui.Shows("No packages detected"), ui.Screen());
        Assert.Empty(ui.Window.Presenter.PackageRows(ui.State));
        Assert.True(ui.Shows("Alt+W"), ui.Screen());
        ui.Press(Key.W.WithAlt);
        Assert.Equal(RightView.Warning, ui.State.View);
        Assert.Contains("registry and service inspection are not implemented", ui.State.WarningText);
        ui.Press(Key.Esc);
        Assert.Equal(RightView.Packages, ui.State.View);
        Assert.True(ui.Shows("Package inventory is"), ui.Screen());
    }
}
