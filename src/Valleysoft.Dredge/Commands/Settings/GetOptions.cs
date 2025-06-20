using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Settings;

public class GetOptions : OptionsBase
{
    private readonly Argument<string> nameArg;

    public string Name { get; set; } = string.Empty;

    public GetOptions()
    {
        nameArg = Add(new Argument<string>("name") { Description = "Name of the setting to set" });
    }

    protected override void GetValues()
    {
        Name = GetValue(nameArg);
    }
}
