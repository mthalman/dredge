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

    public static void ValidateLayer(int? layer, int layerCount)
    {
        if (layer is int value && (value < 0 || value >= layerCount))
        {
            throw new InvalidOperationException(
                layerCount == 0 ? "--layer can't be used with an image that has no layers."
                    : $"--layer must be between 0 and {layerCount - 1}.");
        }
    }
}
