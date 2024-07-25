#nullable disable
using System.Text.Json;
using Aderis.OpcuaInjection.Models;
using Opc.Ua;
using Opc.Ua.Client;
using Npgsql;
using System.Data;
using System.Text.Json.Serialization;

namespace Aderis.OpcuaInjection.Helpers;

public class OpcuaSubscribe
{
    private static FileSystemWatcher watcher = new();
    private static CancellationTokenSource FileSystemReloadCancel = new();
    private static CancellationToken GlobalCancel = new();
    private static string ConnectionString = LoadConnectionString();
    private static Dictionary<string, OpcClientSubscribeConfig> connectionInfo = new(); 
    private static Dictionary<string, Session> opcClientsByUrl = new();
    private static Dictionary<string, Dictionary<string, List<OpcTemplatePointConfiguration>>> OpcTemplates = LoadOpcTemplates();
    private static OpcClientConfig OpcClientConfig = OpcuaHelperFunctions.LoadClientConfig();
    private static Dictionary<string, List<JSONGenericDevice>> SiteDevices = LoadSiteDevices();

    /// <summary>
    /// Represents the online/offline status tag for device
    /// </summary>
    /// <remarks>
    /// Each device has one row with the myPV_online tag.
    /// Each device can have 2 or more rows depending on the number of data streams it provides.
    /// The value of myPV_online is 1 if the device has been updated in the last 60 seconds, otherwise 0.
    /// </remarks>
    private static OpcTemplatePointConfigurationBase myPVOnlineTag = new OpcTemplatePointConfigurationBase()
    {
        Unit = "bool",
        MeasureName = "myPV_online",
        TagName = "myPV_online"
    };

    private static Dictionary<string, System.Timers.Timer> opcTimeoutTimers = new();
    private static readonly TimeSpan OpcTimeoutPeriod = TimeSpan.FromMinutes(3);  //time period allowed of 0 SubscribedItemChange events before assuming server connection down (1 min works fine too)

    //seperate cancellation tokens for each opc server.
    private static Dictionary<string, CancellationTokenSource> serverCancellationTokens = new();


    private class OpcClientSubscribeConfig
    {
        public required int TimeoutMs { get; set; }
        public required List<OPCMonitoredItem> points { get; set; }
    }

    public static System.Timers.Timer InitializeOpcTimeoutTimer(string serverUrl)
    {
        Console.WriteLine($"Initializing OPC UA server timeout timer for {serverUrl}...");
        var timer = new System.Timers.Timer(OpcTimeoutPeriod.TotalMilliseconds);
        timer.Elapsed += (sender, e) => OnOpcTimeout(sender, e, serverUrl);
        timer.AutoReset = false; // Once elapsed, do not restart
        timer.Start();
        return timer;
    }

    private static bool AreAllServersDown()
    {
        return serverCancellationTokens.All(cts => cts.Value.Token.IsCancellationRequested);
    }

