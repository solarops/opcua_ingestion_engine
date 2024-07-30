using Aderis.OpcuaInjection.Helpers;
using Aderis.OpcuaInjection.Interfaces;
using Aderis.OpcuaInjection.Models;

using System.Text.Json;
using Opc.Ua;
using Opc.Ua.Client;
using System.Text.Json.Serialization;
using Npgsql;
using System.Data;
// using ConcurrentCollections;

namespace Aderis.OpcuaInjection.Services;

/*
    Consider implementing a queue (Kafka?) where the SubscribedItemChange and TimestampUpdate both push
    needed changes and a timestamp to, and the queue reconciles which value gets set
*/

public class OpcSubscribeService : BackgroundService, IOpcSubscribeService
{
    // Connection Agnostic State
    private readonly IOpcHelperService _opcHelperService;
    private FileSystemWatcher _watcher = new();
    private CancellationTokenSource _fileSystemReloadCancel = new();
    private CancellationToken _globalCancel = new();
    private Dictionary<string, Dictionary<string, List<OpcTemplatePointConfiguration>>> _opcTemplates;
    private Dictionary<string, List<JSONGenericDevice>> _siteDevices;
    private string _dbConnectionString;
    
    private static Dictionary<string, OpcClientSubscribeConfig> _connectionInfo = new(); 
    private static Dictionary<string, Session> _opcClientsByUrl = new();

    private static Dictionary<string, System.Timers.Timer> _opcTimeoutTimers = new();
    private static readonly TimeSpan _opcTimeoutPeriod = TimeSpan.FromMinutes(3);  //time period allowed of 0 SubscribedItemChange events before assuming server connection down (1 min works fine too)
    private static Dictionary<string, CancellationTokenSource> _serverCancellationTokens = new(); //seperate cancellation tokens for each opc server.


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

    // private ConcurrentHashSet<string> _offlineSet;
    // private ConcurrentHashSet<string> _onlineSet;

    public OpcSubscribeService(IOpcHelperService opcHelperService)
    {
        _opcHelperService = opcHelperService;
        _opcTemplates = LoadOpcTemplates();
        _siteDevices = LoadSiteDevices();
        _dbConnectionString = LoadConnectionString();
        // _offlineSet = new();
        // _onlineSet = new();
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Start(stoppingToken);
    }

    // public Task StartSubscribe(CancellationToken stoppingToken)
    // {
    //     return ExecuteAsync(stoppingToken);
    // }

    public void ReloadPolling()
    {
        _fileSystemReloadCancel.Cancel();
    }

    private async Task Start(CancellationToken GlobalStop)
    {
        // Register Passed 
        _globalCancel = GlobalStop;

        // Set the directory to monitor
        _watcher.Path = OpcuaHelperFunctions.SosConfigPrefix;

        // Watch for changes in LastWrite times, and the renaming of files or directories
        _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.DirectoryName;

        // Only watch json files (you can modify this to watch other types of files)
        _watcher.Filter = "*.json";

        // Add event handlers for each type of file change event
        _watcher.Changed += TemplateFileChanged;

        // Begin watching
        _watcher.EnableRaisingEvents = true;

        await OpcuaSubscribeStart();
    }

    private class OpcClientSubscribeConfig
    {
        public required UserIdentity UserIdentity { get; set; }
        public required int TimeoutMs { get; set; }
        public required List<OPCMonitoredItem> points { get; set; }
    }

