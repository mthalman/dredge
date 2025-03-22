using Newtonsoft.Json;

namespace Valleysoft.Dredge;

public record LinuxOsInfo
{
    [JsonProperty("PRETTY_NAME")]
    public string? PrettyName { get; private set; }

    [JsonProperty("NAME")]
    public string? Name { get; private set; }

    [JsonProperty("ID")]
    public string? Id { get; private set; }

    [JsonProperty("ID_LIKE")]
    public string[]? IdLike { get; private set; }

    [JsonProperty("VERSION")]
    public string? Version { get; private set; }

    [JsonProperty("VERSION_ID")]
    public string? VersionId { get; private set; }

    [JsonProperty("VERSION_CODENAME")]
    public string? VersionCodeName { get; private set; }

    [JsonProperty("BUILD_ID")]
    public string? BuildId { get; private set; }

    [JsonProperty("IMAGE_ID")]
    public string? ImageId { get; private set; }

    [JsonProperty("IMAGE_VERSION")]
    public string? ImageVersion { get; private set; }

    [JsonProperty("VARIANT")]
    public string? Variant { get; private set; }

    [JsonProperty("VARIANT_ID")]
    public string? VariantId { get; private set; }

    [JsonProperty("HOME_URL")]
    public string? HomeUrl { get; private set; }

    [JsonProperty("SUPPORT_URL")]
    public string? SupportUrl { get; private set; }

    [JsonProperty("BUG_REPORT_URL")]
    public string? BugReportUrl { get; private set; }

    [JsonProperty("PRIVACY_POLICY_URL")]
    public string? PrivacyPolicyUrl { get; private set; }

    [JsonProperty("CPE_NAME")]
    public string? CpeName { get; private set; }

    public static LinuxOsInfo Parse(string osInfoContent)
    {
        Dictionary<string, string> osFields = new([..osInfoContent
            .Split("\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                int index = line.IndexOf('=');
                return new KeyValuePair<string, string>(line[..index], line[(index + 1)..].TrimStart('"').TrimEnd('"'));
            })]);

        return new LinuxOsInfo
        {
            PrettyName = osFields.GetValueOrDefault("PRETTY_NAME"),
            Name = osFields.GetValueOrDefault("NAME"),
            Id = osFields.GetValueOrDefault("ID"),
            IdLike = osFields.GetValueOrDefault("ID_LIKE")?.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            Version = osFields.GetValueOrDefault("VERSION"),
            VersionId = osFields.GetValueOrDefault("VERSION_ID"),
            VersionCodeName = osFields.GetValueOrDefault("VERSION_CODENAME"),
            BuildId = osFields.GetValueOrDefault("BUILD_ID"),
            ImageId = osFields.GetValueOrDefault("IMAGE_ID"),
            ImageVersion = osFields.GetValueOrDefault("IMAGE_VERSION"),
            Variant = osFields.GetValueOrDefault("VARIANT"),
            VariantId = osFields.GetValueOrDefault("VARIANT_ID"),
            HomeUrl = osFields.GetValueOrDefault("HOME_URL"),
            SupportUrl = osFields.GetValueOrDefault("SUPPORT_URL"),
            BugReportUrl = osFields.GetValueOrDefault("BUG_REPORT_URL"),
            PrivacyPolicyUrl = osFields.GetValueOrDefault("PRIVACY_POLICY_URL"),
            CpeName = osFields.GetValueOrDefault("CPE_NAME")
        };
    }
}
