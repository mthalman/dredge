using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Referrer;

public class ListOptions : BoundedListOptionsBase
{
    private readonly Argument<string> imageArg;
    private readonly Option<string> artifactTypeArg;

    public string Image { get; set; } = string.Empty;
    public string? ArtifactType { get; set; }

    public ListOptions()
    {
        imageArg = Add(new Argument<string>("image") { Description = "Container image reference (<image>, <image>:<tag>, or <image>@<digest>)" });
        artifactTypeArg = Add(new Option<string>("--artifact-type") { Description = "Artifact media type to filter by" });
    }

    protected override void GetValues()
    {
        Image = GetValue(imageArg);
        ArtifactType = GetValue(artifactTypeArg);
        GetBoundedListValues();
    }
}
