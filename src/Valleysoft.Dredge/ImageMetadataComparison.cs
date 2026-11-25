using System.Text.Json;
using System.Text.Json.Nodes;
using Valleysoft.DockerRegistryClient.Models.Manifests;
using Valleysoft.DockerRegistryClient.Models.Manifests.Oci;

namespace Valleysoft.Dredge;

internal static class ImageMetadataComparison
{
    internal static IReadOnlyList<CompareValueDifference> Compare(
        ResolvedImage baseline, ResolvedImage target, bool layers)
    {
        bool distribution = !baseline.IsDaemon && !target.IsDaemon;
        MetadataDocument before = GetMetadata(baseline, distribution, true);
        MetadataDocument after = GetMetadata(target, distribution, true);
        return Compare(before, after)
            .Where(item => item.Diff != CompareDiff.Equal && IsLayer(item.Category) == layers)
            .Select(item => new CompareValueDifference(
                item.Category + "." + item.Path,
                item.Diff switch { CompareDiff.Added => "added", CompareDiff.Removed => "removed", _ => "changed" },
                Element(item.BaseValue), Element(item.TargetValue)))
            .ToArray();
    }

    private static bool IsLayer(string category) =>
        category is "RootFilesystem" or "History" or "Manifest" or "ResolvedManifest" or "Platforms";

