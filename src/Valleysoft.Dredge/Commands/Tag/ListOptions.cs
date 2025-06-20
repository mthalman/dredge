using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Tag;

public class ListOptions : OptionsBase
{
    private readonly Argument<string> repoArg;

    public string Repo { get; set; } = string.Empty;

    public ListOptions()
    {
        repoArg = Add(new Argument<string>("repo") { Description = "Name of the container repository" });
    }

    protected override void GetValues()
    {
        Repo = GetValue(repoArg);
    }
}
