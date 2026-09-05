using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Tag;

public class ListOptions : BoundedListOptionsBase
{
    private readonly Argument<string> repositoryArg;

    public string Repo { get; set; } = string.Empty;

    public ListOptions()
    {
        repositoryArg = Add(new Argument<string>("repository") { Description = "Container repository name" });
    }

    protected override void GetValues()
    {
        Repo = GetValue(repositoryArg);
        GetBoundedListValues();
    }
}
