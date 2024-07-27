using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Valleysoft.Dredge;

internal static class JsonHelper
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.Indented,
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public static readonly JsonSerializerSettings SettingsNoCamelCase = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.Indented
    };
}