    private static async void OnOpcTimeout(object sender, System.Timers.ElapsedEventArgs e, string serverUrl)
    {
        Console.WriteLine($"TIMER ELAPSED / OnOpcTimeout called for {serverUrl}.");

        // Dispose of the timer that just elapsed
        OpcuaHelperFunctions.DisposeTimer(sender);
        opcTimeoutTimers.Remove(serverUrl);

        //step 1 - flag server as down
        if (serverCancellationTokens.TryGetValue(serverUrl, out var cts))
        {
            cts.Cancel();
        }
        //step 2 - check if all servers are down
        if (AreAllServersDown())
        {
            Console.WriteLine("All OPC UA servers are down. Will continue individual server reconnection attempts.");
            //currently ignore and just try reconnect on individual server basis when they get back online)
            //FileSystemReloadCancel.Cancel();
            //return;
        }
        //step 3 - ensure server session stopped and rows marked offline
        await StopServerActivities(serverUrl);

        //step 4 - begin checking for when server back up and then try reconnecting
        await MonitorAndRestartServer(serverUrl);
    }
    // private static int tcpCheckCount = 0;
    private static async Task MonitorAndRestartServer(string serverUrl)
    {
        //Console.WriteLine($"In MonitorAndRestartServer for {serverUrl}");
        int delayMilliseconds = 1000;  // Start with a 1-second delay between OPC UA connection attempts

        //loop contiously but allow for outside cancellation (currently only gets cancelled tokens when server is up and then goes down)
        while (!serverCancellationTokens[serverUrl].IsCancellationRequested)
        {
            // Check TCP connectivity first
            // tcpCheckCount++;
            // if (tcpCheckCount  % 1 == 0) //increase to reduce amount of prints while debugging
            // {
            //     Console.WriteLine($"Checking TCP connectivity for {serverUrl}... Attempt: {tcpCheckCount}");
            // }
            bool serverAvailable = await OpcuaHelperFunctions.IsServerAvailable(serverUrl);

            if (serverAvailable)
            {
                Console.WriteLine($"TCP connection established for {serverUrl}. Starting OPC UA connection attempts.");
                int opcConnectionAttempts = 0;

                // Attempt OPC UA connections with exponential backoff
                while (serverAvailable && !serverCancellationTokens[serverUrl].IsCancellationRequested)
                {
                    try
                    {
                        await StartServerActivities(serverUrl);
                        Console.WriteLine($"OPC UA server activities successfully restarted for {serverUrl}.");
                        return;  // Exit if successful
                    }
                    catch (Exception ex)
                    {
                        opcConnectionAttempts++;
                        Console.WriteLine($"Error during server restart for {serverUrl}: {ex.Message}");
                        Console.WriteLine($"Retrying in {delayMilliseconds / 1000} seconds...");

                        await Task.Delay(delayMilliseconds);
                        delayMilliseconds *= 2;  // Double the delay for the next attempt

                        // Re-check TCP connectivity to ensure the server is still available
                        serverAvailable = await OpcuaHelperFunctions.IsServerAvailable(serverUrl);
                        if (!serverAvailable)
                        {
                            Console.WriteLine($"Lost TCP connectivity for {serverUrl}. Rechecking TCP...");
                            delayMilliseconds = 1000;  // Reset delay when TCP goes back up
                            break;  // Exit OPC UA retry loop and re-check TCP connectivity
                        }
                    }
                }
            }
            else
            {
                //Console.WriteLine($"Server {serverUrl} not yet available. Checking again in 30 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(30));  // Check every 30 seconds
            }
        }
    }

    private static async Task StopServerActivities(string serverUrl)
    {
        //Console.WriteLine($"In StopServerActivities for {serverUrl}");
        Session existingSession = null;

        // Close connection/session if exists
        if (opcClientsByUrl.TryGetValue(serverUrl, out existingSession))
        {
            existingSession.Close();
            existingSession.Dispose();
            Console.WriteLine($"Closed existing session for {serverUrl}.");
        }

        // Mark modvalues rows from that server as offline
        await MarkRowsAsOffline(serverUrl);

        //reset cancel token (doesn't mean its live but the cancellation is handled, allows upcoming reconnection attempts to be stopped if needed)
        serverCancellationTokens[serverUrl] = new CancellationTokenSource();
    }

    private static async Task StartServerActivities(string serverUrl){
        try
        {
            var info = connectionInfo[serverUrl];
            //var newSession = await SubscribeToOpcServer(serverUrl, info);
            await SubscribeToOpcServer(serverUrl, info, true);
            
            // Replace the old session with the new session in the dict
            //opcClients[serverUrl] = newSession; //redundant

            Console.WriteLine($"RECONNECT SUCCESS: Started OPC UA session for {serverUrl}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception occurred when trying to start server session for {serverUrl}: {ex.Message}");
            // Optionally, handle failed restart attempts (e.g., retry logic)
        }
    }

