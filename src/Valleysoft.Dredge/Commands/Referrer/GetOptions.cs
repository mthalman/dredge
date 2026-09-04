using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Referrer;

public class GetOptions : OptionsBase
{
    private readonly Argument<string> imageArgument;
    private readonly Argument<string> artifactDigestArgument;
    private readonly Option<string> payloadOption;
    private readonly Option<string> outputOption;

    public string Image { get; set; } = string.Empty;
    public string ArtifactDigest { get; set; } = string.Empty;
    public string? Payload { get; set; }
    public string? OutputPath { get; set; }

    public GetOptions()
    {
        imageArgument = Add(new Argument<string>("image")
        {
            Description = "Container image reference (<image>, <image>:<tag>, or <image>@<digest>)"
        });
        artifactDigestArgument = Add(new Argument<string>("artifact-digest")
        {
            Description = "Digest of the artifact manifest"
        });
        payloadOption = Add(new Option<string>("--payload")
        {
            Description = "Payload index or digest (required when the artifact has multiple payloads)"
        });
        outputOption = Add(new Option<string>("--output")
        {
            Description = "File path for the payload; writes exact bytes to standard output when omitted"
        });
    }

    protected override void GetValues()
    {
        Image = GetValue(imageArgument);
        ArtifactDigest = GetValue(artifactDigestArgument);
        Payload = GetValue(payloadOption);
        OutputPath = GetValue(outputOption);
    }
}
