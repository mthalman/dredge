using System.Diagnostics;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareFilesCommand : RegistryCommandBase<CompareFilesOptions>
{
    private const string BaseOutputDirName = "base";
    private const string TargetOutputDirName = "target";

    private readonly IAppSettingsStore settingsStore;
    private readonly IDredgePathProvider pathProvider;
    private readonly IProcessLauncher processLauncher;

    public CompareFilesCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : this(
            dockerRegistryClientFactory,
            new AppSettingsStore(),
            new DredgePathProvider(),
            new ProcessLauncher())
    {
    }

    internal CompareFilesCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        IAppSettingsStore settingsStore,
        IDredgePathProvider pathProvider,
        IProcessLauncher processLauncher)
        : base("files", "Compares two images by their files", dockerRegistryClientFactory)
    {
        this.settingsStore = settingsStore;
        this.pathProvider = pathProvider;
        this.processLauncher = processLauncher;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return ExecuteCommandAsync(registry: null, cancellationToken, async ct =>
        {
            AppSettings settings = settingsStore.Load();
            if (settings.FileCompareTool is null ||
                settings.FileCompareTool.ExePath == string.Empty ||
                settings.FileCompareTool.Args == string.Empty)
            {
                throw new Exception(
                    $"This command requires additional configuration.{Environment.NewLine}In order to compare files, you must first set the '{AppSettings.FileCompareToolName}' setting in {settingsStore.SettingsPath}. This is an external tool of your choosing that will be executed to compare two directories containing files of the specified images. Use '{{0}}' and '{{1}}' placeholders in the args to indicate the base and target path locations that will be the inputs to the compare tool.");
            }

            bool usesDaemon = ImageSourceReference.Parse(Options.BaseImage).IsDaemon ||
                ImageSourceReference.Parse(Options.TargetImage).IsDaemon;
            var sources = usesDaemon
                ? await OpenComparisonAsync(Options.BaseImage, Options.TargetImage, Options, ct, settingsStore,
                    ImageSourceRequirements.LinuxLayerContent)
                : ((ResolvedImage Base, ResolvedImage Target)?)null;
            await using ResolvedImage? baseSource = sources?.Base;
            await using ResolvedImage? targetSource = sources?.Target;
            if (sources is not null)
            {
                ReportComparison(baseSource!, targetSource!);
            }
            string workspace = Path.Combine(pathProvider.TempPath, "compare", Guid.NewGuid().ToString("N"));
            bool launched = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                CacheFileSystem.CreateDirectory(workspace);
                string basePath = Path.Combine(workspace, BaseOutputDirName);
                string targetPath = Path.Combine(workspace, TargetOutputDirName);
                await SaveImageLayersToDiskAsync(
                    Options.BaseImage,
                    basePath,
                    Options.BaseLayerIndex,
                    CompareFilesOptions.BaseLayerIndexOptionName,
                    ct, baseSource);
                Error.WriteLine();
                await SaveImageLayersToDiskAsync(
                    Options.TargetImage,
                    targetPath,
                    Options.TargetLayerIndex,
                    CompareFilesOptions.TargetLayerIndexOptionName,
                    ct, targetSource);

                string args = settings.FileCompareTool.Args
                    .Replace("{0}", basePath)
                    .Replace("{1}", targetPath);
                ct.ThrowIfCancellationRequested();
                processLauncher.Start(new ProcessStartInfo(settings.FileCompareTool.ExePath, args));
                launched = true;
                Error.WriteLine($"Comparison inputs retained in '{workspace}'. Close comparison tools before running 'dredge settings clear-cache' to remove them.");
            }
            finally
            {
                if (!launched && Directory.Exists(workspace))
                {
                    try
                    {
                        Directory.Delete(workspace, recursive: true);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Error.WriteLine($"Could not remove comparison workspace '{workspace}': {e.Message}");
                    }
                }
            }
        });
    }

    private Task SaveImageLayersToDiskAsync(
        string image,
        string workingDir,
        int? layerIndex,
        string layerIndexOptionName,
        CancellationToken cancellationToken,
        ResolvedImage? source)
    {
        return ImageHelper.SaveImageLayersToDiskAsync(
            DockerRegistryClientFactory,
            image,
            workingDir,
            layerIndex,
            layerIndexOptionName,
            noSquash: false,
            Options,
            cancellationToken,
            pathProvider,
            resolvedImage: source);
    }
}
