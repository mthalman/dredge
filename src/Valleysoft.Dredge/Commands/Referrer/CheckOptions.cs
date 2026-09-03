using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Referrer;

public class CheckOptions : OptionsBase
{
    private readonly Argument<string> nameArgument;
    private readonly Option<string[]> artifactTypeOption;
    private readonly Option<CheckOutput> outputOption;

    public string Image { get; set; } = string.Empty;
    public string[] ArtifactTypes { get; set; } = [];
    public CheckOutput OutputFormat { get; set; }

    public CheckOptions()
    {
        nameArgument = Add(new Argument<string>("name")
        {
            Description = "Name of the subject manifest (<name>, <name>:<tag>, or <name>@<digest>)"
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
        Image = GetValue(nameArgument);
        ArtifactTypes = GetValue(artifactTypeOption) ?? [];
        OutputFormat = GetValue(outputOption);
    }
}
