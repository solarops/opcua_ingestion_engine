using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace Aderis.OpcuaInjection.Models;


// READING JSON TYPES
public class OpcTemplate
{
    public required string TemplateName { get; set; }
    public List<OpcTemplatePointConfiguration> Points = new();
}

public enum ScaleModes
{
    slope_intercept,
    point_slope
}

public class OpcTemplatePointConfigurationBase
{
    [JsonPropertyName("unit")]
    public required string Unit { get; set; }
    [JsonPropertyName("name")]
    public required string TagName { get; set; }
    [JsonPropertyName("measure")]
    public required string MeasureName { get; set; }
}

public class OpcTemplatePointConfiguration : OpcTemplatePointConfigurationBase
{
    [JsonPropertyName("autoScaling")]
    public required OpcTemplatePointConfigurationSlope AutoScaling { get; set; }
}

public class OpcTemplatePointConfigurationSlope
{
    [JsonPropertyName("scale_mode")]
    public required string ScaleMode { get; set; }

    // SlopeIntercept
    [JsonPropertyName("slope")]
    public float Slope { get; set; } = 1;
    [JsonPropertyName("offset")]
    public float Offset { get; set; } = 0;


    // PointSlope
    [JsonPropertyName("value_min")]
    public float ValueMin { get; set; } = 0;
    [JsonPropertyName("value_max")]
    public float ValueMax { get; set; } = 0;
    [JsonPropertyName("target_min")]
    public float TargetMin { get; set; } = 0;
    [JsonPropertyName("target_max")]
    public float TargetMax { get; set; } = 0;
}