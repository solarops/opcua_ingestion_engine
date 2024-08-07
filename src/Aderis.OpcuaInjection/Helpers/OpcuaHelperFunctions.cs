using Aderis.OpcuaInjection.Models;
using Opc.Ua.Configuration;
using Opc.Ua.Client;
using Opc.Ua;
using System.Text.Json;
using System.Text;
using System.Net.Sockets;

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
    public static readonly string SosNodesPrefix = "/opt/sos-config/opcua_nodes";
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
    public static DbConfig LoadDbConfig()
    {
        string rawConfig = GetFileContentsNoLock($"{SosConfigPrefix}/plant_config.json");
        return JsonSerializer.Deserialize<DbConfig>(rawConfig) ?? throw new Exception("Error unpacking plant config!");
    }
    public static async Task<Session> GetSessionByUrl(string connectionUrl, UserIdentity userIdentity, int iteration = 0)
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

            var uri = new Uri(connectionUrl);

            DiscoveryClient client = DiscoveryClient.Create(uri);

            var endpoints = await client.GetEndpointsAsync(null);

            Console.WriteLine($"Discovered {endpoints.Count()} endpoints at {connectionUrl}");
            
            int num = 1;
            foreach (var endpoint in endpoints)
            {
                Console.WriteLine($"Endpoint {num}: {endpoint.EndpointUrl} has security mode: {endpoint.SecurityMode}");
                num += 1;
            }

            var selectedEndpoint = endpoints.FirstOrDefault(x => x.SecurityMode == MessageSecurityMode.None);
            if (selectedEndpoint == null) throw new Exception($"URI with NoSecurity not found for {connectionUrl}");

            // Output the selected endpoint details
            Console.WriteLine($"Selected Endpoint: {selectedEndpoint.EndpointUrl}");
            Console.WriteLine($"Security Mode: {selectedEndpoint.SecurityMode}");
            Console.WriteLine($"Security Policy: {selectedEndpoint.SecurityPolicyUri}");

            // new UserIdentity(new AnonymousIdentityToken())

            return await Session.Create(config,
                new ConfiguredEndpoint(null, selectedEndpoint, EndpointConfiguration.Create(config)),
                false,
                "OPC UA Client Session",
                60000,
                userIdentity,
                null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception occurred when attempting to fetch {connectionUrl}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            // Wait, Try again
            Thread.Sleep(1500);
            return await GetSessionByUrl(connectionUrl, userIdentity, iteration+1);
        }
    }

    public static async Task<bool> IsServerAvailable(string serverUrl)
    {
        try
        {
            Uri uri = new Uri(serverUrl);
            using (var tcpClient = new TcpClient())
            {
                await tcpClient.ConnectAsync(uri.Host, uri.Port);
                return tcpClient.Connected;
            }
        }
        catch
        {
            return false;
        }
    }

    public static void DisposeTimer(object sender)
    {
        if (sender is System.Timers.Timer timer)
        {
            timer.Stop();
            timer.Dispose();
            //Console.WriteLine($"Timer stopped and disposed.");
        }
    }
}
