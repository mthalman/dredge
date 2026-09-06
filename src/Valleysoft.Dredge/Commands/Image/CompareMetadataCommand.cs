using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text.Json;
using System.Text.Json.Nodes;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareMetadataCommand : RegistryCommandBase<CompareMetadataOptions>
{
    private readonly IAnsiConsole ansiConsole;
    private readonly IAppSettingsStore settingsStore;

    public CompareMetadataCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        IAnsiConsole? ansiConsole = null)
        : this(dockerRegistryClientFactory, ansiConsole, new AppSettingsStore())
    {
    }

    internal CompareMetadataCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        IAnsiConsole? ansiConsole,
        IAppSettingsStore settingsStore)
        : base("metadata", "Compares two images by configuration and platform metadata", dockerRegistryClientFactory)
    {
        this.ansiConsole = ansiConsole ?? AnsiConsole.Console;
        this.settingsStore = settingsStore;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return ExecuteCommandAsync(
            registry: null,
            cancellationToken,
            async ct =>
            {
                CompareMetadataResult result = await GetResultAsync(ct);
                if (Options.OutputFormat == CompareOutput.Json)
                {
                    WriteJson(result);
                }
                else
                {
                    ansiConsole.Write(GetOutput(result));
                }
            });
    }

    public async Task<CompareMetadataResult> GetResultAsync(CancellationToken cancellationToken = default)
    {
        Task<MetadataDocument> baseTask = GetMetadataAsync(Options.BaseImage, cancellationToken);
        Task<MetadataDocument> targetTask = GetMetadataAsync(Options.TargetImage, cancellationToken);
        await Task.WhenAll(baseTask, targetTask);
        MetadataDocument baseDocument = await baseTask;
        MetadataDocument targetDocument = await targetTask;
        List<MetadataComparison> comparisons = Compare(baseDocument, targetDocument);

        CompareMetadataSummary summary = new(
            areEqual: comparisons.All(comparison => comparison.Diff == CompareDiff.Equal),
            equal: comparisons.Count(comparison => comparison.Diff == CompareDiff.Equal),
            changed: comparisons.Count(comparison => comparison.Diff == CompareDiff.NotEqual),
            added: comparisons.Count(comparison => comparison.Diff == CompareDiff.Added),
            removed: comparisons.Count(comparison => comparison.Diff == CompareDiff.Removed));

        return new CompareMetadataResult(summary, comparisons);
    }

    public async Task<IRenderable> GetOutputAsync(CancellationToken cancellationToken = default) =>
        GetOutput(await GetResultAsync(cancellationToken));

    private void WriteJson(CompareMetadataResult result) =>
        ansiConsole.Profile.Out.Writer.WriteLine(JsonHelper.Serialize(result));

    private IRenderable GetOutput(CompareMetadataResult result)
    {
        bool isColorDisabled =
            Options.IsColorDisabled ||
            !ansiConsole.Profile.Capabilities.Ansi ||
            ansiConsole.Profile.Capabilities.ColorSystem == ColorSystem.NoColors;

        return Options.OutputFormat switch
        {
            CompareOutput.SideBySide => GetSideBySideOutput(result, isColorDisabled),
            CompareOutput.Inline => GetInlineOutput(result, isColorDisabled),
            CompareOutput.Json => new Text(JsonHelper.Serialize(result)),
            _ => throw new NotSupportedException($"Unsupported metadata comparison output format '{Options.OutputFormat}'.")
        };
    }

    private Table GetSideBySideOutput(CompareMetadataResult result, bool isColorDisabled)
    {
        Table table = new Table()
            .AddColumn("Metadata")
            .AddColumn(Options.BaseImage);

        if (isColorDisabled)
        {
            table.AddColumn(new TableColumn("Compare") { Alignment = Justify.Center });
        }

        table.AddColumn(Options.TargetImage);

        foreach (MetadataComparison comparison in result.Comparisons)
        {
            List<IRenderable> cells =
            [
                new Markup(Markup.Escape($"{comparison.Category}.{comparison.Path}")),
                GetValueMarkup(comparison.BaseValue, comparison.Diff, isBase: true, isColorDisabled)
            ];

            if (isColorDisabled)
            {
                cells.Add(new Markup(GetDiffDisplayName(comparison.Diff)));
            }

            cells.Add(GetValueMarkup(comparison.TargetValue, comparison.Diff, isBase: false, isColorDisabled));
            table.AddRow(cells);
        }

        return table;
    }

    private static Rows GetInlineOutput(CompareMetadataResult result, bool isColorDisabled)
    {
        List<IRenderable> rows = [];

        foreach (MetadataComparison comparison in result.Comparisons)
        {
            string path = $"{comparison.Category}.{comparison.Path}";
            if (comparison.Diff == CompareDiff.Equal)
            {
                rows.Add(GetInlineMarkup("  ", path, comparison.BaseValue, Color.Default));
                continue;
            }

            if (comparison.Diff is CompareDiff.NotEqual or CompareDiff.Removed)
            {
                rows.Add(GetInlineMarkup(
                    "- ",
                    path,
                    comparison.BaseValue,
                    isColorDisabled ? Color.Default : Color.Red));
            }

            if (comparison.Diff is CompareDiff.NotEqual or CompareDiff.Added)
            {
                rows.Add(GetInlineMarkup(
                    "+ ",
                    path,
                    comparison.TargetValue,
                    isColorDisabled ? Color.Default : Color.Green));
            }
        }

        return new Rows(rows);
    }

    private static Markup GetInlineMarkup(string prefix, string path, JsonNode? value, Color color) =>
        new(Markup.Escape($"{prefix}{path} = {FormatValue(value)}"), new Style(color));

    private static Markup GetValueMarkup(
        JsonNode? value,
        CompareDiff diff,
        bool isBase,
        bool isColorDisabled)
    {
        Color color = isColorDisabled ? Color.Default : diff switch
        {
            CompareDiff.NotEqual => isBase ? Color.Red : Color.Green,
            CompareDiff.Added => isBase ? Color.Default : Color.Green,
            CompareDiff.Removed => isBase ? Color.Red : Color.Default,
            _ => Color.Default
        };

        return new Markup(Markup.Escape(FormatValue(value)), new Style(color));
    }

    private static string FormatValue(JsonNode? value) =>
        value is null ? string.Empty : value.ToJsonString(JsonHelper.CompactSettings);

    private static string GetDiffDisplayName(CompareDiff diff) =>
        diff switch
        {
            CompareDiff.Equal => "Equal",
            CompareDiff.NotEqual => "Changed",
            CompareDiff.Added => "Added",
            CompareDiff.Removed => "Removed",
            _ => throw new NotSupportedException()
        };

    private async Task<MetadataDocument> GetMetadataAsync(string image, CancellationToken cancellationToken)
    {
        ImageName imageName = ImageName.Parse(image);
        using IDockerRegistryClient client = await DockerRegistryClientFactory.GetClientAsync(imageName.Registry);
        // Keep the original manifest because resolving an index discards the platform inventory that must also be compared.
        ManifestInfo initialManifest = await client.Manifests.GetAsync(
            imageName.Repo,
            (imageName.Tag ?? imageName.Digest)!,
            cancellationToken);
        ResolvedManifest resolvedManifest = await ManifestHelper.GetResolvedManifestAsync(
            client,
            imageName,
            Options,
            initialManifest,
            settingsStore,
            cancellationToken);

        string configDigest = resolvedManifest.Manifest.Config?.Digest ??
            throw new NotSupportedException($"Could not resolve the image config digest of '{image}'.");
        using Stream configBlob = await client.Blobs.GetAsync(imageName.Repo, configDigest, cancellationToken);
        using StreamReader configReader = new(configBlob);
        string configContent = await configReader.ReadToEndAsync(cancellationToken);
        JsonObject imageConfig;
        try
        {
            imageConfig = JsonHelper.ParseObject(configContent);
        }
        catch (JsonException)
        {
            throw new JsonException($"Could not deserialize the image config of '{image}'.");
        }

        MetadataDocument document = new();
        AddInitialManifest(document, initialManifest);
        AddResolvedManifest(document, resolvedManifest);
        AddImageConfig(document, imageConfig);
        return document;
    }

    private static void AddInitialManifest(MetadataDocument document, ManifestInfo manifestInfo)
    {
        document.Add("Manifest", "schemaVersion", manifestInfo.Manifest.SchemaVersion);
        document.Add("Manifest", "mediaType", manifestInfo.Manifest.MediaType);
        document.Add("Manifest", "contentType", manifestInfo.MediaType);
        document.Add("Manifest", "contentDigest", manifestInfo.DockerContentDigest);

        IEnumerable<KeyValuePair<string, string>>? annotations = manifestInfo.Manifest switch
        {
            OciImageIndex index => index.Annotations,
            OciImageManifest manifest => manifest.Annotations,
            _ => null
        };
        document.AddDictionary("Manifest", "annotations", annotations);

        if (manifestInfo.Manifest is IManifestList manifestList)
        {
            AddPlatforms(document, manifestList);
        }
    }

    private static void AddPlatforms(MetadataDocument document, IManifestList manifestList)
    {
        // Platform identity makes index ordering irrelevant; the suffix keeps duplicate/unknown platform descriptors distinct.
        foreach (IGrouping<string, IManifestReference> platformGroup in manifestList.Manifests
            .GroupBy(GetPlatformId)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            IManifestReference[] references = [.. platformGroup.OrderBy(reference => reference.Digest, StringComparer.Ordinal)];
            for (int i = 0; i < references.Length; i++)
            {
                string id = references.Length == 1 ? platformGroup.Key : $"{platformGroup.Key}#{i + 1}";
                string path = $"available[{JsonSerializer.Serialize(id, JsonHelper.Settings)}]";
                AddDescriptor(document, "Platforms", path, references[i]);
                AddPlatform(document, "Platforms", $"{path}.platform", references[i].Platform);
            }
        }
    }

    private static void AddResolvedManifest(MetadataDocument document, ResolvedManifest resolved)
    {
        document.Add("ResolvedManifest", "schemaVersion", resolved.Manifest.SchemaVersion);
        document.Add("ResolvedManifest", "mediaType", resolved.Manifest.MediaType);
        document.Add("ResolvedManifest", "contentType", resolved.ManifestInfo.MediaType);
        document.Add("ResolvedManifest", "contentDigest", resolved.ManifestInfo.DockerContentDigest);

        if (resolved.Manifest is OciImageManifest ociManifest)
        {
            document.Add("ResolvedManifest", "artifactType", ociManifest.ArtifactType);
            document.AddDictionary("ResolvedManifest", "annotations", ociManifest.Annotations);
            if (ociManifest.Subject is not null)
            {
                AddDescriptor(document, "ResolvedManifest", "subject", ociManifest.Subject);
            }
        }

        if (resolved.Manifest.Config is not null)
        {
            AddDescriptor(document, "ResolvedManifest", "config", resolved.Manifest.Config);
        }

        for (int i = 0; i < resolved.Manifest.Layers.Length; i++)
        {
            AddDescriptor(document, "ResolvedManifest", $"layers[{i}]", resolved.Manifest.Layers[i]);
        }
    }

    private static void AddDescriptor(
        MetadataDocument document,
        string category,
        string path,
        IDescriptor descriptor)
    {
        document.Add(category, $"{path}.mediaType", descriptor.MediaType);
        document.Add(category, $"{path}.digest", descriptor.Digest);
        document.Add(category, $"{path}.size", descriptor.Size);

        if (descriptor is OciDescriptor ociDescriptor)
        {
            document.Add(category, $"{path}.artifactType", ociDescriptor.ArtifactType);
            document.Add(category, $"{path}.data", ociDescriptor.Data);
            document.AddSet(category, $"{path}.urls", ociDescriptor.Urls);
            document.AddDictionary(category, $"{path}.annotations", ociDescriptor.Annotations);
        }
    }

    private static void AddPlatform(
        MetadataDocument document,
        string category,
        string path,
        ManifestPlatform? platform)
    {
        if (platform is null)
        {
            return;
        }

        document.Add(category, $"{path}.os", platform.Os);
        document.Add(category, $"{path}.architecture", platform.Architecture);
        document.Add(category, $"{path}.osVersion", platform.OsVersion);
        document.Add(category, $"{path}.variant", platform.Variant);
        document.AddSet(category, $"{path}.osFeatures", platform.OsFeatures);
        document.AddSet(category, $"{path}.features", platform.Features);
    }

    private static string GetPlatformId(IManifestReference reference)
    {
        ManifestPlatform? platform = reference.Platform;
        if (platform is null)
        {
            return "<unknown>";
        }

        return string.Join(
            "/",
            new[] { platform.Os, platform.Architecture, platform.Variant, platform.OsVersion }
                .Where(value => !string.IsNullOrEmpty(value)));
    }

    private static void AddImageConfig(MetadataDocument document, JsonObject image)
    {
        foreach ((string name, JsonNode? value) in image.OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            switch (name)
            {
                case "config":
                    AddExecutionConfig(document, value);
                    break;
                case "rootfs":
                    AddRootFilesystem(document, value);
                    break;
                case "history":
                    AddHistory(document, value);
                    break;
                case "os.features":
                    document.AddTokenSet("Image", "osFeatures", value);
                    break;
                case "os.version":
                    document.AddToken("Image", "osVersion", value);
                    break;
                default:
                    document.AddToken("Image", LowerFirstCharacter(name), value);
                    break;
            }
        }
    }

    private static void AddRootFilesystem(MetadataDocument document, JsonNode? rootFilesystem)
    {
        if (rootFilesystem is not JsonObject rootFilesystemObject)
        {
            return;
        }

        foreach ((string name, JsonNode? value) in rootFilesystemObject
            .OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            string path = name == "diff_ids" ? "diffIds" : LowerFirstCharacter(name);
            document.AddToken("RootFilesystem", path, value);
        }
    }

    private static void AddHistory(MetadataDocument document, JsonNode? history)
    {
        if (history is not JsonArray historyArray)
        {
            return;
        }

        for (int i = 0; i < historyArray.Count; i++)
        {
            if (historyArray[i] is not JsonObject historyEntry)
            {
                document.AddToken("History", $"entries[{i}]", historyArray[i]);
                continue;
            }

            foreach ((string name, JsonNode? value) in historyEntry
                .OrderBy(property => property.Key, StringComparer.Ordinal))
            {
                string propertyName = name switch
                {
                    "created_by" => "createdBy",
                    "empty_layer" => "emptyLayer",
                    _ => LowerFirstCharacter(name)
                };
                document.AddToken("History", $"entries[{i}].{propertyName}", value);
            }
        }
    }

    private static void AddExecutionConfig(MetadataDocument document, JsonNode? config)
    {
        if (config is not JsonObject configObject)
        {
            return;
        }

        foreach ((string name, JsonNode? value) in configObject
            .OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            switch (name)
            {
                case "Env":
                    AddEnvironment(document, value);
                    break;
                case "ExposedPorts":
                    document.AddObjectKeys("Config", "exposedPorts", value);
                    break;
                case "Volumes":
                    document.AddObjectKeys("Config", "volumes", value);
                    break;
                default:
                    string path = name switch
                    {
                        "Cmd" => "command",
                        "WorkingDir" => "workingDirectory",
                        _ => LowerFirstCharacter(name)
                    };
                    document.AddToken("Config", path, value);
                    break;
            }
        }
    }

    private static void AddEnvironment(MetadataDocument document, JsonNode? environment)
    {
        if (environment is not JsonArray environmentArray)
        {
            return;
        }

        // Environment order is not significant, but duplicate names remain ordered because the last assignment can affect runtime behavior.
        foreach (IGrouping<string, string> group in environmentArray
            .Select(GetStringValue)
            .Where(value => value is not null)
            .Cast<string>()
            .Select(ParseEnvironmentVariable)
            .GroupBy(variable => variable.Name, variable => variable.Value)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            string path = $"environment[{JsonSerializer.Serialize(group.Key, JsonHelper.Settings)}]";
            string[] values = [.. group];
            document.Add(
                "Config",
                path,
                values.Length == 1
                    ? JsonValue.Create(values[0])
                    : new JsonArray(values.Select(value => JsonValue.Create(value)).ToArray()));
        }
    }

    private static (string Name, string Value) ParseEnvironmentVariable(string variable)
    {
        int separatorIndex = variable.IndexOf('=');
        return separatorIndex < 0
            ? (variable, string.Empty)
            : (variable[..separatorIndex], variable[(separatorIndex + 1)..]);
    }

    private static string LowerFirstCharacter(string value) =>
        string.IsNullOrEmpty(value) || char.IsLower(value[0])
            ? value
            : $"{char.ToLowerInvariant(value[0])}{value[1..]}";

    private static List<MetadataComparison> Compare(MetadataDocument @base, MetadataDocument target)
    {
        List<MetadataComparison> comparisons = [];
        IEnumerable<string> keys = @base.Items.Keys
            .Union(target.Items.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal);

        foreach (string key in keys)
        {
            @base.Items.TryGetValue(key, out MetadataItem? baseItem);
            target.Items.TryGetValue(key, out MetadataItem? targetItem);
            MetadataItem item = baseItem ?? targetItem!;

            comparisons.Add(new MetadataComparison(
                item.Category,
                item.Path,
                baseItem?.Value,
                targetItem?.Value,
                GetDiff(baseItem, targetItem)));
        }

        return comparisons;
    }

    private static CompareDiff GetDiff(MetadataItem? baseItem, MetadataItem? targetItem)
    {
        if (baseItem is null)
        {
            return CompareDiff.Added;
        }

        if (targetItem is null)
        {
            return CompareDiff.Removed;
        }

        return MetadataValuesEqual(baseItem.Value, targetItem.Value)
            ? CompareDiff.Equal
            : CompareDiff.NotEqual;
    }

    private static bool MetadataValuesEqual(JsonNode left, JsonNode right)
    {
        if (left.GetValueKind() == JsonValueKind.Number &&
            right.GetValueKind() == JsonValueKind.Number)
        {
            string leftValue = left.ToJsonString(JsonHelper.CompactSettings);
            string rightValue = right.ToJsonString(JsonHelper.CompactSettings);
            bool leftIsFloat = leftValue.Contains('.') || leftValue.IndexOfAny(['e', 'E']) >= 0;
            bool rightIsFloat = rightValue.Contains('.') || rightValue.IndexOfAny(['e', 'E']) >= 0;
            return leftIsFloat == rightIsFloat &&
                (!leftIsFloat ||
                    ApproximatelyEqual(
                        double.Parse(
                            leftValue,
                            System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(
                            rightValue,
                            System.Globalization.CultureInfo.InvariantCulture))) &&
                (leftIsFloat ||
                    string.Equals(leftValue, rightValue, StringComparison.Ordinal));
        }

        return JsonNode.DeepEquals(left, right);
    }

    private static bool ApproximatelyEqual(double left, double right)
    {
        if (left.Equals(right))
        {
            return true;
        }

        double tolerance =
            (Math.Abs(left) + Math.Abs(right) + 10.0) * 2.2204460492503131e-16;
        double difference = left - right;
        return -tolerance < difference && tolerance > difference;
    }

    private static string? GetStringValue(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is not JsonValue jsonValue)
        {
            throw new InvalidCastException($"Cannot cast {value.GetType().Name} to string.");
        }

        if (jsonValue.TryGetValue(out string? stringValue))
        {
            return stringValue;
        }

        if (jsonValue.TryGetValue(out bool boolValue))
        {
            return boolValue.ToString();
        }

        if (jsonValue.GetValueKind() == JsonValueKind.Number)
        {
            string rawValue = jsonValue.ToJsonString(JsonHelper.CompactSettings);
            if (!rawValue.Contains('.') && rawValue.IndexOfAny(['e', 'E']) < 0)
            {
                return long.TryParse(
                    rawValue,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long integer)
                        ? integer.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : throw new InvalidCastException("Object must implement IConvertible.");
            }

            return double.TryParse(
                rawValue,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double number)
                    ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : rawValue;
        }

        throw new InvalidCastException($"Cannot cast {jsonValue.GetValueKind()} to string.");
    }

    private sealed class MetadataDocument
    {
        public SortedDictionary<string, MetadataItem> Items { get; } =
            new(StringComparer.Ordinal);

        public void Add(string category, string path, object? value)
        {
            if (value is null)
            {
                return;
            }

            JsonNode token = value as JsonNode ??
                JsonSerializer.SerializeToNode(value, value.GetType(), JsonHelper.Settings)!;
            if (value is JsonNode && token.GetValueKind() == JsonValueKind.Number)
            {
                token = JsonNode.Parse(JsonHelper.NormalizeNewtonsoftNumber(
                    token.ToJsonString(JsonHelper.CompactSettings)))!;
            }
            // A null separator cannot collide with the JSON-escaped user keys embedded in paths.
            Items[$"{category}\0{path}"] = new MetadataItem(category, path, token);
        }

        public void AddToken(string category, string path, JsonNode? value)
        {
            if (value is null)
            {
                return;
            }

            if (value is JsonObject valueObject)
            {
                foreach ((string name, JsonNode? propertyValue) in valueObject
                    .OrderBy(property => property.Key, StringComparer.Ordinal))
                {
                    AddToken(
                        category,
                        $"{path}[{JsonSerializer.Serialize(name, JsonHelper.Settings)}]",
                        propertyValue);
                }
            }
            else if (value is JsonArray valueArray)
            {
                for (int i = 0; i < valueArray.Count; i++)
                {
                    AddToken(category, $"{path}[{i}]", valueArray[i]);
                }
            }
            else
            {
                Add(category, path, value.DeepClone());
            }
        }

        public void AddTokenSet(string category, string path, JsonNode? value)
        {
            if (value is not JsonArray valueArray)
            {
                return;
            }

            AddSet(
                category,
                path,
                valueArray.Select(GetStringValue).Where(item => item is not null).Cast<string>());
        }

        public void AddObjectKeys(string category, string path, JsonNode? value)
        {
            if (value is not JsonObject valueObject)
            {
                return;
            }

            AddKeys(category, path, valueObject.Select(property => property.Key));
        }

        public void AddDictionary(
            string category,
            string path,
            IEnumerable<KeyValuePair<string, string>>? values)
        {
            if (values is null)
            {
                return;
            }

            foreach ((string key, string value) in values.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                Add(category, $"{path}[{JsonSerializer.Serialize(key, JsonHelper.Settings)}]", value);
            }
        }

        public void AddSet(string category, string path, IEnumerable<string>? values)
        {
            if (values is null)
            {
                return;
            }

            AddKeys(category, path, values.Distinct(StringComparer.Ordinal));
        }

        private void AddKeys(string category, string path, IEnumerable<string> values)
        {
            foreach (string value in values.OrderBy(value => value, StringComparer.Ordinal))
            {
                Add(category, $"{path}[{JsonSerializer.Serialize(value, JsonHelper.Settings)}]", true);
            }
        }
    }

    private sealed record MetadataItem(string Category, string Path, JsonNode Value);
}
