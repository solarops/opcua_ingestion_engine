using System.Text.Json.Serialization;

namespace Aderis.OpcuaInjection.Models;

public class DbConfig
{
    [JsonPropertyName("modvalues_db_config")]
    public DBConnection Connection { get; set; } = new();
    [JsonPropertyName("opcua_client_config")]
    public DBConnection ClientConfigConnection { get; set; } = new();
}

/*
    Set defaults for access to the db if deserialization doesn't work
*/
public class DBConnection
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = "localhost";
    [JsonPropertyName("port")]
    public string Port { get; set; } = "5432";
    [JsonPropertyName("database")]
    public string Database { get; set; } = "acuity";
    [JsonPropertyName("username")]
    public string Username { get; set; } = "postgres";
    [JsonPropertyName("password")]
    public string Password { get; set; } = "password";

    // Host=localhost;Username=myuser;Password=mypassword;Database=mydatabase
    public override string ToString()
    {
        return $"Host={Server};Port={Port};Username={Username};Password={Password};Database={Database}";
    }
}
