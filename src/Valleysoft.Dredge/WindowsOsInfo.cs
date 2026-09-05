using System.Text.Json.Serialization;

namespace Valleysoft.Dredge;

public record WindowsOsInfo
{
    public WindowsOsInfo(WindowsType type, string? version)
    {
        Type = type;
        Version = version;
    }

    public WindowsType Type { get; private set; }
    
    public string? Version { get; private set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<WindowsType>))]
public enum WindowsType
{
    [JsonStringEnumMemberName("Nano Server")]
    NanoServer,

    [JsonStringEnumMemberName("Server Core")]
    ServerCore,

    [JsonStringEnumMemberName("Server")]
    Server,

    [JsonStringEnumMemberName("Windows")]
    Windows
}
