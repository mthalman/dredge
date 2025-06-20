using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Repo;

public class ListOptions : OptionsBase
{
    private readonly Argument<string> registryArg;

    public string Registry { get; set; } = string.Empty;

    public ListOptions()
    {
        registryArg = Add(new Argument<string>("repo") { Description = "Name of the container registry" });
    }

    protected override void GetValues()
    {
        Registry = GetValue(registryArg);
    }
}
