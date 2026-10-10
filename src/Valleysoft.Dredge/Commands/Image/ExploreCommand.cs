using Valleysoft.DockerRegistryClient;
using Valleysoft.Dredge.Explorer;
using Valleysoft.Dredge.Explorer.Tui;

namespace Valleysoft.Dredge.Commands.Image;

public sealed class ExploreCommand : RegistryCommandBase<ExploreOptions>
{
    public ExploreCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("explore", "Interactively explore image layers, files, and insights",
            dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ImageName image = ImageName.Parse(Options.Image);
        return ExecuteCommandAsync(image.Registry, cancellationToken, async ct =>
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                throw new InvalidOperationException(
                    "Image exploration requires an interactive terminal. Use 'dredge image ls' " +
                    "or 'dredge image compare' for redirected output.");
            }

            ExploreSettings settings = AppSettings.Load().Explore;
            ThemeKind theme = Theme.Parse(settings.Theme,
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")));
            ExplorerOptions explorerOptions = CreateExplorerOptions(Options, settings);

            using IDockerRegistryClient client =
                await DockerRegistryClientFactory.GetClientAsync(image.Registry, ct);
            await using LayerStore store = LayerStore.Create();
            Theme.Apply(theme);
            ExplorerSource source = await ExplorerSource.OpenAsync(
                client, DockerRegistryClientFactory, image, Options, Options.BaseImages, ct,
                choosePlatform: platforms => PlatformPicker.ShowInitial(platforms, explorerOptions.Mouse, ct));
            LayerIndexOption.ValidateLayer(Options.Layer, source.LayerCount);

            await using ExplorerApp app = new(client, DockerRegistryClientFactory, source, store, explorerOptions, ct);
            app.Run();
        }, operationTimeout: Timeout.InfiniteTimeSpan);
    }

    // Command-line flags win over settings: --no-mouse turns the mouse off even
    // when explore.mouse is on.
    internal static ExplorerOptions CreateExplorerOptions(ExploreOptions options, ExploreSettings settings) => new(
        options.Layer, options.Compare,
        Mouse: !options.NoMouse && settings.IsMouseEnabled(),
        Clipboard: Explorer.Tui.Clipboard.Resolve(OperatingSystem.IsWindows(),
            Explorer.Tui.Clipboard.IsRemoteSession(Environment.GetEnvironmentVariable)),
        ViewerExePath: string.IsNullOrWhiteSpace(settings.Viewer.ExePath)
            ? OperatingSystem.IsWindows() ? "cmd.exe" : "less"
            : settings.Viewer.ExePath,
        ViewerArgs: string.IsNullOrWhiteSpace(settings.Viewer.ExePath)
            ? OperatingSystem.IsWindows() ? "/d /s /c \"more < \"{0}\"\"" : "-X \"{0}\""
            : settings.Viewer.Args,
        ViewerUsesTerminal: settings.Viewer.UsesTerminal() || string.IsNullOrWhiteSpace(settings.Viewer.ExePath),
        PauseAfterViewer: string.IsNullOrWhiteSpace(settings.Viewer.ExePath));

    internal static ImageName ResolveCompareImage(ImageName baseline, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Compare image or tag cannot be empty.", nameof(input));
        }
        if (!input.Contains('/') && !input.Contains(':') && !input.Contains('@'))
        {
            string registry = baseline.Registry is null ? string.Empty : baseline.Registry + "/";
            return ImageName.Parse($"{registry}{baseline.Repo}:{input}");
        }
        return ImageName.Parse(input);
    }
}