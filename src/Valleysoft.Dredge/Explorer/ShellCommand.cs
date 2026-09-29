namespace Valleysoft.Dredge.Explorer;

internal static class ShellCommand
{
    public static string Quote(string value) => Quote(value, OperatingSystem.IsWindows());

    internal static string Quote(string value, bool powerShell)
    {
        if (value.Length > 0 && value.All(static c => char.IsAsciiLetterOrDigit(c) || "_./:@=-".Contains(c)))
        {
            return value;
        }
        return "'" + value.Replace("'", powerShell ? "''" : "'\\''") + "'";
    }
}
