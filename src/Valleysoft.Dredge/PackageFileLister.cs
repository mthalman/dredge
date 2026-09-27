using System.Text;

namespace Valleysoft.Dredge;

// Finds the files that belong to an installed package, so compare can show
// which of a package's files changed between two images.
internal static class PackageFileLister
{
    public static async Task<IReadOnlyList<string>> ListAsync(
        InstalledPackageEcosystem ecosystem,
        string name,
        IReadOnlyCollection<string> allPaths,
        Func<string, CancellationToken, Task<string?>> readText,
        CancellationToken cancellationToken)
    {
        switch (ecosystem)
        {
            case InstalledPackageEcosystem.NuGet:
                throw new NotSupportedException(
                    "NuGet dependency metadata identifies packages but does not establish deployed file ownership.");
            case InstalledPackageEcosystem.Npm:
                string marker = "node_modules/" + name + "/";
                return allPaths.Where(path =>
                    (path.StartsWith(marker, StringComparison.Ordinal) ||
                        path.Contains("/" + marker, StringComparison.Ordinal)) &&
                    !path[(path.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..]
                        .Contains("node_modules/", StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal).ToArray();
            case InstalledPackageEcosystem.Dpkg:
                string[] lists = allPaths
                    .Where(path => path.StartsWith("var/lib/dpkg/info/", StringComparison.Ordinal) &&
                        path.EndsWith(".list", StringComparison.Ordinal))
                    .Where(path =>
                    {
                        string file = path["var/lib/dpkg/info/".Length..^".list".Length];
                        int colon = file.IndexOf(':');
                        return (colon < 0 ? file : file[..colon]) == name;
                    }).Order(StringComparer.Ordinal).ToArray();
                HashSet<string> dpkgFiles = new(StringComparer.Ordinal);
                foreach (string list in lists)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    dpkgFiles.UnionWith(ParseDpkgList(await readText(list, cancellationToken) ?? ""));
                }
                return dpkgFiles.Order(StringComparer.Ordinal).ToArray();
            case InstalledPackageEcosystem.Apk:
                return ParseApkInstalled(await readText("lib/apk/db/installed", cancellationToken) ?? "", name);
            case InstalledPackageEcosystem.Pip:
                string normalized = NormalizePip(name);
                IEnumerable<string> records = allPaths.Where(path =>
                {
                    if (!path.EndsWith(".dist-info/RECORD", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    string directory = ImagePath.GetDirectoryName(path);
                    string folder = directory[(directory.LastIndexOf('/') + 1)..^".dist-info".Length];
                    int dash = folder.IndexOf('-');
                    return NormalizePip(dash < 0 ? folder : folder[..dash]) == normalized;
                });
                HashSet<string> pipFiles = new(StringComparer.Ordinal);
                foreach (string record in records.Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string root = ImagePath.GetDirectoryName(ImagePath.GetDirectoryName(record));
                    pipFiles.UnionWith(ParsePipRecord(await readText(record, cancellationToken) ?? "", root));
                }
                return pipFiles.Order(StringComparer.Ordinal).ToArray();
            default:
                return [];
        }
    }

    internal static IReadOnlyList<string> ParseDpkgList(string content) =>
        content.Split('\n')
            .Select(line => line.TrimEnd('\r').Trim('/'))
            .Where(line => line.Length > 0 && line != ".")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal static IReadOnlyList<string> ParseApkInstalled(string content, string name)
    {
        List<string> files = [];
        bool inPackage = false;
        string directory = "";
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                inPackage = false;
                continue;
            }
            if (line.StartsWith("P:", StringComparison.Ordinal))
            {
                inPackage = line[2..] == name;
                directory = "";
            }
            else if (inPackage && line.StartsWith("F:", StringComparison.Ordinal))
            {
                directory = line[2..].Trim('/');
            }
            else if (inPackage && line.StartsWith("R:", StringComparison.Ordinal))
            {
                files.Add(directory.Length == 0 ? line[2..] : directory + "/" + line[2..]);
            }
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<string> ParsePipRecord(string content, string root)
    {
        List<string> files = [];
        foreach (string path in ReadRecordPaths(content))
        {
            List<string> parts = path.StartsWith('/')
                ? [] : [.. root.Split('/', StringSplitOptions.RemoveEmptyEntries)];
            foreach (string part in path.Split('/'))
            {
                if (part == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }
                }
                else if (part is not ("." or ""))
                {
                    parts.Add(part);
                }
            }
            files.Add(string.Join('/', parts));
        }
        return files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> ReadRecordPaths(string content)
    {
        int position = 0;
        while (position < content.Length)
        {
            List<string> fields = [];
            bool moreFields;
            do
            {
                StringBuilder field = new();
                bool quoted = position < content.Length && content[position] == '"';
                if (quoted)
                {
                    position++;
                    bool closed = false;
                    while (position < content.Length)
                    {
                        char value = content[position++];
                        if (value != '"')
                        {
                            field.Append(value);
                        }
                        else if (position < content.Length && content[position] == '"')
                        {
                            field.Append('"');
                            position++;
                        }
                        else
                        {
                            closed = true;
                            break;
                        }
                    }
                    if (!closed || (position < content.Length && content[position] is not (',' or '\r' or '\n')))
                    {
                        throw new InvalidDataException("Python RECORD contains a malformed quoted CSV field.");
                    }
                }
                else
                {
                    while (position < content.Length && content[position] is not (',' or '\r' or '\n'))
                    {
                        char value = content[position++];
                        if (value == '"')
                        {
                            throw new InvalidDataException("Python RECORD contains a quote in an unquoted CSV field.");
                        }
                        field.Append(value);
                    }
                }
                fields.Add(field.ToString());
                moreFields = position < content.Length && content[position] == ',';
                if (moreFields)
                {
                    position++;
                }
            }
            while (moreFields);

            if (position < content.Length && content[position] == '\r')
            {
                position++;
            }
            if (position < content.Length && content[position] == '\n')
            {
                position++;
            }
            if (fields.Count == 1 && fields[0].Length == 0)
            {
                continue;
            }
            if (fields.Count != 3 || fields[0].Length == 0)
            {
                throw new InvalidDataException("Python RECORD requires three CSV fields and a nonempty path.");
            }
            yield return fields[0];
        }
    }

    private static string NormalizePip(string name) =>
        name.Replace('-', '_').Replace('.', '_').ToLowerInvariant();
}

internal enum DiffOp { Same, Delete, Insert }

internal sealed record DiffLine(DiffOp Op, int? OldLine, int? NewLine, string Text);

// Myers O(ND) line diff. Returns null when the edit distance exceeds maxEdits,
// so a pathological pair of files can't stall the UI.
internal static class TextDiff
{
    public static IReadOnlyList<DiffLine>? Diff(IReadOnlyList<string> a, IReadOnlyList<string> b, int maxEdits = 4000)
    {
        int n = a.Count, m = b.Count, max = Math.Min(n + m, maxEdits);
        int offset = max + 1;
        int[] v = new int[2 * max + 3];
        List<int[]> trace = [];
        int found = -1;
        for (int d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
            if (found >= 0)
            {
                break;
            }
        }
        if (found < 0)
        {
            return null;
        }

        List<DiffLine> result = [];
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            int[] pv = trace[d];
            int k = cx - cy;
            int prevK = k == -d || (k != d && pv[offset + k - 1] < pv[offset + k + 1]) ? k + 1 : k - 1;
            int prevX = pv[offset + prevK];
            int prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                cx--;
                cy--;
                result.Add(new(DiffOp.Same, cx + 1, cy + 1, a[cx]));
            }
            if (cx == prevX)
            {
                cy--;
                result.Add(new(DiffOp.Insert, null, cy + 1, b[cy]));
            }
            else
            {
                cx--;
                result.Add(new(DiffOp.Delete, cx + 1, null, a[cx]));
            }
        }
        while (cx > 0 && cy > 0)
        {
            cx--;
            cy--;
            result.Add(new(DiffOp.Same, cx + 1, cy + 1, a[cx]));
        }
        result.Reverse();
        return result;
    }
}