    private async Task OpcuaSubscribeStart()
    {
        //Dictionary<string, OpcClientSubscribeConfig> connectionInfo = new(); // made class scoped
        var _opcDevices = new List<string>();

        // Get contents of the following: sos_templates_opcua, site_devices, plant_config (for db connection string), opcua_client_config.json
        try
        {
            // Check for existing table
            using (var connection = new NpgsqlConnection(_dbConnectionString))
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
            var opcClientConnections = await _opcHelperService.LoadClientConfig();

            foreach (var opcClientConnection in opcClientConnections)
            {
                var userIdentity = new UserIdentity(new AnonymousIdentityToken());

                if (!string.IsNullOrEmpty(opcClientConnection.UserName))
                {
                    userIdentity = new UserIdentity(opcClientConnection.UserName.Trim(), _opcHelperService.DecryptPassword(opcClientConnection.EncryptedPassword).Trim());
                }

                _connectionInfo.Add(opcClientConnection.Url, new OpcClientSubscribeConfig()
                {
                    UserIdentity = userIdentity,
                    TimeoutMs = opcClientConnection.TimeoutMs,
                    points = []
                });

                connectionUrls.Add(opcClientConnection.ConnectionName, opcClientConnection.Url);
            }

            using (var connection = new NpgsqlConnection(_dbConnectionString))
            {
                // Open the connection
                connection.Open();
                foreach ((string deviceType, List<JSONGenericDevice> deviceList) in _siteDevices)
                {
                    foreach (JSONGenericDevice device in deviceList)
                    {
                        if (device.Network.Params.Protocol == "OPCUA")
                        {
                            try
                            {
                                List<OpcTemplatePointConfiguration> points = _opcTemplates[device.DeviceType][device.DaqTemplate];

                                // This tag includes add'l AutoScaling that is not require'd. Consider a different structure

                                //add the online/offline status row for the current device
                                CheckAndAddMeasure(connection, deviceType, device, myPVOnlineTag);

                                _opcDevices.Add(device.DaqName);

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
                                        StartNodeId = $"{device.Network.Params.PointNodeId}/{device.Network.Params.Prefix}{point.TagName}",
                                        AttributeId = Attributes.Value,
                                        DisplayName = point.TagName,
                                        SamplingInterval = 5000,
                                        QueueSize = 10,
                                        DiscardOldest = true,
                                        MonitoringMode = MonitoringMode.Reporting,
                                        Filter = dataChangeFilter
                                    };

                                    oPCMonitoredItem.Notification += SubscribedItemChange;

                                    _connectionInfo[url].points.Add(oPCMonitoredItem);
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
            Console.WriteLine(ex.StackTrace);
        }


        //List<Session> opcClients = new(); // made class scoped dict opcClientsByUrl
        foreach ((string serverUrl, OpcClientSubscribeConfig info) in _connectionInfo)
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
            * Necessary due to fact that OPC UA only pushed data when something changes, otherwise on subscribechange would do all the work
            */

            int i = 0;
            while (true)
            {
                // Every 12th iteration of 5s (60s)
                if (i == 12)
                {
                    using (var connection = new NpgsqlConnection(_dbConnectionString))
                    {
                        connection.Open();
                        // Query the rows with measure_name == "myPV_online" and measure_value == 1
                        // DISTINCT: removes multiple duplicate rows from a result set
                        string selectDevicesQuery = @"
                            SELECT DISTINCT device 
                            FROM modvalues 
                            WHERE device = ANY(@devices)
                                AND measure_name = 'myPV_online'
                                AND measure_value = 1";

                        var devicesToLock = new List<string>();

                        using (var selectCommand = new NpgsqlCommand(selectDevicesQuery, connection))
                        {
                            selectCommand.Parameters.AddWithValue("devices", _opcDevices);
                            using (var reader = selectCommand.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    devicesToLock.Add(reader.GetString(0));
                                }
                            }
                        }

                        if (devicesToLock.Count > 0)
                        {
                            string currentUtcTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
                            using (var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                            {
                                try
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

                                    // Update last_updated value for measures then myPV_online.
                                    
                                    string updateOnlineQuery = @"
                                        UPDATE modvalues 
                                        SET last_updated = @currentTime 
                                        WHERE device = ANY(@devices)";

                                    var updateOnline = 0;
                                    using (var updateCommand = new NpgsqlCommand(updateOnlineQuery, connection, transaction))
                                    {
                                        // Use parameterized query to prevent SQL injection
                                        updateCommand.Parameters.AddWithValue("currentTime", currentUtcTime);
                                        updateCommand.Parameters.AddWithValue("devices", devicesToLock.ToArray());
                                        updateOnline = updateCommand.ExecuteNonQuery();
                                    }
                                   

                                    if (updateOnline > 0)
                                    {
                                        transaction.Commit();
                                    }
                                }
                                catch (NpgsqlException ex)
                                {
                                    Console.WriteLine($"{currentUtcTime}: An Error occurred when attempting to migrate datetimes: {ex.Message}");
                                    Console.Error.WriteLine($"{currentUtcTime}: An Error occurred when attempting to migrate datetimes: {ex.Message}");

                                    transaction.Rollback();
                                }
                            }
                        }
                    }
                    // reset
                    i = 0;
                }

                // Evaluate If has ben cancelled.
                _fileSystemReloadCancel.Token.ThrowIfCancellationRequested();

                // Global Reload, exit
                _globalCancel.ThrowIfCancellationRequested();

                i += 1;
                Thread.Sleep(5000);
            }
        }
        catch (OperationCanceledException ex)
        {
            //dispose all server timers
            foreach (var timer in _opcTimeoutTimers.Values)
            {
                OpcuaHelperFunctions.DisposeTimer(timer);
            }
            _opcTimeoutTimers.Clear();

            // Been cancelled.
            foreach (Session session in _opcClientsByUrl.Values)
            {
                session.Close();
                session.Dispose();
            }

            if (ex.CancellationToken.Equals(_globalCancel))
            {
                // Global Cancel
                return;
            }

            // Reset
            _fileSystemReloadCancel = new CancellationTokenSource();

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

            //dispose all server timers
            foreach (var timer in _opcTimeoutTimers.Values)
            {
                OpcuaHelperFunctions.DisposeTimer(timer);
            }
            _opcTimeoutTimers.Clear();

            // any other exception
            foreach (Session session in _opcClientsByUrl.Values)
            {
                session.Close();
                session.Dispose();
            }

            return;
        }
    }

    private T DeserializeJson<T>(string filePath, int iteration = 1)
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
            return JsonSerializer.Deserialize<T>(rawOutput, options) ?? throw new Exception();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error when attempting to get and deserialize {filePath}: {ex}");
            Thread.Sleep(500);
            return DeserializeJson<T>(filePath, iteration + 1);
        }
    }

