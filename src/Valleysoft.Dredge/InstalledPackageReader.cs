using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Valleysoft.Dredge;

internal enum InstalledPackageEcosystem
{
    Npm,
    Dpkg,
    Apk,
    Pip,
    NuGet
}

internal enum InstalledPackageMetadataAvailability
{
    Unavailable,
    Available
}

internal sealed record InstalledPackage(string Name, string Version);

internal sealed record InstalledPackageDiagnostic(string Path, string Message);

internal sealed record InstalledPackageEcosystemMetadata(
    InstalledPackageMetadataAvailability Availability,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Packages);

internal sealed record InstalledPackageMetadata(
    IReadOnlyDictionary<InstalledPackageEcosystem, InstalledPackageEcosystemMetadata> Ecosystems)
{
    public IReadOnlyList<InstalledPackageDiagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NpmPackageRoots { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

internal static class InstalledPackageReader
{
    internal const long MaxPackageManifestBytes = 1024 * 1024;
    internal const long MaxDatabaseManifestBytes = 64 * 1024 * 1024;

    private const string DpkgStatusPath = "var/lib/dpkg/status";
    private const string ApkInstalledPath = "lib/apk/db/installed";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<InstalledPackageMetadata> ReadAsync(
        ImageFileSystem fileSystem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ImageFileSystemEntry> entries = fileSystem.List(null, true, false);
        ImageFileSystemEntry[] npmManifests = [.. entries.Where(entry => IsReadableFile(entry) && IsNpmPackageManifestPath(entry.Path))];
        ImageFileSystemEntry[] pipManifests = [.. entries.Where(entry => IsReadableFile(entry) && IsPipMetadataPath(entry.Path))];
        HashSet<string> nugetCacheDirectories = entries
            .Where(entry => IsReadableFile(entry) &&
                (entry.Path == ".nupkg.metadata" || entry.Path.EndsWith("/.nupkg.metadata", StringComparison.Ordinal)))
            .Select(entry => ImagePath.GetDirectoryName(entry.Path))
            .ToHashSet(StringComparer.Ordinal);
        ImageFileSystemEntry[] nugetManifests = [.. entries
            .Where(entry => IsReadableFile(entry) && IsNuGetDepsPath(entry.Path) &&
                !IsNuGetCachePath(entry.Path, nugetCacheDirectories))];
        ImageFileSystemEntry? dpkgStatus = entries.SingleOrDefault(
            entry => IsReadableFile(entry) && entry.Path == DpkgStatusPath);
        ImageFileSystemEntry? apkInstalled = entries.SingleOrDefault(
            entry => IsReadableFile(entry) && entry.Path == ApkInstalledPath);

        // Package metadata is advisory: a malformed per-package manifest is skipped,
        // and an unreadable database marks only its ecosystem unavailable.
        List<InstalledPackage> npmPackages = [];
        List<(string Name, string Root)> npmRoots = [];
        List<InstalledPackage> pipPackages = [];
        List<InstalledPackage> nugetPackages = [];
        bool nugetAvailable = false;
        List<InstalledPackageDiagnostic> diagnostics = [];
        IReadOnlyList<InstalledPackage>? dpkgPackages = null;
        IReadOnlyList<InstalledPackage>? apkPackages = null;
        IEnumerable<(string Path, long MaximumBytes)> requests = npmManifests.Concat(pipManifests)
            .Select(entry => (entry.Path, MaxPackageManifestBytes))
            .Concat(new[] { dpkgStatus, apkInstalled }.OfType<ImageFileSystemEntry>()
                .Select(entry => (entry.Path, MaxDatabaseManifestBytes)))
            .Concat(nugetManifests.Select(entry => (entry.Path, MaxDatabaseManifestBytes)));
        await foreach (var result in fileSystem.ReadFilesAsync(requests, cancellationToken))
        {
            if (result.Error is not null)
            {
                diagnostics.Add(new(result.Path, result.Error.Message));
                continue;
            }
            string content;
            try
            {
                content = StrictUtf8.GetString(result.Content!).TrimStart('\uFEFF');
            }
            catch (DecoderFallbackException exception)
            {
                diagnostics.Add(new(result.Path, exception.Message));
                continue;
            }
            switch (result.Path)
            {
                case DpkgStatusPath:
                    dpkgPackages = TryParse(() => ParseDpkgStatus(content), result.Path, diagnostics);
                    break;
                case ApkInstalledPath:
                    apkPackages = TryParse(() => ParseApkInstalled(content), result.Path, diagnostics);
                    break;
                default:
                    if (IsNuGetDepsPath(result.Path))
                    {
                        IReadOnlyList<InstalledPackage>? dependencies =
                            TryParse(() => ParseNuGetDepsJson(content, result.Path), result.Path, diagnostics);
                        if (dependencies is not null)
                        {
                            nugetAvailable = true;
                            nugetPackages.AddRange(dependencies);
                        }
                        break;
                    }
                    bool npm = IsNpmPackageManifestPath(result.Path);
                    InstalledPackage? package = TryParse(() => npm
                        ? ParseNpmPackageJson(content, result.Path) : ParsePipMetadata(content, result.Path),
                        result.Path, diagnostics);
                    if (package is not null)
                    {
                        (npm ? npmPackages : pipPackages).Add(package);
                        if (npm)
                        {
                            npmRoots.Add((package.Name, ImagePath.GetDirectoryName(result.Path)));
                        }
                    }
                    break;
            }
        }

        Dictionary<InstalledPackageEcosystem, InstalledPackageEcosystemMetadata> ecosystems = new()
        {
            [InstalledPackageEcosystem.Npm] = CreateMetadata(npmPackages.Count > 0, npmPackages),
            [InstalledPackageEcosystem.Dpkg] = CreateMetadata(dpkgPackages is not null, dpkgPackages ?? []),
            [InstalledPackageEcosystem.Apk] = CreateMetadata(apkPackages is not null, apkPackages ?? []),
            [InstalledPackageEcosystem.Pip] = CreateMetadata(pipPackages.Count > 0, pipPackages),
            [InstalledPackageEcosystem.NuGet] = CreateMetadata(nugetAvailable, nugetPackages)
        };

        return new InstalledPackageMetadata(ecosystems)
        {
            Diagnostics = diagnostics,
            NpmPackageRoots = npmRoots.GroupBy(item => item.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => (IReadOnlyList<string>)[.. group.Select(item => item.Root)
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                    StringComparer.Ordinal)
        };
    }

    private static T? TryParse<T>(Func<T> parse, string path, List<InstalledPackageDiagnostic> diagnostics) where T : class
    {
        try
        {
            return parse();
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new(path, exception.Message));
            return null;
        }
    }

    internal static bool IsNpmPackageManifestPath(string path)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int nodeModulesIndex = Array.LastIndexOf(segments, "node_modules");
        if (nodeModulesIndex < 0)
        {
            return false;
        }

        int remaining = segments.Length - nodeModulesIndex - 1;
        return remaining == 2 && segments[^1] == "package.json" ||
            remaining == 3 && segments[nodeModulesIndex + 1].StartsWith('@') &&
            segments[^1] == "package.json";
    }

    internal static bool IsPipMetadataPath(string path)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 &&
            segments[^1] == "METADATA" &&
            segments[^2].EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsNuGetDepsPath(string path) =>
        path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsNuGetCachePath(string path, IReadOnlySet<string> cacheDirectories)
    {
        string[] segments = path.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("NuGetFallbackFolder", StringComparison.OrdinalIgnoreCase) ||
                (segments[i].Equals(".nuget", StringComparison.OrdinalIgnoreCase) &&
                    segments[i + 1].Equals("packages", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        string directory = ImagePath.GetDirectoryName(path);
        while (true)
        {
            if (cacheDirectories.Contains(directory))
            {
                return true;
            }
            if (directory.Length == 0)
            {
                return false;
            }
            directory = ImagePath.GetDirectoryName(directory);
        }
    }

    internal static IReadOnlyList<InstalledPackage> ParseNuGetDepsJson(string content, string sourcePath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content.TrimStart('\uFEFF'));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("runtimeTarget", out JsonElement runtimeTarget) ||
                runtimeTarget.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("targets", out JsonElement targets) ||
                targets.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("libraries", out JsonElement libraries) ||
                libraries.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"NuGet metadata '{sourcePath}' must contain runtimeTarget, targets, and libraries objects.");
            }
            string targetName = GetRequiredJsonString(runtimeTarget, "name", sourcePath, "NuGet");
            if (!targets.TryGetProperty(targetName, out JsonElement target) ||
                target.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"NuGet metadata '{sourcePath}' is missing runtime target '{targetName}'.");
            }
            List<InstalledPackage> packages = [];
            foreach (JsonProperty dependency in target.EnumerateObject())
            {
                if (dependency.Value.ValueKind != JsonValueKind.Object ||
                    !libraries.TryGetProperty(dependency.Name, out JsonElement library) ||
                    library.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"NuGet metadata '{sourcePath}' has an invalid library '{dependency.Name}'.");
                }
                string type = GetRequiredJsonString(library, "type", sourcePath, "NuGet");
                if (type != "package")
                {
                    continue;
                }
                int separator = dependency.Name.IndexOf('/');
                if (separator <= 0 || separator == dependency.Name.Length - 1 ||
                    dependency.Name.IndexOf('/', separator + 1) >= 0)
                {
                    throw new InvalidDataException($"NuGet metadata '{sourcePath}' has an invalid package identity '{dependency.Name}'.");
                }
                string name = dependency.Name[..separator];
                string version = dependency.Name[(separator + 1)..];
                if (name.Any(char.IsWhiteSpace) || version.Any(char.IsWhiteSpace))
                {
                    throw new InvalidDataException($"NuGet metadata '{sourcePath}' has an invalid package identity '{dependency.Name}'.");
                }
                packages.Add(new(name.ToLowerInvariant(), version));
            }
            return packages;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"NuGet metadata '{sourcePath}' is not valid JSON.", exception);
        }
    }

    internal static InstalledPackage ParseNpmPackageJson(string content, string sourcePath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content.TrimStart('\uFEFF'));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"npm metadata '{sourcePath}' must contain a JSON object.");
            }

            string name = GetRequiredJsonString(document.RootElement, "name", sourcePath, "npm");
            string version = GetRequiredJsonString(document.RootElement, "version", sourcePath, "npm");
            return new InstalledPackage(name, version);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"npm metadata '{sourcePath}' is not valid JSON.", exception);
        }
    }

    internal static IReadOnlyList<InstalledPackage> ParseDpkgStatus(string content)
    {
        List<InstalledPackage> packages = [];
        foreach (Dictionary<string, string> paragraph in ParseParagraphs(content, "dpkg status"))
        {
            if (!paragraph.TryGetValue("Status", out string? status))
            {
                continue;
            }
            string[] states = status.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (states.Length != 3 || states[2] != "installed")
            {
                continue;
            }

            packages.Add(new InstalledPackage(
                GetRequiredField(paragraph, "Package", "installed dpkg paragraph"),
                GetRequiredField(paragraph, "Version", "installed dpkg paragraph")));
        }

        return packages;
    }

    // apk keys are single case-sensitive letters (T is the description, t the build
    // time), and file keys such as F, R, and Z repeat within a package.
    internal static IReadOnlyList<InstalledPackage> ParseApkInstalled(string content)
    {
        List<InstalledPackage> packages = [];
        string? name = null;
        string? version = null;
        bool any = false;
        int lineNumber = 0;

        void Flush()
        {
            if (any)
            {
                packages.Add(new InstalledPackage(
                    string.IsNullOrWhiteSpace(name)
                        ? throw new InvalidDataException("apk installed paragraph is missing a nonempty 'P' field.")
                        : name,
                    string.IsNullOrWhiteSpace(version)
                        ? throw new InvalidDataException("apk installed paragraph is missing a nonempty 'V' field.")
                        : version));
            }
            name = version = null;
            any = false;
        }

        foreach (string rawLine in content.Split('\n'))
        {
            lineNumber++;
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            if (line.Length < 2 || line[1] != ':' || char.IsWhiteSpace(line[0]))
            {
                throw new InvalidDataException($"apk installed database has an invalid field at line {lineNumber}.");
            }
            any = true;
            switch (line[0])
            {
                case 'P':
                    name ??= line[2..].Trim();
                    break;
                case 'V':
                    version ??= line[2..].Trim();
                    break;
            }
        }
        Flush();
        return packages;
    }

    internal static InstalledPackage ParsePipMetadata(string content, string sourcePath)
    {
        Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                break;
            }
            if (char.IsWhiteSpace(line[0]))
            {
                if (previous is null)
                {
                    throw new InvalidDataException($"pip metadata '{sourcePath}' has a continuation without a header.");
                }
                if (metadata.ContainsKey(previous))
                {
                    metadata[previous] += "\n" + line[1..];
                }
                continue;
            }
            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException($"pip metadata '{sourcePath}' has an invalid header.");
            }
            previous = line[..separator];
            if ((previous.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                previous.Equals("Version", StringComparison.OrdinalIgnoreCase)) &&
                !metadata.TryAdd(previous, line[(separator + 1)..].Trim()))
            {
                throw new InvalidDataException($"pip metadata '{sourcePath}' has a duplicate '{previous}' header.");
            }
        }
        return new InstalledPackage(
            NormalizePipName(GetRequiredField(metadata, "Name", $"pip metadata '{sourcePath}'")),
            GetRequiredField(metadata, "Version", $"pip metadata '{sourcePath}'"));
    }

    internal static string NormalizePipName(string name) =>
        Regex.Replace(name, "[-_.]+", "-").ToLowerInvariant();

    internal static void ValidateManifestSize(string path, long size, long maximumBytes)
    {
        if (size < 0 || size > maximumBytes)
        {
            throw new InvalidDataException(
                $"Installed-package metadata '{path}' has size {size} bytes; " +
                $"the supported maximum is {maximumBytes} bytes.");
        }
    }

    private static bool IsReadableFile(ImageFileSystemEntry entry) =>
        entry.Type is ImageFileType.File or ImageFileType.HardLink or ImageFileType.SymbolicLink;

    internal static InstalledPackageEcosystemMetadata CreateMetadata(
        bool available,
        IEnumerable<InstalledPackage> packages)
    {
        Dictionary<string, IReadOnlyList<string>> byName = packages
            .GroupBy(static package => package.Name, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<string>)[.. group
                    .Select(static package => package.Version)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static version => version, StringComparer.Ordinal)],
                StringComparer.Ordinal);

        return new InstalledPackageEcosystemMetadata(
            available
                ? InstalledPackageMetadataAvailability.Available
                : InstalledPackageMetadataAvailability.Unavailable,
            byName);
    }

    private static string GetRequiredJsonString(
        JsonElement element,
        string propertyName,
        string sourcePath,
        string ecosystem)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException(
                $"{ecosystem} metadata '{sourcePath}' is missing a nonempty '{propertyName}' string.");
        }

        return property.GetString()!;
    }

    private static string GetRequiredField(
        IReadOnlyDictionary<string, string> fields,
        string fieldName,
        string source)
    {
        if (!fields.TryGetValue(fieldName, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{source} is missing a nonempty '{fieldName}' field.");
        }

        return value;
    }

    private static IEnumerable<Dictionary<string, string>> ParseParagraphs(
        string content,
        string source)
    {
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        string? previousField = null;
        int lineNumber = 0;
        foreach (string rawLine in content.Split('\n'))
        {
            lineNumber++;
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (fields.Count > 0)
                {
                    yield return fields;
                    fields = new(StringComparer.OrdinalIgnoreCase);
                    previousField = null;
                }
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                if (previousField is null)
                {
                    throw new InvalidDataException(
                        $"{source} has a continuation without a field at line {lineNumber}.");
                }

                fields[previousField] = $"{fields[previousField]}\n{line[1..]}";
                continue;
            }

            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException($"{source} has an invalid field at line {lineNumber}.");
            }

            string name = line[..separator];
            string value = line[(separator + 1)..].Trim();
            if (!fields.TryAdd(name, value))
            {
                throw new InvalidDataException(
                    $"{source} has a duplicate '{name}' field at line {lineNumber}.");
            }
            previousField = name;
        }

        if (fields.Count > 0)
        {
            yield return fields;
        }
    }

}
