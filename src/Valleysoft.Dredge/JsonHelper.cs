using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace Valleysoft.Dredge;

internal static class JsonHelper
{
    private static readonly JsonNamingPolicy camelCaseNamingPolicy =
        new NewtonsoftCompatibleCamelCaseNamingPolicy();

    public static readonly JsonSerializerOptions Settings = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = NewtonsoftCompatibleJavaScriptEncoder.Instance,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DictionaryKeyPolicy = camelCaseNamingPolicy,
        PropertyNamingPolicy = camelCaseNamingPolicy,
        TypeInfoResolver = CreateTypeInfoResolver(camelCaseNamingPolicy),
        Converters =
        {
            new NewtonsoftCompatibleStringConverter()
        }
    };

    public static readonly JsonSerializerOptions CompactSettings = new(Settings)
    {
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions SettingsNoCamelCase = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = NewtonsoftCompatibleJavaScriptEncoder.Instance,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        TypeInfoResolver = CreateTypeInfoResolver(namingPolicy: null),
    };

    public static string Serialize(object? value, JsonSerializerOptions? options = null) =>
        value is null
            ? JsonSerializer.Serialize(value, options ?? Settings)
            : JsonSerializer.Serialize(value, value.GetType(), options ?? Settings);

    public static T? Deserialize<T>(string json, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<T>(json, options ?? Settings);

    public static string? FormatJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(
            ReplaceInvalidSurrogateEscapes(json),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        if (document.RootElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        StringBuilder output = new();
        WriteJsonValue(document.RootElement, output, depth: 0);
        return output.ToString();
    }

    public static JsonObject ParseObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(
            ReplaceInvalidSurrogateEscapes(json),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        return CreateNode(document.RootElement) as JsonObject ??
            throw new JsonException("The JSON value could not be converted to an object.");
    }

    public static string MergeDuplicateObjects(string json)
    {
        using JsonDocument document = JsonDocument.Parse(
            ReplaceInvalidSurrogateEscapes(json),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        return CreateMergedNode(document.RootElement)?.ToJsonString(CompactSettings) ?? "null";
    }

    public static string NormalizeNewtonsoftNumber(string rawValue)
    {
        if (!rawValue.Contains('.') &&
            rawValue.IndexOfAny(['e', 'E']) < 0)
        {
            return BigInteger.Parse(rawValue, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture);
        }

        double value = double.Parse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value))
        {
            return JsonSerializer.Serialize(
                value > 0 ? "Infinity" : "-Infinity",
                CompactSettings);
        }

        string formatted = value.ToString("R", CultureInfo.InvariantCulture);
        return formatted.Contains('.') || formatted.IndexOfAny(['e', 'E']) >= 0
            ? formatted.Replace('e', 'E')
            : $"{formatted}.0";
    }

    private static IJsonTypeInfoResolver CreateTypeInfoResolver(JsonNamingPolicy? namingPolicy)
    {
        DefaultJsonTypeInfoResolver resolver = new();
        resolver.Modifiers.Add(typeInfo =>
        {
            foreach (JsonPropertyInfo property in typeInfo.Properties)
            {
                if (property.AttributeProvider is MemberInfo member)
                {
                    JsonPropertyNameAttribute? propertyName =
                        member.GetCustomAttribute<JsonPropertyNameAttribute>();
                    string name = propertyName is not null &&
                        member.DeclaringType?.Assembly == typeof(JsonHelper).Assembly
                            ? propertyName.Name
                            : member.Name;
                    property.Name = namingPolicy?.ConvertName(name) ?? name;
                }
            }
        });
        return resolver;
    }

    private static void WriteJsonValue(JsonElement value, StringBuilder output, int depth)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                JsonProperty[] properties = GetLastProperties(value);
                if (properties.Length == 0)
                {
                    output.Append("{}");
                    break;
                }

                output.AppendLine("{");
                for (int i = 0; i < properties.Length; i++)
                {
                    AppendIndent(output, depth + 1);
                    output.Append(JsonSerializer.Serialize(properties[i].Name, CompactSettings));
                    output.Append(": ");
                    WriteJsonValue(properties[i].Value, output, depth + 1);
                    output.AppendLine(i == properties.Length - 1 ? string.Empty : ",");
                }
                AppendIndent(output, depth);
                output.Append('}');
                break;
            case JsonValueKind.Array:
                JsonElement[] items = [.. value.EnumerateArray()];
                if (items.Length == 0)
                {
                    output.Append("[]");
                    break;
                }

                output.AppendLine("[");
                for (int i = 0; i < items.Length; i++)
                {
                    AppendIndent(output, depth + 1);
                    WriteJsonValue(items[i], output, depth + 1);
                    output.AppendLine(i == items.Length - 1 ? string.Empty : ",");
                }
                AppendIndent(output, depth);
                output.Append(']');
                break;
            case JsonValueKind.String:
                string stringValue = value.GetString()!;
                if (TryParseNewtonsoftDate(stringValue, out DateTime date))
                {
                    output.Append(JsonSerializer.Serialize(date, CompactSettings));
                }
                else
                {
                    output.Append(JsonSerializer.Serialize(stringValue, CompactSettings));
                }
                break;
            case JsonValueKind.Number:
                output.Append(NormalizeNewtonsoftNumber(value.GetRawText()));
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            case JsonValueKind.Null:
                output.Append("null");
                break;
            default:
                throw new JsonException($"Unexpected JSON value kind '{value.ValueKind}'.");
        }
    }

    private static JsonProperty[] GetLastProperties(JsonElement value)
    {
        List<JsonProperty> properties = [];
        foreach (JsonProperty property in value.EnumerateObject())
        {
            properties.RemoveAll(existing =>
                string.Equals(existing.Name, property.Name, StringComparison.Ordinal));
            properties.Add(property);
        }

        return [.. properties];
    }

    private static bool TryParseNewtonsoftDate(string value, out DateTime date)
    {
        date = default;
        Match microsoftDate = Regex.Match(
            value,
            @"^/Date\((?<milliseconds>-?\d+)(?<offset>[+-]\d+)?\)/$",
            RegexOptions.CultureInvariant);
        if (microsoftDate.Success)
        {
            if (!long.TryParse(
                microsoftDate.Groups["milliseconds"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long milliseconds))
            {
                return false;
            }

            DateTimeOffset dateTime = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            date = microsoftDate.Groups["offset"].Success
                ? dateTime.LocalDateTime
                : dateTime.UtcDateTime;
            return true;
        }

        if (value.Length is < 19 or > 40 ||
            value[10] != 'T' ||
            !char.IsDigit(value[0]))
        {
            return false;
        }

        string normalizedValue = value.EndsWith('z')
            ? $"{value[..^1]}Z"
            : value;
        Match shortOffset = Regex.Match(
            normalizedValue,
            @"(?<offset>[+-]\d{2})$",
            RegexOptions.CultureInvariant);
        if (shortOffset.Success)
        {
            normalizedValue = $"{normalizedValue}:00";
        }
        Match compactOffset = Regex.Match(
            normalizedValue,
            @"(?<sign>[+-])(?<hours>\d{2})(?<minutes>\d{2})$",
            RegexOptions.CultureInvariant);
        if (compactOffset.Success)
        {
            normalizedValue = normalizedValue[..compactOffset.Index] +
                $"{compactOffset.Groups["sign"].Value}{compactOffset.Groups["hours"].Value}:{compactOffset.Groups["minutes"].Value}";
        }

        Match isoDate = Regex.Match(
            normalizedValue,
            @"^(?<date>\d{4}-\d{2}-\d{2})T(?<hour>\d{2}):(?<minute>\d{2}):(?<second>\d{2})(?<fraction>\.\d+)?(?<zone>Z|[+-]\d{2}:\d{2})?$",
            RegexOptions.CultureInvariant);
        if (!isoDate.Success)
        {
            return false;
        }

        string fraction = isoDate.Groups["fraction"].Value;
        string zone = isoDate.Groups["zone"].Value;
        if (fraction.Length == 10 && zone.Length == 0)
        {
            fraction = fraction[..8];
        }
        else if (fraction.Length > 8)
        {
            return false;
        }

        bool endOfDay = isoDate.Groups["hour"].Value == "24";
        if (endOfDay &&
            (isoDate.Groups["minute"].Value != "00" ||
             isoDate.Groups["second"].Value != "00" ||
             fraction.Any(character => character != '.' && character != '0')))
        {
            return false;
        }

        string hour = endOfDay ? "00" : isoDate.Groups["hour"].Value;
        Match offset = Regex.Match(zone, @"^(?<sign>[+-])(?<hours>\d{2}):(?<minutes>\d{2})$");
        if (offset.Success &&
            int.Parse(offset.Groups["hours"].Value, CultureInfo.InvariantCulture) > 14)
        {
            if (!DateTime.TryParseExact(
                $"{isoDate.Groups["date"].Value}T{hour}:{isoDate.Groups["minute"].Value}:{isoDate.Groups["second"].Value}{fraction}",
                ["yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime localDate))
            {
                return false;
            }

            int offsetMinutes =
                int.Parse(offset.Groups["hours"].Value, CultureInfo.InvariantCulture) * 60 +
                int.Parse(offset.Groups["minutes"].Value, CultureInfo.InvariantCulture);
            if (offset.Groups["sign"].Value == "-")
            {
                offsetMinutes = -offsetMinutes;
            }
            date = DateTime.SpecifyKind(localDate.AddMinutes(-offsetMinutes), DateTimeKind.Utc)
                .ToLocalTime();
        }
        else if (!DateTime.TryParseExact(
            $"{isoDate.Groups["date"].Value}T{hour}:{isoDate.Groups["minute"].Value}:{isoDate.Groups["second"].Value}{fraction}{zone}",
            ["yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-ddTHH:mm:ssK"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out date))
        {
            return false;
        }

        if (endOfDay)
        {
            date = date.AddDays(1);
        }
        return true;
    }

    private static JsonNode? CreateNode(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Object => CreateObject(value),
            JsonValueKind.Array => new JsonArray(value.EnumerateArray().Select(CreateNode).ToArray()),
            JsonValueKind.String => JsonValue.Create(value.GetString()),
            JsonValueKind.Number => JsonNode.Parse(value.GetRawText()),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            JsonValueKind.Null => null,
            _ => throw new JsonException($"Unexpected JSON value kind '{value.ValueKind}'.")
        };

    private static JsonObject CreateObject(JsonElement value)
    {
        JsonObject result = [];
        foreach (JsonProperty property in GetLastProperties(value))
        {
            result[property.Name] = CreateNode(property.Value);
        }
        return result;
    }

    private static JsonNode? CreateMergedNode(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Object => CreateMergedObject(value),
            JsonValueKind.Array => new JsonArray(
                value.EnumerateArray().Select(CreateMergedNode).ToArray()),
            JsonValueKind.String => JsonValue.Create(value.GetString()),
            JsonValueKind.Number => JsonNode.Parse(value.GetRawText()),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            JsonValueKind.Null => null,
            _ => throw new JsonException($"Unexpected JSON value kind '{value.ValueKind}'.")
        };

    private static JsonObject CreateMergedObject(JsonElement value)
    {
        JsonObject result = [];
        foreach (JsonProperty property in value.EnumerateObject())
        {
            JsonNode? propertyValue = CreateMergedNode(property.Value);
            string? existingName = result
                .Select(item => item.Key)
                .FirstOrDefault(name =>
                    string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase));
            if (existingName is not null &&
                result[existingName] is JsonObject existingObject &&
                propertyValue is JsonObject newObject)
            {
                MergeObjects(existingObject, newObject);
            }
            else
            {
                if (existingName is not null)
                {
                    result.Remove(existingName);
                }
                result[property.Name] = propertyValue;
            }
        }
        return result;
    }

    private static void MergeObjects(JsonObject target, JsonObject source)
    {
        foreach ((string name, JsonNode? value) in source.ToArray())
        {
            string? targetName = target
                .Select(item => item.Key)
                .FirstOrDefault(existingName =>
                    string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase));
            if (targetName is not null &&
                target[targetName] is JsonObject targetObject &&
                value is JsonObject sourceObject)
            {
                MergeObjects(targetObject, sourceObject);
            }
            else
            {
                if (targetName is not null)
                {
                    target.Remove(targetName);
                }
                target[name] = value?.DeepClone();
            }
        }
    }

    private static void AppendIndent(StringBuilder output, int depth) =>
        output.Append(' ', depth * 2);

    private static string ReplaceInvalidSurrogateEscapes(string json)
    {
        StringBuilder result = new(json.Length);
        for (int i = 0; i < json.Length;)
        {
            if (TryGetUnicodeEscape(json, i, out char value))
            {
                if (char.IsHighSurrogate(value) &&
                    TryGetUnicodeEscape(json, i + 6, out char lowSurrogate) &&
                    char.IsLowSurrogate(lowSurrogate))
                {
                    result.Append(json, i, 12);
                    i += 12;
                    continue;
                }

                if (char.IsSurrogate(value))
                {
                    result.Append(@"\ufffd");
                    i += 6;
                    continue;
                }
            }

            result.Append(json[i]);
            i++;
        }
        return result.ToString();
    }

    private static bool TryGetUnicodeEscape(string json, int index, out char value)
    {
        value = default;
        if (index + 6 > json.Length ||
            json[index] != '\\' ||
            json[index + 1] != 'u')
        {
            return false;
        }

        int precedingSlashes = 0;
        for (int i = index - 1; i >= 0 && json[i] == '\\'; i--)
        {
            precedingSlashes++;
        }
        if (precedingSlashes % 2 != 0)
        {
            return false;
        }

        bool parsed = ushort.TryParse(
            json.AsSpan(index + 2, 4),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out ushort scalar);
        value = (char)scalar;
        return parsed;
    }

    private sealed class NewtonsoftCompatibleStringConverter : JsonConverter<string>
    {
        public override string? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => GetRawValue(ref reader),
                JsonTokenType.True => bool.TrueString.ToLowerInvariant(),
                JsonTokenType.False => bool.FalseString.ToLowerInvariant(),
                JsonTokenType.Null => null,
                _ => throw new JsonException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Unexpected token {0} when parsing a string.",
                        reader.TokenType))
            };

        public override void Write(
            Utf8JsonWriter writer,
            string value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value);

    }

    private sealed unsafe class NewtonsoftCompatibleJavaScriptEncoder : JavaScriptEncoder
    {
        public static readonly NewtonsoftCompatibleJavaScriptEncoder Instance = new();

        public override int MaxOutputCharactersPerInputCharacter => 6;

        public override bool WillEncode(int unicodeScalar) =>
            unicodeScalar is '"' or '\\' or < 0x20 or 0x85 or 0x2028 or 0x2029 ||
            unicodeScalar is >= 0xD800 and <= 0xDFFF;

        public override int FindFirstCharacterToEncode(char* text, int textLength)
        {
            for (int i = 0; i < textLength; i++)
            {
                if (char.IsHighSurrogate(text[i]) &&
                    i + 1 < textLength &&
                    char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                if (WillEncode(text[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        public override bool TryEncodeUnicodeScalar(
            int unicodeScalar,
            char* buffer,
            int bufferLength,
            out int numberOfCharactersWritten)
        {
            string encoded = unicodeScalar switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => $"\\u{unicodeScalar:x4}"
            };
            if (encoded.Length > bufferLength)
            {
                numberOfCharactersWritten = 0;
                return false;
            }

            encoded.AsSpan().CopyTo(new Span<char>(buffer, bufferLength));
            numberOfCharactersWritten = encoded.Length;
            return true;
        }
    }

    private sealed class NewtonsoftCompatibleCamelCaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name)
        {
            if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0]))
            {
                return name;
            }

            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                bool hasNext = i + 1 < chars.Length;
                if (i == 1 && !char.IsUpper(chars[i]))
                {
                    break;
                }

                if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                {
                    if (char.IsSeparator(chars[i + 1]))
                    {
                        chars[i] = char.ToLowerInvariant(chars[i]);
                    }
                    break;
                }

                chars[i] = char.ToLowerInvariant(chars[i]);
            }
            return new string(chars);
        }
    }

    private static string GetRawValue(ref Utf8JsonReader reader) =>
        reader.HasValueSequence
            ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
            : Encoding.UTF8.GetString(reader.ValueSpan);
}
