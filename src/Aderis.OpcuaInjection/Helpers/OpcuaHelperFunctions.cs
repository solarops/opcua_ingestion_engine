using Aderis.OpcuaInjection.Models;
using Opc.Ua.Client;
using Opc.Ua;
using System.Text.Json;
using System.Text;

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

    // Alex - Will need to re-evaluate, do more testing on sites with Acuity
    public static string GetFileTextLock(string filePath, int iteration = 0)
    {
        if (iteration > 5) throw new Exception($"Could not acquire lock on {filePath}");

        try
        {
            using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Read the contents of the file into a byte array
                byte[] bytes = new byte[fileStream.Length];
                fileStream.Read(bytes, 0, (int)fileStream.Length);

                // Convert byte array to string using UTF-8 encoding
                return Encoding.UTF8.GetString(bytes);
            }
        }
        catch (IOException)
        {
            // Recursively Wait for 
            Thread.Sleep(1500);
            return GetFileTextLock(filePath, iteration + 1);
        }
    }

    public static string GetFileContentsNoLock(string filePath, int iteration=0)
    {
        if (iteration > 5) throw new Exception($"Could not get Lock on {filePath}");
        
        if (!File.Exists(filePath)) throw new Exception($"Filepath {filePath} does not exist...");

        try
        {
            string text = File.ReadAllText(filePath);
            if (text.Length < 1) throw new Exception("empty file...");
            return text;
        }
        catch (Exception)
        {
            Thread.Sleep(500);
            return GetFileContentsNoLock(filePath, iteration+1);
        }    
    }
    public static OpcClientConfig LoadClientConfig()
    {
        // string rawConfig = GetFileTextLock($"{SosConfigPrefix}/opcua_client_config.json");
        string rawConfig = GetFileContentsNoLock($"{SosConfigPrefix}/opcua_client_config.json");
        
        return JsonSerializer.Deserialize<OpcClientConfig>(rawConfig) ?? throw new Exception("Error unpacking opcua client config!");
    }
    public static async Task<Session> GetSessionByUrl(string connectionUrl, int iteration = 0)
    {
        if (iteration > 5) throw new Exception($"Could not get session for {connectionUrl}");

        try
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
        catch (Exception)
        {
            // Wait, Try again
            Thread.Sleep(1500);
            return await GetSessionByUrl(connectionUrl, iteration+1);
        }
    }
}