    private Dictionary<string, Dictionary<string, List<OpcTemplatePointConfiguration>>> LoadOpcTemplates()
    {
        // string rawTemplates = OpcuaHelperFunctions.GetFileTextLock($"{OpcuaHelperFunctions.SosConfigPrefix}/sos_templates_opcua.json");
        return DeserializeJson<Dictionary<string, Dictionary<string, List<OpcTemplatePointConfiguration>>>>($"{OpcuaHelperFunctions.SosConfigPrefix}/sos_templates_opcua.json");

    }

    private Dictionary<string, List<JSONGenericDevice>> LoadSiteDevices()
    {
        // string rawSiteDevices = OpcuaHelperFunctions.GetFileTextLock($"{OpcuaHelperFunctions.SosConfigPrefix}/site_devices.json");
        return DeserializeJson<Dictionary<string, List<JSONGenericDevice>>>($"{OpcuaHelperFunctions.SosConfigPrefix}/site_devices.json");
    }

    private string LoadConnectionString()
    {
        DbConfig dbConfig = DeserializeJson<DbConfig>($"{OpcuaHelperFunctions.SosConfigPrefix}/plant_config.json");
        var connection = dbConfig.Connection;
        var builder = new NpgsqlConnectionStringBuilder()
        {
            Host = connection.Server,
            Port = Int32.Parse(connection.Port),
            Database = connection.Database,
            Username = connection.Username,
            Password = connection.Password,
            IncludeErrorDetail = true
        };

        return builder.ToString();
    }

