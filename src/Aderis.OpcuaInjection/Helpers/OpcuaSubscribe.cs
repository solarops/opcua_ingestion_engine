#nullable disable
using System.Text.Json;
using Aderis.OpcuaInjection.Models;
using Opc.Ua;
using Opc.Ua.Client;
using Npgsql;

using System.Globalization;

namespace Aderis.OpcuaInjection.Helpers;

public class OpcuaSubscribe
{
    private static FileSystemWatcher watcher= new();
    private static CancellationTokenSource FileSystemReloadCancel = new();
    private static CancellationToken GlobalCancel = new();
    private static string ConnectionString = LoadConnectionString();
    private static Dictionary<string, Dictionary<string, List<OpcTemplatePointConfiguration>>> OpcTemplates = LoadOpcTemplates();
    private static OpcClientConfig OpcClientConfig = OpcuaHelperFunctions.LoadClientConfig();
    private static Dictionary<string, List<JSONGenericDevice>> SiteDevices = LoadSiteDevices();

    private static Dictionary<string, Dictionary<string, List<OpcTemplatePointConfiguration>>> LoadOpcTemplates()
    {
        string rawTemplates = File.ReadAllText($"{OpcuaHelperFunctions.SosConfigPrefix}/sos_templates_opcua.json");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<OpcTemplatePointConfiguration>>>>(rawTemplates);
    }
    
    private static Dictionary<string, List<JSONGenericDevice>> LoadSiteDevices()
    {
        string rawSiteDevices = File.ReadAllText($"{OpcuaHelperFunctions.SosConfigPrefix}/site_devices.json");
        return JsonSerializer.Deserialize<Dictionary<string, List<JSONGenericDevice>>>(rawSiteDevices);
    }
    private static string LoadConnectionString()
    {
        string plantConfig = File.ReadAllText($"{OpcuaHelperFunctions.SosConfigPrefix}/plant_config.json");
        MODBUSDBConfig dbConfig = JsonSerializer.Deserialize<MODBUSDBConfig>(plantConfig);
        return dbConfig.Connection.ToConnectionString();
    }

