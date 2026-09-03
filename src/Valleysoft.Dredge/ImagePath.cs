namespace Valleysoft.Dredge;

internal static class ImagePath
{
    public static string NormalizeArchive(string value)
    {
        if (string.IsNullOrEmpty(value) || ContainsControlCharacter(value))
        {
            throw new InvalidDataException($"Invalid archive entry path '{value}'.");
        }
        if (IsAbsolute(value) || value.Contains('\\'))
        {
            throw new InvalidDataException(
                $"Archive entry path '{value}' must be a relative Linux path.");
        }
        return Normalize(value, allowParentSegments: false, "archive entry");
    }

    public static string NormalizeRequested(string? value)
    {
        value = string.IsNullOrEmpty(value) ? string.Empty : value;
        if (value.Contains('\\') || ContainsControlCharacter(value))
        {
            throw new InvalidDataException($"Invalid image path '{value}'.");
        }
        return Normalize(value.TrimStart('/'), allowParentSegments: false, "image");
    }

    public static string ResolveLinkTarget(
        string basePath,
        string target,
        string remainder,
        string linkPath)
    {
        if (target.Contains('\\') || ContainsControlCharacter(target))
        {
            throw new InvalidDataException(
                $"Link '/{linkPath}' has invalid target '{target}'.");
        }

        string combined = IsAbsolute(target)
            ? target.TrimStart('/')
            : Join(basePath, target);
        combined = Join(combined, remainder);
        return Normalize(combined, allowParentSegments: true, $"link target for '/{linkPath}'");
    }

    public static bool IsAbsolute(string path) => path.StartsWith('/');

    public static string GetDirectoryName(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    public static string GetFileName(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    public static void ValidateSegment(string value, string description)
    {
        if (string.IsNullOrEmpty(value) ||
            value is "." or ".." ||
            value.Contains('/') ||
            value.Contains('\\') ||
            ContainsControlCharacter(value))
        {
            throw new InvalidDataException($"Invalid {description} '{value}'.");
        }
    }

    private static string Normalize(
        string value,
        bool allowParentSegments,
        string description)
    {
        List<string> segments = [];
        foreach (string segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (!allowParentSegments)
                {
                    throw new InvalidDataException(
                        $"The {description} path '{value}' escapes the image root.");
                }
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                // Linux clamps excess ".." segments at the filesystem root.
                continue;
            }
            ValidateSegment(segment, $"{description} path segment");
            segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    private static string Join(string first, string second) =>
        first.Length == 0 ? second :
        second.Length == 0 ? first :
        $"{first}/{second}";

    internal static bool ContainsControlCharacter(string value) =>
        value.Any(char.IsControl);
}
