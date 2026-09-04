using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Referrer;

public class CheckOptions : OptionsBase
{
    private readonly Argument<string> imageArgument;
    private readonly Option<string[]> artifactTypeOption;
    private readonly Option<CheckOutput> outputOption;

    public string Image { get; set; } = string.Empty;
    public string[] ArtifactTypes { get; set; } = [];
    public CheckOutput OutputFormat { get; set; }

    public CheckOptions()
    {
        imageArgument = Add(new Argument<string>("image")
        {
            Description = "Container image reference (<image>, <image>:<tag>, or <image>@<digest>)"
        });
        artifactTypeOption = Add(new Option<string[]>("--artifact-type")
        {
            Description = "Required artifact media type; may be specified multiple times",
            Required = true,
            AllowMultipleArgumentsPerToken = false
        });
        artifactTypeOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string[]>().Any(string.IsNullOrWhiteSpace))
            {
                result.AddError("Artifact types cannot be empty or whitespace.");
            }
        });
        outputOption = Add(new Option<CheckOutput>("--output")
        {
            Description = "Output format",
            DefaultValueFactory = _ => CheckOutput.Summary
        });
    }

    protected override void GetValues()
    {
        Image = GetValue(imageArgument);
        ArtifactTypes = GetValue(artifactTypeOption) ?? [];
        OutputFormat = GetValue(outputOption);
    }
}