    static void TemplateFileChanged(object source, FileSystemEventArgs e)
    {
        switch (e.Name)
        {
            case "sos_templates_opcua.json":
                Console.WriteLine("Templates OPCUA Changed...");
                OpcTemplates = LoadOpcTemplates();
                goto case "CHANGED";
            case "opcua_client_config.json":
                Console.WriteLine("Client Config changed...");
                OpcClientConfig = OpcuaHelperFunctions.LoadClientConfig();
                goto case "CHANGED";
            case "site_devices.json":
                Console.WriteLine("Devices changed...");
                SiteDevices = LoadSiteDevices();
                goto case "CHANGED";
            // case "plant_config.json":
            //     ConnectionString = LoadConnectionString();
            // goto case "CHANGED";
            case "CHANGED":
                // Synchronously cancel
                FileSystemReloadCancel.Cancel();
                
                break;
        }
    }
    static void SubscribedItemChange(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        // This will be an OPCMonitoredItem
        OPCMonitoredItem opcItem = (OPCMonitoredItem)item;

        using (var connection = new NpgsqlConnection(ConnectionString))
        {
            connection.Open();
            foreach (var value in item.DequeueValues())
            {
                try
                {
                    OpcTemplatePointConfiguration config = opcItem.Config;
                    double scaledValue = Convert.ToDouble(value.Value);
                    OpcTemplatePointConfigurationSlope AutoScaling = config.AutoScaling;

                    switch (AutoScaling.ScaleMode)
                    {
                        case "slope_intercept":
                            scaledValue = Math.Round((scaledValue * AutoScaling.Slope) + AutoScaling.Offset, 3);
                            break;
                        case "point_slope":
                            scaledValue = Math.Round((AutoScaling.TargetMax - AutoScaling.TargetMin) / (AutoScaling.ValueMax - AutoScaling.ValueMin) * (scaledValue - AutoScaling.ValueMin) + AutoScaling.TargetMin, 3);
                            break;
                    }

                    string updateRowQuery = @"
                        UPDATE modvalues
                        SET 
                            tag_value=@tagValue, 
                            measure_value=@measureValue,
                            last_updated=@lastUpdated
                        WHERE device = @device AND measure_name = @measure";

                    // Console.WriteLine(scaledValue);

                    using (var updateCommand = new NpgsqlCommand(updateRowQuery, connection))
                    {
                        updateCommand.Parameters.AddWithValue("device", opcItem.DaqName);
                        updateCommand.Parameters.AddWithValue("measure", config.MeasureName);
                        updateCommand.Parameters.AddWithValue("tagValue", scaledValue);
                        updateCommand.Parameters.AddWithValue("measureValue", scaledValue);
                        updateCommand.Parameters.AddWithValue("lastUpdated", value.SourceTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));

                        int affectedRows = updateCommand.ExecuteNonQuery();

                        // Console.WriteLine(affectedRows);
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


    public static async Task Start(CancellationToken GlobalStop)
    {
        // Register Passed 
        GlobalCancel = GlobalStop;

        // Set the directory to monitor
        watcher.Path = OpcuaHelperFunctions.SosConfigPrefix;

        // Watch for changes in LastWrite times, and the renaming of files or directories
        watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.DirectoryName;

        // Only watch json files (you can modify this to watch other types of files)
        watcher.Filter = "*.json";

        // Add event handlers for each type of file change event
        watcher.Changed += TemplateFileChanged;

        // Begin watching
        watcher.EnableRaisingEvents = true;

        await OpcuaSubscribeStart();
    }


    private static async Task OpcuaSubscribeStart()
    {
        Dictionary<string, List<OPCMonitoredItem>> connectionPoints = new();

        // Get contents of the following: sos_templates_opcua, site_devices, plant_config (for db connection string), opcua_client_config.json
        try
        {
            // Check for existing table
            using (var connection = new NpgsqlConnection(ConnectionString))
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
            foreach (OpcClientConnection opcClientConnection in OpcClientConfig.Connections)
            {
                connectionPoints.Add(opcClientConnection.Url, []);
                connectionUrls.Add(opcClientConnection.ConnectionName, opcClientConnection.Url);
            }

            foreach ((string deviceType, List<JSONGenericDevice> deviceList) in SiteDevices)
            {
                foreach (JSONGenericDevice device in deviceList)
                {
                    if (device.Network.Params.Protocol == "OPCUA")
                    {
                        List<OpcTemplatePointConfiguration> points = OpcTemplates[device.DeviceType][device.DaqTemplate];

                        foreach (OpcTemplatePointConfiguration point in points)
                        {

                            using (var connection = new NpgsqlConnection(ConnectionString))
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


        List<Session> opcClients = new();
        foreach ((string serverUrl, List<OPCMonitoredItem> points) in connectionPoints)
        {
            Session session = await OpcuaHelperFunctions.GetSessionByUrl(serverUrl);
            opcClients.Add(session);

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

        try
        {
            while (true)
            {
                // Evaluate If has ben cancelled.
                FileSystemReloadCancel.Token.ThrowIfCancellationRequested();

                // Global Reload, exit
                GlobalCancel.ThrowIfCancellationRequested();

                // 5s
                Thread.Sleep(5000);
            }
        }
        catch (OperationCanceledException ex)
        {
            // Been cancelled.
            foreach (Session session in opcClients)
            {
                session.Close();
                session.Dispose();
            }

            if (ex.CancellationToken.Equals(GlobalCancel))
            {
                // Global Cancel
                return;
            }
            
            // Reset
            FileSystemReloadCancel = new CancellationTokenSource();

            // restart
            // artificial 1s delay
            await Task.Delay(1000);
            await OpcuaSubscribeStart();
        }
    }
}