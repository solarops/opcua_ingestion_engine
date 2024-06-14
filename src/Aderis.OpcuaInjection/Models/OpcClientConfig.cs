namespace Aderis.OpcuaInjection.Models;
using System.Text.Json.Serialization;
using System.Collections.Generic;

public class OpcClientConfig
{
    [JsonPropertyName("connections")]
    public List<OpcClientConnection> Connections { get; set; } = new();
}

public class OpcClientConnection
{
    [JsonPropertyName("connection_name")]
    public required string ConnectionName { get; set; }
    
    [JsonPropertyName("url")]
    public required string Url { get; set; }
    
    [JsonPropertyName("browse_exclusion_folders")]
    public required List<string> BrowseExclusionFolders { get; set; }
}