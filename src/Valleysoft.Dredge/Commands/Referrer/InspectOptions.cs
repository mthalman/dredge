using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Referrer;

public class InspectOptions : OptionsBase
{
    private readonly Argument<string> nameArgument;
    private readonly Argument<string> artifactDigestArgument;
    private readonly CliOutputOption<ArtifactInspectOutput> outputOption;

    public string Image { get; set; } = string.Empty;
    public string ArtifactDigest { get; set; } = string.Empty;
    public ArtifactInspectOutput OutputFormat { get; set; }

    public InspectOptions()
    {
        nameArgument = Add(new Argument<string>("name")
        {
            Description = "Name of the subject manifest (<name>, <name>:<tag>, or <name>@<digest>)"
        });
        artifactDigestArgument = Add(new Argument<string>("artifact-digest")
        {
            Description = "Digest of the artifact manifest"
        });
        outputOption = new CliOutputOption<ArtifactInspectOutput>(
            "Output format",
            ArtifactInspectOutput.Summary,
            ("summary", ArtifactInspectOutput.Summary),
            ("json", ArtifactInspectOutput.Json));
        Add(outputOption.Option);
    }

    protected override void GetValues()
    {
        Image = GetValue(nameArgument);
        ArtifactDigest = GetValue(artifactDigestArgument);
        OutputFormat = outputOption.GetValue(GetValue(outputOption.Option));
    }
}
