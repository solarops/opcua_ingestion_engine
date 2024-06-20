using System.Text.Json.Serialization;

namespace Aderis.OpcuaInjection.Models;

public class MODBUSDBConfig
{
    [JsonPropertyName("modvalues_db_config")]
    public required DBConnection Connection { get; set; }
}

public class DBConnection
{
    [JsonPropertyName("server")]
    public required string Server { get; set; }
    [JsonPropertyName("port")]
    public required string Port { get; set; }
    [JsonPropertyName("database")]
    public required string Database { get; set; }
    [JsonPropertyName("username")]
    public required string Username { get; set; }
    [JsonPropertyName("password")]
    public required string Password { get; set; }

    // Host=localhost;Username=myuser;Password=mypassword;Database=mydatabase
    public string ToConnectionString()
    {
        return $"Host={Server};Port={Port};Username={Username};Password={Password};Database={Database}";
    }
}
