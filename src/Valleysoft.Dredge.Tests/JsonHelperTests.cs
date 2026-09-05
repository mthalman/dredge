using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valleysoft.Dredge.Tests;

public class JsonHelperTests
{
    [Fact]
    public void Serialize_UsesClrPropertyNamesAndCamelCasesDictionaryKeys()
    {
        SerializerContract value = new()
        {
            OsVersion = "1",
            Values = new Dictionary<string, string>
            {
                ["Vendor"] = "value",
                ["Mixed.Case.Key"] = "value"
            }
        };

        string json = JsonHelper.Serialize(value);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal("1", document.RootElement.GetProperty("osVersion").GetString());
        Assert.False(document.RootElement.TryGetProperty("os.version", out _));
        JsonElement values = document.RootElement.GetProperty("values");
        Assert.True(values.TryGetProperty("vendor", out _));
        Assert.True(values.TryGetProperty("mixed.Case.Key", out _));
    }

    [Theory]
    [InlineData("AB\u2028C", "ab\u2028C")]
    [InlineData("FOO\u00a0bar", "foo\u00a0bar")]
    [InlineData("AB\u2029C", "ab\u2029C")]
    [InlineData("IoT", "ioT")]
    [InlineData("MiB", "miB")]
    [InlineData("IdP", "idP")]
    public void Serialize_CamelCasesDictionaryKeysLikeNewtonsoft(string key, string expected)
    {
        string json = JsonHelper.Serialize(new Dictionary<string, string> { [key] = "value" });
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(expected, document.RootElement.EnumerateObject().Single().Name);
    }

    [Fact]
    public void Serialize_UsesNewtonsoftCompatibleEscaping()
    {
        const string Value = "\u000b\u001f\u007f\u0080\u0085\u00a0\u2028\u2029\ufeff\uffff😀";

        string json = JsonSerializer.Serialize(Value, JsonHelper.CompactSettings);

        Assert.Equal(
            "\"\\u000b\\u001f\u007f\u0080\\u0085\u00a0\\u2028\\u2029\ufeff\uffff😀\"",
            json);
    }

    [Fact]
    public void Deserialize_CoercesScalarValuesToStrings()
    {
        SerializerContract value = JsonHelper.Deserialize<SerializerContract>(
            """{"osVersion":5,"values":{"enabled":true}}""")!;

        Assert.Equal("5", value.OsVersion);
        Assert.Equal("true", value.Values["enabled"]);
    }

    [Fact]
    public void FormatJson_IndentsNumericArrays()
    {
        string json = JsonHelper.FormatJson("[1,2,3]")!;

        Assert.Equal(
            $"[{Environment.NewLine}  1,{Environment.NewLine}  2,{Environment.NewLine}  3{Environment.NewLine}]",
            json);
    }

    [Fact]
    public void FormatJson_UsesLastDuplicateProperty()
    {
        string json = JsonHelper.FormatJson("""{"first":1,"dup":1,"last":2,"dup":3}""")!;

        Assert.Equal(
            $"{{{Environment.NewLine}  \"first\": 1,{Environment.NewLine}  \"last\": 2,{Environment.NewLine}  \"dup\": 3{Environment.NewLine}}}",
            json);
    }

    [Fact]
    public void FormatJson_DoesNotReplaceEscapedSurrogateText()
    {
        string json = JsonHelper.FormatJson("""{"value":"\\ud83d\\ude00"}""")!;
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(@"\ud83d\ude00", document.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public void FormatJson_ReplacesOnlyRealSurrogateEscapeAfterLiteralText()
    {
        string json = JsonHelper.FormatJson("""{"value":"\\ud83d\ude00"}""")!;
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal("\\ud83d\ufffd", document.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public void FormatJson_ReplacesInvalidSurrogateEscapes()
    {
        string json = JsonHelper.FormatJson("""{"value":"\ud800"}""")!;
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal("\ufffd", document.RootElement.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("2023-01-02T03:04")]
    [InlineData("2023-01-02T03:04:05+5")]
    [InlineData("2023-01-02T03:04:05.")]
    [InlineData("2023-01-02T03:04:05.Z")]
    [InlineData("2023-01-02T03:04:05.+01:00")]
    [InlineData("2023-01-02T03:04:05.111111111Z")]
    [InlineData("2023-01-02T03:04:05.111111111+01:00")]
    [InlineData("Mon, 02 Jan 2023 03:04:05 GMT")]
    [InlineData("2023-01-02 03:04:05 GMT")]
    public void FormatJson_DoesNotConvertNonNewtonsoftDates(string value)
    {
        string json = JsonHelper.FormatJson(JsonSerializer.Serialize(value))!;
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(value, document.RootElement.GetString());
    }

    [Theory]
    [InlineData("2023-01-02T03:04:05+05")]
    [InlineData("2023-01-02T03:04:05z")]
    [InlineData("2023-01-02T03:04:05+0100")]
    [InlineData("2023-01-02T03:04:05-0800")]
    [InlineData("2023-01-02T24:00:00Z")]
    [InlineData("2023-01-02T03:04:05+99:00")]
    [InlineData("2023-01-02T03:04:05.111111111")]
    public void FormatJson_ConvertsNewtonsoftDateVariants(string value)
    {
        string json = JsonHelper.FormatJson(JsonSerializer.Serialize(value))!;
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.NotEqual(value, document.RootElement.GetString());
    }

    [Fact]
    public void FormatJson_ConvertsMicrosoftDates()
    {
        string json = JsonHelper.FormatJson(JsonSerializer.Serialize("/Date(1198908717056)/"))!;
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(
            "2007-12-29T06:11:57.056Z",
            document.RootElement.GetString());
    }

    [Theory]
    [InlineData("/Date(253402300800000)/")]
    [InlineData("/Date(-62135596800001)/")]
    public void FormatJson_ThrowsForOutOfRangeMicrosoftDates(string value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => JsonHelper.FormatJson(JsonSerializer.Serialize(value)));
    }

    [Fact]
    public void FormatJson_RejectsUppercaseUnicodeEscape()
    {
        Assert.ThrowsAny<JsonException>(() => JsonHelper.FormatJson("\"\\Ud800\""));
    }

    private sealed class SerializerContract
    {
        [JsonPropertyName("os.version")]
        public string OsVersion { get; set; } = string.Empty;

        public Dictionary<string, string> Values { get; set; } = [];
    }
}
