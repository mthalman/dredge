using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Runtime.Serialization;

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

[JsonConverter(typeof(StringEnumConverter))]
public enum WindowsType
{
    [EnumMember(Value = "Nano Server")]
    NanoServer,

    [EnumMember(Value = "Server Core")]
    ServerCore,

    [EnumMember(Value = "Server")]
    Server,

    [EnumMember(Value = "Windows")]
    Windows
}
