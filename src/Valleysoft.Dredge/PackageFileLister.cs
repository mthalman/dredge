using System.Text;
using Valleysoft.DockerRegistryClient;

namespace Valleysoft.Dredge;

// Finds the files that belong to an installed package, so compare can show
// which of a package's files changed between two images.
internal static class PackageFileLister
{
    public static IReadOnlyList<string> ListNpm(IReadOnlyList<string> roots, IEnumerable<string> allPaths)
    {
        string[] prefixes = [.. roots.Select(root => root + "/")];
        return [.. allPaths.Where(path => prefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.Ordinal) &&
            !path[prefix.Length..].Contains("node_modules/", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)];
    }

    public static async Task<IReadOnlyList<string>> ListAsync(
        InstalledPackageEcosystem ecosystem,
        string name,
        IReadOnlyCollection<string> allPaths,
        Func<string, CancellationToken, Task<string?>> readText,
        CancellationToken cancellationToken,
        Action<string, Exception>? onError = null)
    {
        async Task<IReadOnlyList<string>> ReadListAsync(string path, Func<string, IReadOnlyList<string>> parse)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string? content = await readText(path, cancellationToken);
                if (content is null && onError is not null)
                {
                    throw new FileNotFoundException($"Ownership metadata '/{path}' is unavailable.");
                }
                return parse(content ?? "");
            }
            catch (Exception exception) when (onError is not null &&
                exception is IOException or InvalidDataException or HttpRequestException or RegistryException or
                    UnauthorizedAccessException or DecoderFallbackException or NotSupportedException)
            {
                onError(path, exception);
                return [];
            }
        }

        switch (ecosystem)
        {
            case InstalledPackageEcosystem.NuGet:
                throw new NotSupportedException(
                    "NuGet dependency metadata identifies packages but does not establish deployed file ownership.");
            case InstalledPackageEcosystem.Dpkg:
                string[] lists = [.. allPaths
                    .Where(path => path.StartsWith("var/lib/dpkg/info/", StringComparison.Ordinal) &&
                        path.EndsWith(".list", StringComparison.Ordinal))
                    .Where(path =>
                    {
                        string file = path["var/lib/dpkg/info/".Length..^".list".Length];
                        int colon = file.IndexOf(':');
                        return (colon < 0 ? file : file[..colon]) == name;
                    }).Order(StringComparer.Ordinal)];
                HashSet<string> dpkgFiles = new(StringComparer.Ordinal);
                foreach (string list in lists)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    dpkgFiles.UnionWith(await ReadListAsync(list, ParseDpkgList));
                }
                return [.. dpkgFiles.Order(StringComparer.Ordinal)];
            case InstalledPackageEcosystem.Apk:
                return await ReadListAsync("lib/apk/db/installed", content => ParseApkInstalled(content, name));
            case InstalledPackageEcosystem.Pip:
                string normalized = InstalledPackageReader.NormalizePipName(name);
                IEnumerable<string> records = allPaths.Where(path =>
                {
                    if (!path.EndsWith(".dist-info/RECORD", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    string directory = ImagePath.GetDirectoryName(path);
                    string folder = directory[(directory.LastIndexOf('/') + 1)..^".dist-info".Length];
                    int dash = folder.IndexOf('-');
                    return InstalledPackageReader.NormalizePipName(dash < 0 ? folder : folder[..dash]) == normalized;
                });
                HashSet<string> pipFiles = new(StringComparer.Ordinal);
                foreach (string record in records.Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string root = ImagePath.GetDirectoryName(ImagePath.GetDirectoryName(record));
                    pipFiles.UnionWith(await ReadListAsync(record, content => ParsePipRecord(content, root)));
                }
                return [.. pipFiles.Order(StringComparer.Ordinal)];
            default:
                throw new ArgumentOutOfRangeException(nameof(ecosystem), ecosystem,
                    "The ecosystem does not provide supported file ownership metadata.");
        }
    }

    internal static IReadOnlyList<string> ParseDpkgList(string content) =>
        [.. content.Split('\n')
            .Select(static line => line.TrimEnd('\r').Trim('/'))
            .Where(static line => line.Length > 0 && line != ".")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

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
        return [.. files.Order(StringComparer.Ordinal)];
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
        return [.. files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
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

}

internal enum DiffOp { Same, Delete, Insert }

internal sealed record DiffLine(DiffOp Op, int? OldLine, int? NewLine, string Text);

// Myers O(ND) line diff. Checkpoints trade a second pass for bounded trace memory.
// Returns null when the edit-distance or working-memory limit is exceeded.
internal static class TextDiff
{
    private const int CheckpointInterval = 64;
    private const long MaxTraceBytes = 8 * 1024 * 1024;

    public static IReadOnlyList<DiffLine>? Diff(
        IReadOnlyList<string> a, IReadOnlyList<string> b, int maxEdits = 4000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEdits);
        cancellationToken.ThrowIfCancellationRequested();
        int n = a.Count, m = b.Count, max = (int)Math.Min((long)n + m, maxEdits);
        long frontierLength = 2L * max + 3;
        long frontierCount = 2L + max / CheckpointInterval + Math.Min(max, CheckpointInterval);
        if (frontierLength * sizeof(int) * frontierCount > MaxTraceBytes)
        {
            return null;
        }
        int offset = max + 1;
        int[] v = new int[(int)frontierLength];
        List<int[]> checkpoints = [];
        int found = -1;
        for (int d = 0; d <= max; d++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (d % CheckpointInterval == 0)
            {
                checkpoints.Add((int[])v.Clone());
            }
            if (Advance(d))
            {
                found = d;
                break;
            }
        }
        if (found < 0)
        {
            return null;
        }

        bool Advance(int d)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]
                    : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    if ((x & 255) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    return true;
                }
            }
            return false;
        }

        List<DiffLine> result = [];
        int cx = n, cy = m;
        int[][] trace = new int[Math.Min(found, CheckpointInterval)][];
        for (int i = 0; i < trace.Length; i++)
        {
            trace[i] = new int[v.Length];
        }
        for (int end = found; end > 0;)
        {
            int start = (end - 1) / CheckpointInterval * CheckpointInterval;
            checkpoints[start / CheckpointInterval].CopyTo(v, 0);
            // Replay only this block; reuse its buffers while walking earlier checkpoints.
            for (int d = start; d < end; d++)
            {
                Advance(d);
                v.CopyTo(trace[d - start], 0);
            }
            for (int d = end; d > start; d--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int[] pv = trace[d - start - 1];
                int k = cx - cy;
                int prevK = k == -d || (k != d && pv[offset + k - 1] < pv[offset + k + 1]) ? k + 1 : k - 1;
                int prevX = pv[offset + prevK];
                int prevY = prevX - prevK;
                while (cx > prevX && cy > prevY)
                {
                    if ((cx & 255) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
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
            end = start;
        }
        while (cx > 0 && cy > 0)
        {
            if ((cx & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            cx--;
            cy--;
            result.Add(new(DiffOp.Same, cx + 1, cy + 1, a[cx]));
        }
        result.Reverse();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