    private void TemplateFileChanged(object source, FileSystemEventArgs e)
    {
        switch (e.Name)
        {
            case "sos_templates_opcua.json":
                Console.WriteLine("Templates OPCUA Changed...");
                _opcTemplates = LoadOpcTemplates();
                goto case "CHANGED";
            case "site_devices.json":
                Console.WriteLine("Devices changed...");
                _siteDevices = LoadSiteDevices();
                // Console.WriteLine("Deserialized SiteDevices...");
                goto case "CHANGED";
            case "CHANGED":
                // Synchronously cancel
                // Console.WriteLine("Cancelling...");
                ReloadPolling();

                break;
        }
    }

    private void SubscribedItemChange(MonitoredItem item, MonitoredItemNotificationEventArgs e)
    {
        // This will be an OPCMonitoredItem
        OPCMonitoredItem opcItem = (OPCMonitoredItem)item;

        string clientUrl = opcItem.ClientUrl;

        // Reset the corresponding timer each time new data is received
        if (_opcTimeoutTimers.ContainsKey(clientUrl))
        {
            _opcTimeoutTimers[clientUrl].Stop();
            _opcTimeoutTimers[clientUrl].Start();
            //LogTimerStatus(clientUrl, "reset");
        }
        else
        {
            //LogTimerStatus(clientUrl, "not found");
        }


        var subscription = (OPCSubscription)item.Subscription;

        using (var connection = new NpgsqlConnection(_dbConnectionString))
        {
            connection.Open();
            foreach (var value in opcItem.DequeueValues())
            {


                OpcTemplatePointConfiguration config = opcItem.Config;

                // ALEX: Commented-out timeout
                /*
                Explanation:
                    - If we have no way of historizing at value.SourceTimestamp, why store it as it?
                    - If the OPCUA server claims this is "good" data - record it. Store as UTC now.
                    - Maybe a configuration setting for "historian" mode when we are capable of storing by SourceTimestamp
                
                string timestamp = value.SourceTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
                if (Math.Abs((DateTime.UtcNow - value.SourceTimestamp).TotalMilliseconds) <= subscription.TimeoutMs) {}
                */

                string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
                if (Math.Abs((DateTime.UtcNow - value.SourceTimestamp).TotalMilliseconds) <= subscription.TimeoutMs)
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
                            ModifyMeasure(connection, myPVOnlineTag.MeasureName, opcItem.DaqName, 0.0, timestamp);

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

    private void ModifyMeasure(NpgsqlConnection connection, string measureName, string daqName, object scaledValue, string timestamp, int iteration = 0)
    {
        // 0, 1, 2 = Three tries
        if (iteration > 2) return;

        if (scaledValue == null) scaledValue = DBNull.Value;

        // For Acquiring Locks
        string selectForUpdateQuery = @"
            SELECT ctid
            FROM modvalues
            WHERE device = @device AND measure_name = @measure
            FOR UPDATE";

        using (var transaction = connection.BeginTransaction())
        {
            using (var selectCommand = new NpgsqlCommand(selectForUpdateQuery, connection))
            {
                selectCommand.Parameters.AddWithValue("device", daqName);
                selectCommand.Parameters.AddWithValue("measure", measureName);
                try
                {
                    selectCommand.ExecuteNonQuery();

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

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    Console.WriteLine("Real-time data update failed: " + ex.Message);
                    Thread.Sleep(100);
                    ModifyMeasure(connection, measureName, daqName, scaledValue, timestamp, iteration + 1);
                }
            }
        }
    }

    private void CheckAndAddMeasure(NpgsqlConnection connection, string deviceType, JSONGenericDevice device, OpcTemplatePointConfigurationBase point)
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

    //Used on startup to set all myPV_online tags to 0
    //more effecient than 1 at time modifyMeasure and removes outdated online devices due to bad disconnect or acuity side reconfigs
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


//-----------------REFACTORED SUBSCRIBE METHOD BELOW-----------------

    private async Task<Session> SubscribeToOpcServer(string serverUrl, OpcClientSubscribeConfig config, bool isResubscribe = false)
    {
        //Console.WriteLine($"In SubscribeToOpcServer for {serverUrl}");
        try
        {
            //within get session by url it tries 5 times to get a session
            Session session = await OpcuaHelperFunctions.GetSessionByUrl(serverUrl, config.UserIdentity);

            //add new session to opcClient dict, and make corresponding cancel token
            _opcClientsByUrl[serverUrl] = session; 
            _serverCancellationTokens[serverUrl] = new CancellationTokenSource();

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
            _opcTimeoutTimers[serverUrl] = timer;

            return session;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to subscribe to OPC UA server {serverUrl}: {ex.Message}");
            return null;
        }
    }


//-----------------OPC UA SERVER RECONNECT CODE BELOW-----------------


    public System.Timers.Timer InitializeOpcTimeoutTimer(string serverUrl)
    {
        Console.WriteLine($"Initializing OPC UA server timeout timer for {serverUrl}...");
        var timer = new System.Timers.Timer(_opcTimeoutPeriod.TotalMilliseconds);
        timer.Elapsed += (sender, e) => OnOpcTimeout(sender, e, serverUrl);
        timer.AutoReset = false; // Once elapsed, do not restart
        timer.Start();
        return timer;
    }

    private bool AreAllServersDown()
    {
        return _serverCancellationTokens.All(cts => cts.Value.Token.IsCancellationRequested);
    }

    private async void OnOpcTimeout(object sender, System.Timers.ElapsedEventArgs e, string serverUrl)
    {
        Console.WriteLine($"TIMER ELAPSED / OnOpcTimeout called for {serverUrl}.");

        // Dispose of the timer that just elapsed
        OpcuaHelperFunctions.DisposeTimer(sender);
        _opcTimeoutTimers.Remove(serverUrl);

        //step 1 - flag server as down
        if (_serverCancellationTokens.TryGetValue(serverUrl, out var cts))
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
    private async Task MonitorAndRestartServer(string serverUrl)
    {
        //Console.WriteLine($"In MonitorAndRestartServer for {serverUrl}");
        int delayMilliseconds = 1000;  // Start with a 1-second delay between OPC UA connection attempts

        //loop contiously but allow for outside cancellation (currently only gets cancelled tokens when server is up and then goes down)
        while (!_serverCancellationTokens[serverUrl].IsCancellationRequested)
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
                while (serverAvailable && !_serverCancellationTokens[serverUrl].IsCancellationRequested)
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

    private async Task StopServerActivities(string serverUrl)
    {
        //Console.WriteLine($"In StopServerActivities for {serverUrl}");
        Session existingSession = null;

        // Close connection/session if exists
        if (_opcClientsByUrl.TryGetValue(serverUrl, out existingSession))
        {
            existingSession.Close();
            existingSession.Dispose();
            Console.WriteLine($"Closed existing session for {serverUrl}.");
        }

        // Mark modvalues rows from that server as offline
        await MarkRowsAsOffline(serverUrl);

        //reset cancel token (doesn't mean its live but the cancellation is handled, allows upcoming reconnection attempts to be stopped if needed)
        _serverCancellationTokens[serverUrl] = new CancellationTokenSource();
    }

    private async Task StartServerActivities(string serverUrl){
        try
        {
            var info = _connectionInfo[serverUrl];
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

    private async Task MarkRowsAsOffline(string serverUrl)
    {
        Console.WriteLine($"Marking rows as offline for server: {serverUrl}");
        //get each device 
        var deviceNamesFromServer = _connectionInfo[serverUrl].points
                        .Select(item => item.DaqName)
                        .Distinct();
        //Console.WriteLine($"Devices from server: {string.Join(", ", deviceNamesFromServer)}");
        using (var connection = new NpgsqlConnection(_dbConnectionString))
        {
            await connection.OpenAsync();
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");

            foreach (var daqName in deviceNamesFromServer) //need to do in bulk, also secure lock first
            {
                ModifyMeasure(connection, myPVOnlineTag.MeasureName, daqName, 0.0, timestamp);
            }
        }
    }


}