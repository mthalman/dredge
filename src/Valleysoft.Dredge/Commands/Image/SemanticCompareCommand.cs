namespace Valleysoft.Dredge.Commands.Image;

public class SemanticCompareCommand : RegistryCommandBase<CompareOptions>
{
    private readonly IDredgePathProvider? paths;
    public SemanticCompareCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output = null)
        : this(dockerRegistryClientFactory, output, null)
    {
    }

    internal SemanticCompareCommand(IDockerRegistryClientFactory dockerRegistryClientFactory, TextWriter? output, IDredgePathProvider? paths)
        : base(
            "semantic",
            "Compares effective image facts without a UI. Exit 0: equal, 2: different, 1: incomplete or failed.",
            dockerRegistryClientFactory, output)
    {
        this.paths = paths;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        CompareResult? result = null;
        await ExecuteCommandAsync(null, cancellationToken, async ct =>
        {
            CompareResult computed = await GetResultAsync(ct);
            if (Options.OutputFormat == SemanticCompareOutput.Json) { Output.WriteLine(JsonHelper.Serialize(computed)); }
            else
            {
                CompareTextRenderer.Write(
                    Output,
                    computed,
                    Options.Limit,
                    Options.IsColorDisabled || !ReferenceEquals(Output, Console.Out));
            }
            result = computed;
        });
        if (result is not null && result.ExitCode != 0) { Exit(result.ExitCode); }
    }

    public async Task<CompareResult> GetResultAsync(CancellationToken cancellationToken = default)
    {
        bool content = Options.Scopes.Any(static s => s is "files" or "packages" or "sizes");
        bool daemon = ImageSourceReference.Parse(Options.BaseImage).IsDaemon || ImageSourceReference.Parse(Options.TargetImage).IsDaemon;
        var acquired = await OpenComparisonAsync(Options.BaseImage, Options.TargetImage, Options, cancellationToken,
            requirements: content || daemon && Options.Scopes.Contains("layers") ? ImageSourceRequirements.LayerContent : ImageSourceRequirements.Metadata);
        await using ResolvedImage baseline = acquired.Base;
        await using ResolvedImage target = acquired.Target;
        await using LayerStore? store = content || daemon && Options.Scopes.Contains("layers") ? LayerStore.Create(paths) : null;
        return await ImageComparisonService.CompareAsync(baseline, target, Options, store, cancellationToken);
    }
}
