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

            await SaveImageLayersToDiskAsync(
                Options.BaseImage, BaseOutputDirName, Options.BaseLayerIndex, CompareOptionsBase.BaseArg, ct);
            Console.Error.WriteLine();
            await SaveImageLayersToDiskAsync(
                Options.TargetImage, TargetOutputDirName, Options.TargetLayerIndex, CompareOptionsBase.TargetArg, ct);

            string compareTempPath = Path.Combine(pathProvider.TempPath, "compare");
            string args = settings.FileCompareTool.Args
                .Replace("{0}", Path.Combine(compareTempPath, BaseOutputDirName))
                .Replace("{1}", Path.Combine(compareTempPath, TargetOutputDirName));
            ct.ThrowIfCancellationRequested();
            processLauncher.Start(new ProcessStartInfo(settings.FileCompareTool.ExePath, args));
        });
    }

    private Task SaveImageLayersToDiskAsync(
        string image,
        string outputDirName,
        int? layerIndex,
        string layerIndexArg,
        CancellationToken cancellationToken)
    {
        string workingDir = Path.Combine(pathProvider.TempPath, "compare", outputDirName);
        if (Directory.Exists(workingDir))
        {
            Directory.Delete(workingDir, recursive: true);
        }

        return ImageHelper.SaveImageLayersToDiskAsync(
            DockerRegistryClientFactory,
            image,
            workingDir,
            layerIndex,
            layerIndexArg + CompareFilesOptions.LayerIndexSuffix,
            noSquash: false,
            Options,
            cancellationToken,
            pathProvider);
    }
}
