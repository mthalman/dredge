using System.Globalization;
using System.Text;

namespace Valleysoft.Dredge;

internal static class TerminalText
{
    public static string EscapeControls(string value)
    {
        StringBuilder? escaped = null;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsControl(character))
            {
                escaped ??= new StringBuilder(value.Length).Append(value, 0, index);
                escaped.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                escaped?.Append(character);
            }
        }
        return escaped?.ToString() ?? value;
    }
}
