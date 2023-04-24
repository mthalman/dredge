using Newtonsoft.Json;

namespace Valleysoft.Dredge.Core;

public class AppSettings
{
    public const string FileCompareToolName = "fileCompareTool";

    [JsonProperty(FileCompareToolName)]
    public FileCompareToolSettings FileCompareTool { get; set; } = new();

    [JsonProperty("platform")]
    public PlatformSettings Platform { get; set; } = new();
}

public class FileCompareToolSettings
{
    [JsonProperty("exePath")]
    public string ExePath { get; set; } = string.Empty;

    [JsonProperty("args")]
    public string Args { get; set; } = string.Empty;
}

public class PlatformSettings
{
    [JsonProperty("os")]
    public string Os { get; set; } = string.Empty;

    [JsonProperty("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    [JsonProperty("arch")]
    public string Architecture { get; set; } = string.Empty;
}
