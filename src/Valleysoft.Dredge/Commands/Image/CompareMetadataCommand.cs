using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Spectre.Console;
using Spectre.Console.Rendering;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge.Commands.Image;

public class CompareMetadataCommand : RegistryCommandBase<CompareMetadataOptions>
{
    private static readonly JsonSerializerSettings imageConfigJsonSettings = new()
    {
        DateParseHandling = DateParseHandling.None
    };

    private readonly IAnsiConsole ansiConsole;
    private readonly Func<PlatformSettings> platformSettingsProvider;

    public CompareMetadataCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        IAnsiConsole? ansiConsole = null)
        : this(dockerRegistryClientFactory, ansiConsole, () => AppSettings.Load().Platform)
    {
    }

    internal CompareMetadataCommand(
        IDockerRegistryClientFactory dockerRegistryClientFactory,
        IAnsiConsole? ansiConsole,
        Func<PlatformSettings> platformSettingsProvider)
        : base("metadata", "Compares two images by configuration and platform metadata", dockerRegistryClientFactory)
    {
        this.ansiConsole = ansiConsole ?? AnsiConsole.Console;
        this.platformSettingsProvider = platformSettingsProvider;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return CommandHelper.ExecuteCommandAsync(
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
        ansiConsole.Profile.Out.Writer.WriteLine(JsonConvert.SerializeObject(result, JsonHelper.Settings));

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
            CompareOutput.Json => new Text(JsonConvert.SerializeObject(result, JsonHelper.Settings)),
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

    private static Markup GetInlineMarkup(string prefix, string path, JToken? value, Color color) =>
        new(Markup.Escape($"{prefix}{path} = {FormatValue(value)}"), new Style(color));

    private static Markup GetValueMarkup(
        JToken? value,
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

    private static string FormatValue(JToken? value) =>
        value is null ? string.Empty : value.ToString(Formatting.None);

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
            platformSettingsProvider,
            cancellationToken);

        string configDigest = resolvedManifest.Manifest.Config?.Digest ??
            throw new NotSupportedException($"Could not resolve the image config digest of '{image}'.");
        using Stream configBlob = await client.Blobs.GetAsync(imageName.Repo, configDigest, cancellationToken);
        using StreamReader configReader = new(configBlob);
        string configContent = await configReader.ReadToEndAsync(cancellationToken);
        JObject imageConfig = JsonConvert.DeserializeObject<JObject>(configContent, imageConfigJsonSettings) ??
            throw new JsonException($"Could not deserialize the image config of '{image}'.");

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
                string path = $"available[{JsonConvert.ToString(id)}]";
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

    private static void AddImageConfig(MetadataDocument document, JObject image)
    {
        foreach (JProperty property in image.Properties().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            switch (property.Name)
            {
                case "config":
                    AddExecutionConfig(document, property.Value);
                    break;
                case "rootfs":
                    AddRootFilesystem(document, property.Value);
                    break;
                case "history":
                    AddHistory(document, property.Value);
                    break;
                case "os.features":
                    document.AddTokenSet("Image", "osFeatures", property.Value);
                    break;
                case "os.version":
                    document.AddToken("Image", "osVersion", property.Value);
                    break;
                default:
                    document.AddToken("Image", LowerFirstCharacter(property.Name), property.Value);
                    break;
            }
        }
    }

    private static void AddRootFilesystem(MetadataDocument document, JToken rootFilesystem)
    {
        if (rootFilesystem is not JObject rootFilesystemObject)
        {
            return;
        }

        foreach (JProperty property in rootFilesystemObject.Properties()
            .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            string path = property.Name == "diff_ids" ? "diffIds" : LowerFirstCharacter(property.Name);
            document.AddToken("RootFilesystem", path, property.Value);
        }
    }

    private static void AddHistory(MetadataDocument document, JToken history)
    {
        if (history is not JArray historyArray)
        {
            return;
        }

        for (int i = 0; i < historyArray.Count; i++)
        {
            if (historyArray[i] is not JObject historyEntry)
            {
                document.AddToken("History", $"entries[{i}]", historyArray[i]);
                continue;
            }

            foreach (JProperty property in historyEntry.Properties()
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                string propertyName = property.Name switch
                {
                    "created_by" => "createdBy",
                    "empty_layer" => "emptyLayer",
                    _ => LowerFirstCharacter(property.Name)
                };
                document.AddToken("History", $"entries[{i}].{propertyName}", property.Value);
            }
        }
    }

    private static void AddExecutionConfig(MetadataDocument document, JToken config)
    {
        if (config is not JObject configObject)
        {
            return;
        }

        foreach (JProperty property in configObject.Properties()
            .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            switch (property.Name)
            {
                case "Env":
                    AddEnvironment(document, property.Value);
                    break;
                case "ExposedPorts":
                    document.AddObjectKeys("Config", "exposedPorts", property.Value);
                    break;
                case "Volumes":
                    document.AddObjectKeys("Config", "volumes", property.Value);
                    break;
                default:
                    string path = property.Name switch
                    {
                        "Cmd" => "command",
                        "WorkingDir" => "workingDirectory",
                        _ => LowerFirstCharacter(property.Name)
                    };
                    document.AddToken("Config", path, property.Value);
                    break;
            }
        }
    }

    private static void AddEnvironment(MetadataDocument document, JToken environment)
    {
        if (environment is not JArray environmentArray)
        {
            return;
        }

        // Environment order is not significant, but duplicate names remain ordered because the last assignment can affect runtime behavior.
        foreach (IGrouping<string, string> group in environmentArray
            .Values<string>()
            .Where(variable => variable is not null)
            .Select(variable => ParseEnvironmentVariable(variable!))
            .GroupBy(variable => variable.Name, variable => variable.Value)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            string path = $"environment[{JsonConvert.ToString(group.Key)}]";
            string[] values = [.. group];
            document.Add(
                "Config",
                path,
                values.Length == 1 ? JValue.CreateString(values[0]) : JArray.FromObject(values));
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

        return JToken.DeepEquals(baseItem.Value, targetItem.Value)
            ? CompareDiff.Equal
            : CompareDiff.NotEqual;
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

            JToken token = value as JToken ?? JToken.FromObject(value);
            // A null separator cannot collide with the JSON-escaped user keys embedded in paths.
            Items[$"{category}\0{path}"] = new MetadataItem(category, path, token);
        }

        public void AddToken(string category, string path, JToken? value)
        {
            if (value is null || value.Type is JTokenType.Null or JTokenType.Undefined)
            {
                return;
            }

            if (value is JObject valueObject)
            {
                foreach (JProperty property in valueObject.Properties()
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    AddToken(category, $"{path}[{JsonConvert.ToString(property.Name)}]", property.Value);
                }
            }
            else if (value is JArray valueArray)
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

        public void AddTokenSet(string category, string path, JToken value)
        {
            if (value is not JArray valueArray)
            {
                return;
            }

            AddSet(category, path, valueArray.Values<string>().Where(item => item is not null).Cast<string>());
        }

        public void AddObjectKeys(string category, string path, JToken value)
        {
            if (value is not JObject valueObject)
            {
                return;
            }

            AddKeys(category, path, valueObject.Properties().Select(property => property.Name));
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
                Add(category, $"{path}[{JsonConvert.ToString(key)}]", value);
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
                Add(category, $"{path}[{JsonConvert.ToString(value)}]", true);
            }
        }
    }

    private sealed record MetadataItem(string Category, string Path, JToken Value);
}
