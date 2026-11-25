using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace ProcBridge.Relay.Shared.Json;

/// <summary>
/// The only source of the relay JSON convention (camelCase, enums as strings, nulls omitted). Server (MVC) and
/// client (Integration.Api, CLI) both call it so the two ends cannot drift apart.
/// </summary>
public static class RelayJsonSettings
{
    public static JsonSerializerSettings Create()
    {
        var settings = new JsonSerializerSettings();
        Apply(settings);
        return settings;
    }

    public static void Apply(JsonSerializerSettings settings)
    {
        settings.ContractResolver = new CamelCasePropertyNamesContractResolver();
        settings.NullValueHandling = NullValueHandling.Ignore;
        settings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
        settings.Converters.Add(new StringEnumConverter());
    }
}
