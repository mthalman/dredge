using System.CommandLine;
using System.Globalization;

namespace Valleysoft.Dredge.Commands.Image;

internal static class LayerIndexOption
{
    public static Option<int?> Create(string name, string description)
    {
        Option<int?> option = new(name)
        {
            Description = $"Zero-based {description}"
        };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count is 1 &&
                int.TryParse(
                    result.Tokens[0].Value,
                    NumberStyles.Integer,
                    CultureInfo.CurrentCulture,
                    out int value) &&
                value < 0)
            {
                result.AddError($"Layer index for option '{name}' must be zero or greater.");
            }
        });

        return option;
    }
}
