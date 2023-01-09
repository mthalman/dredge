using Spectre.Console.Rendering;
using System.Text;

namespace Valleysoft.Dredge.Tests;

public static class TestHelper
{
    public static string GetString(IEnumerable<Segment> segments)
    {
        StringBuilder builder = new();
        foreach (Segment segment in segments)
        {
            builder.Append(segment.Text);
        }
        return builder.ToString();
    }

    public static string Normalize(string val) =>
        val.Replace("\r", string.Empty).TrimEnd();
}
