#nullable disable
using System.Threading;
using System.Text.Json;
using Aderis.OpcuaInjection.Models;
using Opc.Ua;
using Opc.Ua.Client;
using Npgsql;

using System.Diagnostics;
using System.Globalization;

namespace Aderis.OpcuaInjection.Helpers;

public class OpcuaSubscribe
{
    private static string connectionString = "";
    private static string SosConfigPrefix = "/opt/sos-config/";
    static void SubscribedItemChange(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        // This will be an OPCMonitoredItem
        OPCMonitoredItem opcItem = (OPCMonitoredItem)item;
        
        using (var connection = new NpgsqlConnection(connectionString))
        {
            connection.Open();
            foreach (var value in item.DequeueValues())
            {
                DateTime parsedDateTime = DateTime.ParseExact(value.SourceTimestamp.ToString(), "M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);
                try
                {
                    string updateRowQuery = @"
                        UPDATE modvalues
                        SET 
                            tag_value=@tagValue, 
                            measure_value=@measureValue,
                            last_updated=@lastUpdated
                        WHERE device = @device AND measure_name = @measure";
                    
                    using (var updateCommand = new NpgsqlCommand(updateRowQuery, connection))
                    {
                        updateCommand.Parameters.AddWithValue("device", opcItem.DaqName);
                        updateCommand.Parameters.AddWithValue("measure", opcItem.Config.MeasureName);
                        updateCommand.Parameters.AddWithValue("tagValue", value.Value);
                        updateCommand.Parameters.AddWithValue("measureValue", value.Value);
                        updateCommand.Parameters.AddWithValue("lastUpdated", value.SourceTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));

                        updateCommand.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"An Error occurred when saving device {opcItem.DaqName}, {opcItem.Config.MeasureName}: {ex}");
                    connection.Close();
                }
            }
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

            string plantConfig = File.ReadAllText($"{SosConfigPrefix}/plant_config.json");
            MODBUSDBConfig dbConfig = JsonSerializer.Deserialize<MODBUSDBConfig>(plantConfig);
            connectionString = dbConfig.Connection.ToConnectionString();

            // Check for existing table
            using (var connection = new NpgsqlConnection(connectionString))
            {
                try
                {
                    connection.Open();
                    string checkTableQuery = @"
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_name = 'modvalues';";

                    bool tableExists = false;
                    using (var checkCommand = new NpgsqlCommand(checkTableQuery, connection))
                    {
                        using (var reader = checkCommand.ExecuteReader())
                        {
                            tableExists = reader.HasRows;
                        }
                    }

                    // Step 2: If the table does not exist, print a notice and create it
                    if (!tableExists)
                    {
                        Console.WriteLine("Table 'modvalues' must be created...");

                        string createTableQuery = @"
                        CREATE TABLE modvalues (
                            device TEXT,
                            device_type TEXT,
                            tag_name TEXT,
                            tag_value REAL,
                            measure_name TEXT,
                            measure_value REAL,
                            source_unit TEXT,
                            destination_unit TEXT,
                            last_updated TEXT,
                            logging TEXT
                        );";

                        using (var createCommand = new NpgsqlCommand(createTableQuery, connection))
                        {
                            createCommand.ExecuteNonQuery();
                            Console.WriteLine("Table 'modvalues' has been created successfully.");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Table 'modvalues' already exists.");
                    }


                }
                catch (Exception ex)
                {
                    Console.WriteLine($"An Error occurred when checking for table existence: {ex}");
                }
            }

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

                            using (var connection = new NpgsqlConnection(connectionString))
                            {
                                try
                                {
                                    // Open the connection
                                    connection.Open();

                                    // Define the query to check if the row exists
                                    string checkRowQuery = @"
                                    SELECT 1
                                    FROM modvalues
                                    WHERE device = @device AND measure_name = @measure";

                                    // Use parameterized query to avoid SQL injection
                                    using (var command = new NpgsqlCommand(checkRowQuery, connection))
                                    {
                                        // Define parameters and assign values
                                        command.Parameters.AddWithValue("device", device.DaqName);
                                        command.Parameters.AddWithValue("measure", point.MeasureName);

                                        // Execute the command and check for row existence
                                        bool rowExists = false;
                                        using (var reader = command.ExecuteReader())
                                        {
                                            rowExists = reader.HasRows;
                                        }

                                        if (!rowExists)
                                        {
                                            DateTime utcNow = DateTime.UtcNow;

                                            // Format the DateTime to the desired format: yyyy-MM-ddTHH:mm:ss.ffffff
                                            string formattedUtcNow = utcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");

                                            string insertRowQuery = @"
                                                INSERT INTO modvalues (device, device_type, tag_name, tag_value, measure_name, measure_value, source_unit, destination_unit, last_updated, logging)
                                                VALUES (@device, @deviceType, @tagName, @tagValue, @measure, @measureValue, @sourceUnit, @destinationUnit, @lastUpdated, @logging)";

                                            using (var insertCommand = new NpgsqlCommand(insertRowQuery, connection))
                                            {
                                                insertCommand.Parameters.AddWithValue("device", device.DaqName);
                                                insertCommand.Parameters.AddWithValue("deviceType", deviceType);
                                                insertCommand.Parameters.AddWithValue("measure", point.MeasureName);
                                                insertCommand.Parameters.AddWithValue("tagName", point.TagName);
                                                insertCommand.Parameters.AddWithValue("tagValue", 0.0);
                                                insertCommand.Parameters.AddWithValue("measureValue", 0.0);
                                                insertCommand.Parameters.AddWithValue("sourceUnit", point.Unit);
                                                insertCommand.Parameters.AddWithValue("destinationUnit", point.Unit);
                                                insertCommand.Parameters.AddWithValue("lastUpdated", formattedUtcNow.ToString());
                                                insertCommand.Parameters.AddWithValue("logging", "instant");

                                                insertCommand.ExecuteNonQuery();
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"An error occurred inserting row for {device.DaqName} and {point.MeasureName}: {ex.Message}");
                                }
                            }

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