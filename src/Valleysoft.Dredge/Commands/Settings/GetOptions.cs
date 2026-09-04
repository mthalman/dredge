using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Settings;

public class GetOptions : OptionsBase
{
    private readonly Argument<string> settingArg;

    public string Name { get; set; } = string.Empty;

    public GetOptions()
    {
        settingArg = Add(new Argument<string>("setting") { Description = "Setting name to get" });
    }

    protected override void GetValues()
    {
        Name = GetValue(settingArg);
    }
}
