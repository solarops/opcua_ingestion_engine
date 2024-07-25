using System.Text.Json.Serialization;
using Opc.Ua.Client;

namespace Aderis.OpcuaInjection.Models;

// READING JSON TYPES
public class JSONGenericDevice
{
    [JsonPropertyName("daq_name")]
    public required string DaqName { get; set; }
    [JsonPropertyName("daq_template")]
    public required string DaqTemplate { get; set; }
    [JsonPropertyName("network")]
    public required NetworkObject Network { get; set; }
    [JsonPropertyName("device_type")]
    public required string DeviceType { get; set; }
}

public class NetworkObject
{
    [JsonPropertyName("params")]
    public required ParamsObject Params { get; set; }
    // public string Type { get; set; }
}


public class ParamsObject
{
    [JsonPropertyName("protocol")]
    public required string Protocol { get; set; }
    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = "";
    [JsonPropertyName("server")]
    public string Server { get; set; } = "";
    [JsonPropertyName("point_node")]
    public string PointNodeId { get; set; } = "";
}


// CUSTOM PROCESS TYPES

public class OPCDevicePoint
{
    public required string DaqName { get; set; }
    public required OpcTemplatePointConfiguration Config { get; set; }
    public required string ExtendedNodeId { get; set; }
}

// Use per-point
public class OPCMonitoredItem : MonitoredItem
{
    public string DaqName { get; set; } = "";

    // Includes Unit, TagName, MeasureName, ScaleMode
    public required OpcTemplatePointConfiguration Config { get; set; }

    public required string ClientUrl { get; set; }
}

public class OPCSubscription : Subscription
{
    public int TimeoutMs { get; set; } = 60000;
}