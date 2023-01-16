using System.Diagnostics;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareFilesCommand : CommandWithOptions<CompareFilesOptions>
{
    private const string BaseOutputDirName = "base";
    private const string TargetOutputDirName = "target";

    private static readonly string CompareTempPath = Path.Combine(DredgeState.DredgeTempPath, "compare");

    public CompareFilesCommand(IDockerRegistryClientFactory dockerRegistryClientFactory)
        : base("files", "Compares two images by their files", dockerRegistryClientFactory)
    {
    }

    protected override Task ExecuteAsync()
    {
        return CommandHelper.ExecuteCommandAsync(registry: null, async () =>
        {
            AppSettings settings = AppSettings.Load();
            if (settings.FileCompareTool is null ||
                settings.FileCompareTool.ExePath == string.Empty ||
                settings.FileCompareTool.Args == string.Empty)
            {
                throw new Exception(
                    $"This command requires additional configuration.{Environment.NewLine}In order to compare files, you must first set the '{AppSettings.FileCompareToolName}' setting in {AppSettings.SettingsPath}. This is an external tool of your choosing that will be executed to compare two directories containing files of the specified images. Use '{{0}}' and '{{1}}' placeholders in the args to indicate the base and target path locations that will be the inputs to the compare tool.");
            }

            await SaveImageLayersToDiskAsync(Options.BaseImage, BaseOutputDirName, Options.BaseLayerIndex, CompareOptionsBase.BaseArg);
            Console.Error.WriteLine();
            await SaveImageLayersToDiskAsync(Options.TargetImage, TargetOutputDirName, Options.TargetLayerIndex, CompareOptionsBase.TargetArg);

            string args = settings.FileCompareTool.Args
                .Replace("{0}", Path.Combine(CompareTempPath, BaseOutputDirName))
                .Replace("{1}", Path.Combine(CompareTempPath, TargetOutputDirName));
            Process.Start(settings.FileCompareTool.ExePath, args);
        });
    }

    private Task SaveImageLayersToDiskAsync(string image, string outputDirName, int? layerIndex, string layerIndexArg)
    {
        string workingDir = Path.Combine(CompareTempPath, outputDirName);
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
            Options);
    }
}
