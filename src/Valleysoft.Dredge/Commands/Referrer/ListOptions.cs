using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Referrer;

public class ListOptions : OptionsBase
{
    private readonly Argument<string> imageArg;
    private readonly Option<string> artifactTypeArg;

    public string Image { get; set; } = string.Empty;
    public string? ArtifactType { get; set; }

    public ListOptions()
    {
        imageArg = Add(new Argument<string>("name", "Name of the manifest (<name>, <name>:<tag>, or <name>@<digest>)"));
        artifactTypeArg = Add(new Option<string>("--artifact-type", "Artifact media type to filter by"));
    }

    protected override void GetValues()
    {
        Image = GetValue(imageArg);
        ArtifactType = GetValue(artifactTypeArg);
    }
}
