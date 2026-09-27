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
            ExplorerSource source = await ExplorerSource.OpenAsync(
                client, DockerRegistryClientFactory, image, Options, Options.BaseImages, ct);
            ValidateLayer(Options.Layer, source.LayerCount);

            Theme.Apply(theme);
            while (true)
            {
                ExplorerExit exit;
                await using (ExplorerApp app = new(client, DockerRegistryClientFactory, source, store, explorerOptions, ct))
                {
                    exit = app.Run();
                }
                if (exit.Kind != ExplorerExitKind.Platform || exit.Platform is null)
                {
                    return;
                }
                try
                {
                    source = await ExplorerSource.OpenAsync(client, DockerRegistryClientFactory, image,
                        ExplorerSource.ForPlatform(exit.Platform), Options.BaseImages, ct,
                        exactPlatform: exit.Platform);
                    explorerOptions = explorerOptions with { Layer = null, Compare = null, Notice = null };
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Stay in the explorer on the platform that already works.
                    explorerOptions = explorerOptions with
                    {
                        Layer = null, Compare = null,
                        Notice = $"Could not open {exit.Platform}: {exception.Message}"
                    };
                }
            }
        });
    }

    // Command-line flags win over settings: --no-mouse turns the mouse off even
    // when explore.mouse is on.
    internal static ExplorerOptions CreateExplorerOptions(ExploreOptions options, ExploreSettings settings) => new(
        options.Layer, options.Compare,
        Mouse: !options.NoMouse && settings.IsMouseEnabled(),
        Clipboard: Explorer.Tui.Clipboard.Resolve(OperatingSystem.IsWindows(),
            Explorer.Tui.Clipboard.IsRemoteSession(Environment.GetEnvironmentVariable)),
        Keys: KeyMap.FromSettings(settings.Keys),
        ViewerExePath: string.IsNullOrWhiteSpace(settings.Viewer.ExePath)
            ? OperatingSystem.IsWindows() ? "cmd.exe" : "less"
            : settings.Viewer.ExePath,
        ViewerArgs: string.IsNullOrWhiteSpace(settings.Viewer.ExePath)
            ? OperatingSystem.IsWindows() ? "/d /s /c \"more < \"{0}\"\"" : "-X \"{0}\""
            : settings.Viewer.Args,
        ViewerUsesTerminal: settings.Viewer.UsesTerminal() || string.IsNullOrWhiteSpace(settings.Viewer.ExePath),
        PauseAfterViewer: string.IsNullOrWhiteSpace(settings.Viewer.ExePath));

    internal static void ValidateLayer(int? layer, int layerCount)
    {
        if (layer is int value && (value < 0 || value >= layerCount))
        {
            throw new InvalidOperationException(
                layerCount == 0 ? "--layer can't be used with an image that has no layers."
                    : $"--layer must be between 0 and {layerCount - 1}.");
        }
    }

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