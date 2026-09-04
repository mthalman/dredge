using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Referrer;

public class InspectOptions : OptionsBase
{
    private readonly Argument<string> imageArgument;
    private readonly Argument<string> artifactDigestArgument;
    private readonly CliOutputOption<ArtifactInspectOutput> outputOption;

    public string Image { get; set; } = string.Empty;
    public string ArtifactDigest { get; set; } = string.Empty;
    public ArtifactInspectOutput OutputFormat { get; set; }

    public InspectOptions()
    {
        imageArgument = Add(new Argument<string>("image")
        {
            Description = "Container image reference (<image>, <image>:<tag>, or <image>@<digest>)"
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
        Image = GetValue(imageArgument);
        ArtifactDigest = GetValue(artifactDigestArgument);
        OutputFormat = outputOption.GetValue(GetValue(outputOption.Option));
    }
}
