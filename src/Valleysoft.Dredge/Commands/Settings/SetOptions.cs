using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Settings;

public class SetOptions : OptionsBase
{
    private readonly Argument<string> settingArg;
    private readonly Argument<string> valueArg;

    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public SetOptions()
    {
        settingArg = Add(new Argument<string>("setting") { Description = "Setting name to set" });
        valueArg = Add(new Argument<string>("value") { Description = "Value to assign to the setting" });
    }

    protected override void GetValues()
    {
        Name = GetValue(settingArg);
        Value = GetValue(valueArg);
    }
}