    private static async Task MarkRowsAsOffline(string serverUrl)
    {
        Console.WriteLine($"Marking rows as offline for server: {serverUrl}");
        //get each device 
        var deviceNamesFromServer = connectionInfo[serverUrl].points
                        .Select(item => item.DaqName)
                        .Distinct();
        //Console.WriteLine($"Devices from server: {string.Join(", ", deviceNamesFromServer)}");
        using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");

            foreach (var daqName in deviceNamesFromServer) //need to do in bulk, also secure lock first
            {
                ModifyMeasure(connection, myPVOnlineTag.MeasureName, daqName, 0.0, timestamp);
            }
        }
    }


    private static T DeserializeJson<T>(string filePath, int iteration = 1)
    {
        if (iteration > 5) throw new Exception("Deserialize Error!");

        var options = new JsonSerializerOptions()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        try
        {
            string rawOutput = OpcuaHelperFunctions.GetFileContentsNoLock(filePath);
            return JsonSerializer.Deserialize<T>(rawOutput, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error when attempting to get and deserialize {filePath}: {ex}");
            Thread.Sleep(500);
            return DeserializeJson<T>(filePath, iteration + 1);
        }
    }

    private static Dictionary<string, Dictionary<string, List<OpcTemplatePointConfiguration>>> LoadOpcTemplates()
    {
        // string rawTemplates = OpcuaHelperFunctions.GetFileTextLock($"{OpcuaHelperFunctions.SosConfigPrefix}/sos_templates_opcua.json");
        return DeserializeJson<Dictionary<string, Dictionary<string, List<OpcTemplatePointConfiguration>>>>($"{OpcuaHelperFunctions.SosConfigPrefix}/sos_templates_opcua.json");

    }

    private static Dictionary<string, List<JSONGenericDevice>> LoadSiteDevices()
    {
        // string rawSiteDevices = OpcuaHelperFunctions.GetFileTextLock($"{OpcuaHelperFunctions.SosConfigPrefix}/site_devices.json");
        return DeserializeJson<Dictionary<string, List<JSONGenericDevice>>>($"{OpcuaHelperFunctions.SosConfigPrefix}/site_devices.json");
    }
    private static string LoadConnectionString()
    {
        // string plantConfig = OpcuaHelperFunctions.GetFileTextLock($"{OpcuaHelperFunctions.SosConfigPrefix}/plant_config.json");
        // string plantConfig = OpcuaHelperFunctions.GetFileContentsNoLock($"{OpcuaHelperFunctions.SosConfigPrefix}/plant_config.json");
        // MODBUSDBConfig dbConfig = JsonSerializer.Deserialize<MODBUSDBConfig>(plantConfig);

        MODBUSDBConfig dbConfig = DeserializeJson<MODBUSDBConfig>($"{OpcuaHelperFunctions.SosConfigPrefix}/plant_config.json");
        return dbConfig.Connection.ToConnectionString();
    }

    static void TemplateFileChanged(object source, FileSystemEventArgs e)
    {
        Console.WriteLine($"In TemplateFileChanged for {e.Name}");

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
                // Console.WriteLine("Deserialized SiteDevices...");
                goto case "CHANGED";
            // case "plant_config.json":
            //     ConnectionString = LoadConnectionString();
            // goto case "CHANGED";
            case "CHANGED":
                // Synchronously cancel
                // Console.WriteLine("Cancelling...");
                FileSystemReloadCancel.Cancel();

                break;
        }
    }
    private static int logCounter = 0;
    private static void LogTimerStatus(string clientUrl, string action)
    {
        logCounter++;
        if (logCounter % 3000 == 0 ) //increase divisor to decrease message rate while debugging
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] Timer for {clientUrl} {action}. (Log Counter: {logCounter})");
        }
    }

    /// <summary>
    /// Handles changes in subscribed OPC items by processing the new values and updating the corresponding entries in the modvalues db table.
    /// </summary>
    /// <param name="item">The OPC monitored item that has changed.</param>
    /// <param name="e">Event arguments containing the notification details.</param>
    static void SubscribedItemChange(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        // This will be an OPCMonitoredItem
        OPCMonitoredItem opcItem = (OPCMonitoredItem)item;

        string clientUrl = opcItem.ClientUrl;

        // Reset the corresponding timer each time new data is received
        // Console.WriteLine($"Client url in object: '{clientUrl}', length: {clientUrl.Length}");
        // foreach (var key in opcTimeoutTimers.Keys)
        // {
        //     Console.WriteLine($"Client url in dict: '{key}', length: {key.Length}");
        // }

        if (opcTimeoutTimers.ContainsKey(clientUrl))
        {
            opcTimeoutTimers[clientUrl].Stop();
            opcTimeoutTimers[clientUrl].Start();
            //LogTimerStatus(clientUrl, "reset");
        }
        else
        {
            //LogTimerStatus(clientUrl, "not found");
        }

        using (var connection = new NpgsqlConnection(ConnectionString))
        {
            connection.Open();
            foreach (var value in opcItem.DequeueValues())
            {

                string timestamp = value.SourceTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
                OpcTemplatePointConfiguration config = opcItem.Config;
                if (Math.Abs((DateTime.UtcNow - value.SourceTimestamp).TotalSeconds) <= 60) //old stale data check, later has timeoutms put here
                {
                    try
                    {
                        if (StatusCode.IsGood(value.StatusCode))
                        {
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

                            ModifyMeasure(connection, config.MeasureName, opcItem.DaqName, scaledValue, timestamp);

                            // Got a new Measure, need to set myPV_online
                            ModifyMeasure(connection, myPVOnlineTag.MeasureName, opcItem.DaqName, 1.0, timestamp);
                        }
                        else
                        {
                            // Set myPV_online to false now
                            ModifyMeasure(connection, myPVOnlineTag.MeasureName, opcItem.DaqName, 0.0, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));

                            // Re-evaluate: Should we write "null" to this point? Or just leave as-is?
                            // ModifyMeasure(connection, config.MeasureName, opcItem.DaqName, null, DateTime.UtcNow);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"An Error occurred when saving device {opcItem.DaqName}, {opcItem.Config.MeasureName}: {ex}");
                    }
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

    private static void ModifyMeasure(NpgsqlConnection connection, string measureName, string daqName, object scaledValue, string timestamp)
    {
        if (scaledValue == null) scaledValue = DBNull.Value;

        string updateRowQuery = @"
                        UPDATE modvalues
                        SET 
                            tag_value=@tagValue, 
                            measure_value=@measureValue,
                            last_updated=@lastUpdated
                        WHERE device = @device AND measure_name = @measure";

        using (var updateCommand = new NpgsqlCommand(updateRowQuery, connection))
        {
            updateCommand.Parameters.AddWithValue("device", daqName);
            updateCommand.Parameters.AddWithValue("measure", measureName);
            updateCommand.Parameters.AddWithValue("tagValue", scaledValue);
            updateCommand.Parameters.AddWithValue("measureValue", scaledValue);
            updateCommand.Parameters.AddWithValue("lastUpdated", timestamp);

            int affectedRows = updateCommand.ExecuteNonQuery();
        }
    }

    private static void CheckAndAddMeasure(NpgsqlConnection connection, string deviceType, JSONGenericDevice device, OpcTemplatePointConfigurationBase point)
    {
        try
        {
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
                bool measuresRowExists = false;
                using (var reader = command.ExecuteReader())
                {
                    measuresRowExists = reader.HasRows;
                }

                if (!measuresRowExists)
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

    private static async Task SetAllMyPVOnlineFalse(NpgsqlConnection connection)
    {
        string formattedUtcNow = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
        
        string updateQuery = @"
            UPDATE modvalues
            SET 
                tag_value = @tagValue,
                measure_value = @measureValue,
                last_updated = @lastUpdated
            WHERE tag_name = 'myPV_online';
        ";

        using (var updateCommand = new NpgsqlCommand(updateQuery, connection))
        {
            updateCommand.Parameters.AddWithValue("tagValue", 0.0);
            updateCommand.Parameters.AddWithValue("measureValue", 0.0);
            updateCommand.Parameters.AddWithValue("lastUpdated", formattedUtcNow);

            int affectedRows = await updateCommand.ExecuteNonQueryAsync();
            Console.WriteLine($"On startup: {affectedRows} 'myPV_online' tags have been reset to 0 and timestamp updated.");
        }
    }


    private static async Task<Session> SubscribeToOpcServer(string serverUrl, OpcClientSubscribeConfig config, bool isResubscribe = false)
    {
        //Console.WriteLine($"In SubscribeToOpcServer for {serverUrl}");
        try
        {
            //within get session by url it tries 5 times to get a session
            Session session = await OpcuaHelperFunctions.GetSessionByUrl(serverUrl);

            //add new session to opcClient dict, and make corresponding cancel token
            opcClientsByUrl[serverUrl] = session; 
            serverCancellationTokens[serverUrl] = new CancellationTokenSource();

            var subscription = new OPCSubscription()
            {
                DisplayName = $"Subscription to {serverUrl}",
                PublishingEnabled = true,
                PublishingInterval = 1000,
                LifetimeCount = 0,
                MinLifetimeInterval = 120_000,
                TimeoutMs = config.TimeoutMs
            };

            var points = new List<OPCMonitoredItem>();

            //if resubscribe, need to make new instances of monitor items for points to get updates/call SubcribedItemChange 
            if(isResubscribe)
            {
                foreach (var oldPoint in config.points)
                {
                    OPCMonitoredItem oPCMonitoredItem = new OPCMonitoredItem()
                    {
                        DaqName = oldPoint.DaqName,
                        Config = oldPoint.Config,
                        ClientUrl = oldPoint.ClientUrl,
                        StartNodeId = oldPoint.StartNodeId,
                        AttributeId = oldPoint.AttributeId,
                        DisplayName = oldPoint.DisplayName,
                        SamplingInterval = oldPoint.SamplingInterval,
                        QueueSize = oldPoint.QueueSize,
                        DiscardOldest = oldPoint.DiscardOldest,
                        MonitoringMode = oldPoint.MonitoringMode,
                        Filter = oldPoint.Filter
                    };
                    oPCMonitoredItem.Notification += SubscribedItemChange;
                    points.Add(oPCMonitoredItem);
                    //here we could update connectionInfo[serverUrl].points to be current,
                    // but at this stage in code (after startup) its only used to provide the monitor item point info when reinit them on resub
                }
            }
            else
            {
                points = config.points;
            }

            session.AddSubscription(subscription);
            subscription.Create();
            subscription.AddItems(points); //subscribe to each data stream of each device obtained from site_devices.json
            subscription.ApplyChanges();

            //initialize/start an opc timer right before subscriptions for that server start
            var timer = InitializeOpcTimeoutTimer(serverUrl);
            Console.WriteLine($"Timer for {serverUrl} started.");
            opcTimeoutTimers[serverUrl] = timer;

            return session;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to subscribe to OPC UA server {serverUrl}: {ex.Message}");
            return null;
        }
    }


    private static async Task OpcuaSubscribeStart()
    {
        Console.WriteLine("In OpcuaSubscribeStart");
        //Dictionary<string, OpcClientSubscribeConfig> connectionInfo = new(); // made class scoped

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
                connectionInfo.Add(opcClientConnection.Url, new OpcClientSubscribeConfig()
                {
                    TimeoutMs = opcClientConnection.TimeoutMs,
                    points = []
                });

                connectionUrls.Add(opcClientConnection.ConnectionName, opcClientConnection.Url);
            }

            using (var connection = new NpgsqlConnection(ConnectionString))
            {
                // Open the connection
                connection.Open();
                foreach ((string deviceType, List<JSONGenericDevice> deviceList) in SiteDevices) //ABQ they come in as lists? what is the higher order grouping? anyway of knowing if a list will def not have opcua devices?
                {
                    foreach (JSONGenericDevice device in deviceList)
                    {
                        if (device.Network.Params.Protocol == "OPCUA")
                        {
                            try
                            {
                                List<OpcTemplatePointConfiguration> points = OpcTemplates[device.DeviceType][device.DaqTemplate];

                                // This tag includes add'l AutoScaling that is not require'd. Consider a different structure

                                //add the online/offline status row for the current device
                                CheckAndAddMeasure(connection, deviceType, device, myPVOnlineTag);

                                //add a row in modvalues for each datapoint the current device provides
                                //For example adds two rows for same device "weather_station", one for irradiance and one for tilt-angle
                                foreach (OpcTemplatePointConfiguration point in points)
                                {
                                    CheckAndAddMeasure(connection, deviceType, device, point);

                                    // Define the data change filter
                                    var dataChangeFilter = new DataChangeFilter
                                    {
                                        Trigger = DataChangeTrigger.StatusValueTimestamp,
                                        DeadbandType = (uint)DeadbandType.None
                                    };
                                    string url = connectionUrls[device.Network.Params.Server];
                                    //construct monitored OPC item based on template in site devices
                                    OPCMonitoredItem oPCMonitoredItem = new()
                                    {
                                        DaqName = device.DaqName,
                                        Config = point,
                                        ClientUrl = url,
                                        StartNodeId = $"{device.Network.Params.PointNodeId}/{device.Network.Params.Prefix}{point.TagName}", //NOTE node id is the prefix + tagname
                                        AttributeId = Attributes.Value,
                                        DisplayName = point.TagName,
                                        SamplingInterval = 5000,
                                        QueueSize = 10,
                                        DiscardOldest = true,
                                        MonitoringMode = MonitoringMode.Reporting,
                                        Filter = dataChangeFilter
                                    };

                                    oPCMonitoredItem.Notification += SubscribedItemChange;

                                    connectionInfo[url].points.Add(oPCMonitoredItem);
                                }

                                string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");

                                // Set online to false until we get an update
                                //(now handled below in setFalseAllMyPVOnlineinneficient)
                                //ModifyMeasure(connection, myPVOnlineTag.MeasureName, device.DaqName, 0.0, timestamp); 
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Unexpected Exception Occurred for OPCUA Device {device.DaqName}: {ex.Message}");
                                continue;
                            }
                        }
                    }
                }
                //new approach marks offline old rows in one query, also works for devices perhaps not in the new config and still set online from last time but actually no longer online
                await SetAllMyPVOnlineFalse(connection);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected Exception Occurred when reading config files: {ex.Message}");
        }


        //List<Session> opcClients = new(); // made class scoped
        foreach ((string serverUrl, OpcClientSubscribeConfig info) in connectionInfo)
        {
            try
            {
                await SubscribeToOpcServer(serverUrl, info);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to subscribe to server {serverUrl}: {ex.Message}");
            }
        }

        try
        {
            /* --- Main Loop ----
            * Every 60s updates timestamp to current time for each device marked online
            * (updates the timestamp for each of the devices data points/rows)
            * 
            * also checks if global cancellation or file system reload cancellation has been requested and exits/begins those processes
            *
            * Mainly necessary due to fact that OPC UA is designed to not update points that have not changed
            * otherwise SubscribedItemChange method called on monitored opcua items would do all the work
            */

            int i = 0;
            while (true)
            {
                // Every 12th iteration of 5s (60s)
                if (i == 12)
                {
                    using (var connection = new NpgsqlConnection(ConnectionString))
                    {
                        connection.Open();

                        using (var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                        {
                            try
                            {
                                // Query the rows with measure_name == "myPV_online" and measure_value == 1
                                // DISTINCT: removes multiple duplicate rows from a result set
                                string selectDevicesQuery = @"
                                    SELECT DISTINCT device 
                                    FROM modvalues 
                                    WHERE measure_name = 'myPV_online' AND measure_value = 1";

                                var devicesToLock = new List<string>();

                                using (var selectCommand = new NpgsqlCommand(selectDevicesQuery, connection, transaction))
                                using (var reader = selectCommand.ExecuteReader())
                                {
                                    while (reader.Read())
                                    {
                                        devicesToLock.Add(reader.GetString(0));
                                    }
                                }

                                if (devicesToLock.Count > 0)
                                {
                                    // Lock rows with the devices found 
                                    //(FOR UPDATE is the locking clause, it blocks potential conflicting concurrent transactions)
                                    string lockQuery = @"
                                        SELECT * 
                                        FROM modvalues 
                                        WHERE device = ANY(@devices) 
                                        FOR UPDATE";

                                    using (var lockCommand = new NpgsqlCommand(lockQuery, connection, transaction))
                                    {
                                        lockCommand.Parameters.AddWithValue("devices", devicesToLock.ToArray());
                                        lockCommand.ExecuteNonQuery();
                                    }

                                    // Update the last_updated value for the locked rows
                                    string updateQuery = @"
                                        UPDATE modvalues 
                                        SET last_updated = @currentTime 
                                        WHERE device = ANY(@devices)";

                                    using (var updateCommand = new NpgsqlCommand(updateQuery, connection, transaction))
                                    {
                                        // Use parameterized query to prevent SQL injection
                                        updateCommand.Parameters.AddWithValue("currentTime", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));
                                        updateCommand.Parameters.AddWithValue("devices", devicesToLock.ToArray());

                                        int rowsAffected = updateCommand.ExecuteNonQuery();
                                    }
                                }

                                transaction.Commit();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"An Error occurred when attempting to migrate datetimes: {ex.Message}");
                                transaction.Rollback();
                            }
                        }
                    }
                    // reset
                    i = 0;
                }

                // Evaluate If has ben cancelled.
                FileSystemReloadCancel.Token.ThrowIfCancellationRequested();

                // Global Reload, exit
                GlobalCancel.ThrowIfCancellationRequested();

                i += 1;
                Thread.Sleep(5000);
            }
        }
        catch (OperationCanceledException ex)
        {
            //change this to work by going thru opcclients by url keys and calling stop server activities 
            foreach (var timer in opcTimeoutTimers.Values)
            {
                OpcuaHelperFunctions.DisposeTimer(timer);
            }
            opcTimeoutTimers.Clear();

            // Been cancelled.
            foreach (Session session in opcClientsByUrl.Values)
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

            // Console.WriteLine("Resetting....");

            // restart
            // artificial 1s delay
            await Task.Delay(1000);
            await OpcuaSubscribeStart();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Process Crashed! {ex}");
            Console.WriteLine(ex.StackTrace);
            // any other exception
            foreach (Session session in opcClientsByUrl.Values)
            {
                session.Close();
                session.Dispose();
            }

            return;
        }
    }
}