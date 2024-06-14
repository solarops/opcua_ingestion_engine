#nullable disable
using System.Threading;
using System.Text.Json;
using Aderis.OpcuaInjection.Models;
using Opc.Ua;
using Opc.Ua.Client;

using System.Diagnostics;

namespace Aderis.OpcuaInjection.Helpers;

public class OpcuaSubscribe
{
    private static string SosConfigPrefix = "/opt/sos-config/";
    static void SubscribedItemChange(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        // This will be an OPCMonitoredItem
        OPCMonitoredItem opcItem = (OPCMonitoredItem)item;
        foreach (var value in item.DequeueValues())
        {
            Console.WriteLine($"{opcItem.DaqName}: {opcItem.DisplayName} Value: {value.Value}, Status: {value.StatusCode}, Timestamp: {value.SourceTimestamp}");
        }
    }

    public static async Task OpcuaSubscribeStart()
    {
        Dictionary<string, List<OPCMonitoredItem>> connectionPoints = new();

        // Get contents of the following: sos_templates_opcua, site_devices, plant_config (for db connection string), opcua_client_config.json
        try
        {
            string rawTemplates = File.ReadAllText($"{SosConfigPrefix}/sos_templates_opcua.json");

            Dictionary<string, Dictionary<string, List<OpcTemplatePointConfiguration>>> OpcTemplates = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<OpcTemplatePointConfiguration>>>>(rawTemplates);

            string rawConfig = File.ReadAllText($"{SosConfigPrefix}/opcua_client_config.json");
            OpcClientConfig opcClientConfig = JsonSerializer.Deserialize<OpcClientConfig>(rawConfig);

            string rawSiteDevices = File.ReadAllText($"{SosConfigPrefix}/site_devices.json");
            Dictionary<string, List<JSONGenericDevice>> siteDevices = JsonSerializer.Deserialize<Dictionary<string, List<JSONGenericDevice>>>(rawSiteDevices);

            Dictionary<string, string> connectionUrls = new();
            foreach (OpcClientConnection opcClientConnection in opcClientConfig.Connections)
            {
                connectionPoints.Add(opcClientConnection.Url, []);
                connectionUrls.Add(opcClientConnection.ConnectionName, opcClientConnection.Url);
            }

            foreach ((string deviceType, List<JSONGenericDevice> deviceList) in siteDevices)
            {
                foreach (JSONGenericDevice device in deviceList)
                {
                    if (device.Network.Params.Protocol == "OPCUA")
                    {

                        List<OpcTemplatePointConfiguration> points = OpcTemplates[device.DeviceType][device.DaqTemplate];

                        foreach (OpcTemplatePointConfiguration point in points)
                        {
                            Console.WriteLine($"{device.Network.Params.PointNodeId}/{device.Network.Params.Prefix}{point.TagName}");

                            OPCMonitoredItem oPCMonitoredItem = new()
                            {
                                DaqName = device.DaqName,
                                Config = point,
                                StartNodeId = $"{device.Network.Params.PointNodeId}/{device.Network.Params.Prefix}{point.TagName}",
                                AttributeId = Attributes.Value,
                                DisplayName = point.TagName,
                                SamplingInterval = 3000,
                                QueueSize = 10,
                                DiscardOldest = true
                            };

                            oPCMonitoredItem.Notification += SubscribedItemChange;

                            string url = connectionUrls[device.Network.Params.Server];
                            connectionPoints[url].Add(oPCMonitoredItem);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected Exception Occurred when reading config files: {ex.Message}");
        }
        
        foreach ((string serverUrl, List<OPCMonitoredItem> points) in connectionPoints)
        {

            var defaultConfig = new ApplicationConfiguration()
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

            await defaultConfig.Validate(ApplicationType.Client);

            // Select the endpoint with no security for simplicity
            var selectedEndpoint = CoreClientUtils.SelectEndpoint(serverUrl, useSecurity: false, discoverTimeout: 5000);

            // Output the selected endpoint details
            Console.WriteLine($"Selected Endpoint: {selectedEndpoint.EndpointUrl}");
            Console.WriteLine($"Security Mode: {selectedEndpoint.SecurityMode}");
            Console.WriteLine($"Security Policy: {selectedEndpoint.SecurityPolicyUri}");

            // Establish a session with the server
            Session session = await Session.Create(defaultConfig,
                                                      new ConfiguredEndpoint(null, selectedEndpoint, EndpointConfiguration.Create(defaultConfig)),
                                                      false,
                                                      "OPC UA Client Session",
                                                      60000,
                                                      new UserIdentity(new AnonymousIdentityToken()),
                                                      null);
            
            var subscription = new Subscription()
            {
                DisplayName = $"Subscription to {serverUrl}",
                PublishingEnabled = true,
                PublishingInterval = 1000,
                LifetimeCount = 0,
                MinLifetimeInterval = 120_000,
            };

            session.AddSubscription(subscription);
            subscription.Create();
            subscription.AddItems(points);
            subscription.ApplyChanges();
            
        }

        // wait for timeout or Ctrl-C
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }
}


public class LowercaseNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name)
    {
        // Convert the property name to lowercase
        return name.ToLower();
    }
}