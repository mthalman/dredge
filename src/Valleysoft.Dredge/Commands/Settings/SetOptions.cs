using System.CommandLine;

namespace Valleysoft.Dredge.Commands.Settings;

public class SetOptions : OptionsBase
{
    private readonly Argument<string> nameArg;
    private readonly Argument<string> valueArg;

    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public SetOptions()
    {
        nameArg = Add(new Argument<string>("name") { Description = "Name of the setting to set" });
        valueArg = Add(new Argument<string>("value") { Description = "Value to assign to the setting" });
    }

    protected override void GetValues()
    {
        Name = GetValue(nameArg);
        Value = GetValue(valueArg);
    }
}
