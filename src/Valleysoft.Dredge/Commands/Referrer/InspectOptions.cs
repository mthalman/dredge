using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Referrer;

public class InspectOptions : OptionsBase
{
    private readonly Argument<string> nameArgument;
    private readonly Argument<string> artifactDigestArgument;
    private readonly Option<ArtifactInspectOutput> outputOption;

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
        outputOption = Add(new Option<ArtifactInspectOutput>("--output")
        {
            Description = "Output format",
            DefaultValueFactory = _ => ArtifactInspectOutput.Summary
        });
    }

    protected override void GetValues()
    {
        Image = GetValue(nameArgument);
        ArtifactDigest = GetValue(artifactDigestArgument);
        OutputFormat = GetValue(outputOption);
    }
}
