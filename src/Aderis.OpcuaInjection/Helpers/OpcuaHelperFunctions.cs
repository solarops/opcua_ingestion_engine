using Aderis.OpcuaInjection.Models;
using Opc.Ua.Client;
using Opc.Ua;
using System.Text.Json;

namespace Aderis.OpcuaInjection.Helpers;

public class OpcuaHelperFunctions
{
    public class LowercaseNamingPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name)
        {
            // Convert the property name to lowercase
            return name.ToLower();
        }
    }
    public static readonly string SosConfigPrefix = "/opt/sos-config";
    public static OpcClientConfig LoadClientConfig()
    {
        string rawConfig = File.ReadAllText($"{SosConfigPrefix}/opcua_client_config.json");
        return JsonSerializer.Deserialize<OpcClientConfig>(rawConfig) ?? throw new Exception("Error unpacking opcua client config!");
    }
    public static async Task<Session> GetSessionByUrl(string connectionUrl)
    {
        var config = new ApplicationConfiguration()
        {
            ApplicationName = "OPC UA Client",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                AutoAcceptUntrustedCertificates = true,
                ApplicationCertificate = new CertificateIdentifier()
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
        };

        // // Validate the application certificate
        await config.Validate(ApplicationType.Client);

        var selectedEndpoint = CoreClientUtils.SelectEndpoint(connectionUrl, useSecurity: false, discoverTimeout: 5000);

        // Output the selected endpoint details
        Console.WriteLine($"Selected Endpoint: {selectedEndpoint.EndpointUrl}");
        Console.WriteLine($"Security Mode: {selectedEndpoint.SecurityMode}");
        Console.WriteLine($"Security Policy: {selectedEndpoint.SecurityPolicyUri}");

        return await Session.Create(config,
            new ConfiguredEndpoint(null, selectedEndpoint, EndpointConfiguration.Create(config)),
            false,
            "OPC UA Client Session",
            60000,
            new UserIdentity(new AnonymousIdentityToken()),
            null);
    }
}