    private static JsonElement? Element(JsonNode? node)
    {
        if (node is null) { return null; }
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString(JsonHelper.CompactSettings));
        return document.RootElement.Clone();
    }

    internal static MetadataDocument GetMetadata(ResolvedImage source, bool includeDistributionMetadata, bool preserveEmptyValues = false)
    {
        string configContent = Valleysoft.Dredge.ImageSourceFactory.DecodeConfiguration(source.ConfigurationJson);
        JsonObject imageConfig;
        try
        {
            imageConfig = JsonHelper.ParseObject(configContent);
        }
        catch (JsonException)
        {
            throw new JsonException($"Could not deserialize the image config of '{source.Reference}'.");
        }

        MetadataDocument document = new(preserveEmptyValues);
        if (includeDistributionMetadata)
        {
            AddInitialManifest(document, source.InitialManifest ??
                throw new InvalidDataException("The registry image's original manifest is unavailable."));
            AddResolvedManifest(document, source.Resolved);
        }
        AddImageConfig(document, imageConfig);
        return document;
    }

    private static void AddInitialManifest(MetadataDocument document, ManifestInfo manifestInfo)
    {
        document.Add("Manifest", "schemaVersion", GetSchemaVersion(manifestInfo.Manifest));
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
            .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            IManifestReference[] references = [.. platformGroup.OrderBy(static reference => reference.Digest, StringComparer.Ordinal)];
            for (int i = 0; i < references.Length; i++)
            {
                string id = references.Length == 1 ? platformGroup.Key : $"{platformGroup.Key}#{i + 1}";
                string path = $"available[{JsonHelper.Serialize(id, JsonHelper.Settings)}]";
                AddDescriptor(document, "Platforms", path, references[i]);
                AddPlatform(document, "Platforms", $"{path}.platform", references[i].Platform);
            }
        }
    }

    private static void AddResolvedManifest(MetadataDocument document, ResolvedManifest resolved)
    {
        document.Add("ResolvedManifest", "schemaVersion", GetSchemaVersion(resolved.Manifest));
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

    private static int GetSchemaVersion(IManifest manifest) =>
        manifest is DockerRegistryClient.Models.Manifests.Manifest typedManifest
            ? typedManifest.SchemaVersion
            : throw new NotSupportedException(
                $"The manifest media type '{manifest.MediaType}' does not have a supported schema version.");

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
                .Where(static value => !string.IsNullOrEmpty(value)));
    }

    private static void AddImageConfig(MetadataDocument document, JsonObject image)
    {
        foreach ((string name, JsonNode? value) in image.OrderBy(static property => property.Key, StringComparer.Ordinal))
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
                    document.AddToken("Image", "osVersion", value, preserveEmptyValue: false);
                    break;
                default:
                    document.AddToken("Image", LowerFirstCharacter(name), value,
                        preserveEmptyValue: !IsKnownImageConfigField(name));
                    break;
            }
        }
    }

    private static bool IsKnownImageConfigField(string name) =>
        name is "architecture" or "author" or "created" or "os" or "variant";

    private static void AddRootFilesystem(MetadataDocument document, JsonNode? rootFilesystem)
    {
        if (rootFilesystem is not JsonObject rootFilesystemObject)
        {
            return;
        }

        foreach ((string name, JsonNode? value) in rootFilesystemObject
            .OrderBy(static property => property.Key, StringComparer.Ordinal))
        {
            string path = name == "diff_ids" ? "diffIds" : LowerFirstCharacter(name);
            document.AddToken("RootFilesystem", path, value,
                preserveEmptyValue: name is not ("diff_ids" or "type"));
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
                .OrderBy(static property => property.Key, StringComparer.Ordinal))
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
            .OrderBy(static property => property.Key, StringComparer.Ordinal))
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
                    document.AddToken("Config", path, value, preserveEmptyValue: !IsKnownExecutionConfigField(name));
                    break;
            }
        }
    }

    private static bool IsKnownExecutionConfigField(string name) =>
        name is "ArgsEscaped" or "Cmd" or "Entrypoint" or "Healthcheck" or "Labels" or "OnBuild" or
            "Shell" or "StopSignal" or "User" or "WorkingDir";

    private static void AddEnvironment(MetadataDocument document, JsonNode? environment)
    {
        if (environment is not JsonArray environmentArray)
        {
            return;
        }

        // Environment order is not significant, but duplicate names remain ordered because the last assignment can affect runtime behavior.
        foreach (IGrouping<string, string> group in environmentArray
            .Select(GetStringValue)
            .Where(static value => value is not null)
            .Cast<string>()
            .Select(ParseEnvironmentVariable)
            .GroupBy(static variable => variable.Name, static variable => variable.Value)
            .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            string path = $"environment[{JsonHelper.Serialize(group.Key, JsonHelper.Settings)}]";
            string[] values = [.. group];
            document.Add(
                "Config",
                path,
                values.Length == 1
                    ? JsonValue.Create(values[0])
                    : new JsonArray(values.Select(static value => JsonValue.Create(value)).ToArray()));
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

    internal static List<MetadataComparison> Compare(MetadataDocument @base, MetadataDocument target)
    {
        List<MetadataComparison> comparisons = [];
        IEnumerable<string> keys = @base.Items.Keys
            .Union(target.Items.Keys, StringComparer.Ordinal)
            .OrderBy(static key => key, StringComparer.Ordinal);

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

    private static bool MetadataValuesEqual(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) { return left is null && right is null; }
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

    internal sealed class MetadataDocument
    {
        private readonly bool preserveEmptyValues;
        public MetadataDocument(bool preserveEmptyValues = false)
        {
            this.preserveEmptyValues = preserveEmptyValues;
        }
        public SortedDictionary<string, MetadataItem> Items { get; } =
            new(StringComparer.Ordinal);

        public void Add(string category, string path, object? value)
        {
            if (value is null)
            {
                return;
            }

            JsonNode token = value as JsonNode ?? value switch
            {
                string text => JsonValue.Create(text)!,
                bool boolean => JsonValue.Create(boolean)!,
                int integer => JsonValue.Create(integer)!,
                long longValue => JsonValue.Create(longValue)!,
                double doubleValue => JsonValue.Create(doubleValue)!,
                _ => throw new NotSupportedException(
                    $"Metadata value type '{value.GetType().FullName}' is not supported.")
            };
            if (value is JsonNode && token.GetValueKind() == JsonValueKind.Number)
            {
                token = JsonNode.Parse(JsonHelper.NormalizeComparisonNumber(
                    token.ToJsonString(JsonHelper.CompactSettings)))!;
            }
            // A null separator cannot collide with the JSON-escaped user keys embedded in paths.
            Items[$"{category}\0{path}"] = new MetadataItem(category, path, token);
        }

        public void AddToken(string category, string path, JsonNode? value, bool? preserveEmptyValue = null)
        {
            bool preserveEmpty = preserveEmptyValues && preserveEmptyValue != false;
            if (preserveEmpty && (value is null || value is JsonObject { Count: 0 } || value is JsonArray { Count: 0 }))
            {
                Items[$"{category}\0{path}"] = new(category, path, value?.DeepClone());
                return;
            }
            if (value is null)
            {
                return;
            }

            if (value is JsonObject valueObject)
            {
                foreach ((string name, JsonNode? propertyValue) in valueObject
                    .OrderBy(static property => property.Key, StringComparer.Ordinal))
                {
                    AddToken(
                        category,
                        $"{path}[{JsonHelper.Serialize(name, JsonHelper.Settings)}]",
                        propertyValue,
                        preserveEmpty);
                }
            }
            else if (value is JsonArray valueArray)
            {
                for (int i = 0; i < valueArray.Count; i++)
                {
                    AddToken(category, $"{path}[{i}]", valueArray[i], preserveEmpty);
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
                valueArray.Select(GetStringValue).Where(static item => item is not null).Cast<string>());
        }

        public void AddObjectKeys(string category, string path, JsonNode? value)
        {
            if (value is not JsonObject valueObject)
            {
                return;
            }

            AddKeys(category, path, valueObject.Select(static property => property.Key));
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

            foreach ((string key, string value) in values.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                Add(category, $"{path}[{JsonHelper.Serialize(key, JsonHelper.Settings)}]", value);
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
            foreach (string value in values.OrderBy(static value => value, StringComparer.Ordinal))
            {
                Add(category, $"{path}[{JsonHelper.Serialize(value, JsonHelper.Settings)}]", true);
            }
        }
    }

    internal sealed record MetadataItem(string Category, string Path, JsonNode? Value);
}
