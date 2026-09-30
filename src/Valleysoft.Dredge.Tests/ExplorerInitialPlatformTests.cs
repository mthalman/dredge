using Terminal.Gui.Input;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Tests;

[Collection(ExplorerUiCollection.Name)]
public class ExplorerInitialPlatformTests
{
    [Fact]
    public void PickerWithoutCurrentPlatformSelectsAnArchitectureVariant()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
        ExplorerPlatform v6 = new("linux", "arm", "v6", null);
        ExplorerPlatform v7 = new("linux", "arm", "v7", null);
        ExplorerPlatform? chosen = null;

        Assert.True(ui.AnswerDialog(
            () => chosen = PlatformPicker.Show(ui.App, [v6, v7], null),
            Key.CursorDown, Key.Enter));

        Assert.Equal(v7, chosen);
    }

    [Fact]
    public void InitialPickerHonorsCancellationBeforeOpeningATerminal()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PlatformPicker.ShowInitial([new("linux", "arm", "v7", null)], false, cancellation.Token));
    }

    [Fact]
    public void DismissingInitialSelectionDoesNotChooseTheFirstVariant()
    {
        using ExplorerUiHarness ui = ExplorerWindowTests.Open(out _);
        ExplorerPlatform? chosen = new("linux", "arm", "v6", null);

        Assert.True(ui.AnswerDialog(
            () => chosen = PlatformPicker.Show(ui.App,
                [new("linux", "arm", "v6", null), new("linux", "arm", "v7", null)], null),
            Key.Esc));

        Assert.Null(chosen);
    }
}
