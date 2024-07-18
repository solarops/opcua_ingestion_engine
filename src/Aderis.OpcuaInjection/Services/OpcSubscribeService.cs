using Aderis.OpcuaInjection.Helpers;
using Aderis.OpcuaInjection.Interfaces;
using Aderis.OpcuaInjection.Models;

using System.Text.Json;
using Opc.Ua;
using Opc.Ua.Client;
using System.Text.Json.Serialization;
using Npgsql;
using System.Data;

namespace Aderis.OpcuaInjection.Services;

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
    private static OpcTemplatePointConfigurationBase myPVOnlineTag = new OpcTemplatePointConfigurationBase()
    {
        Unit = "bool",
        MeasureName = "myPV_online",
        TagName = "myPV_online"
    };

    public OpcSubscribeService(IOpcHelperService opcHelperService)
    {
        _opcHelperService = opcHelperService;
        _opcTemplates = LoadOpcTemplates();
        _siteDevices = LoadSiteDevices();
        _dbConnectionString = LoadConnectionString();
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
        Dictionary<string, OpcClientSubscribeConfig> connectionInfo = new();

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
                if (!string.IsNullOrEmpty(opcClientConnection.UserName)) {
                    var pw = _opcHelperService.DecryptPassword(opcClientConnection.EncryptedPassword);
                    // Console.WriteLine($"Password: {pw}");
                }

                connectionInfo.Add(opcClientConnection.Url, new OpcClientSubscribeConfig()
                {
                    UserIdentity = string.IsNullOrEmpty(opcClientConnection.UserName)
                        ? new UserIdentity(new AnonymousIdentityToken())
                        : new UserIdentity(opcClientConnection.UserName, _opcHelperService.DecryptPassword(opcClientConnection.EncryptedPassword)),
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

                                CheckAndAddMeasure(connection, deviceType, device, myPVOnlineTag);

                                foreach (OpcTemplatePointConfiguration point in points)
                                {
                                    CheckAndAddMeasure(connection, deviceType, device, point);

                                    // Define the data change filter
                                    var dataChangeFilter = new DataChangeFilter
                                    {
                                        Trigger = DataChangeTrigger.StatusValueTimestamp,
                                        DeadbandType = (uint)DeadbandType.None
                                    };

                                    OPCMonitoredItem oPCMonitoredItem = new()
                                    {
                                        DaqName = device.DaqName,
                                        Config = point,
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

                                    string url = connectionUrls[device.Network.Params.Server];
                                    connectionInfo[url].points.Add(oPCMonitoredItem);
                                }

                                string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");

                                // Set online to false until we get an update
                                ModifyMeasure(connection, myPVOnlineTag.MeasureName, device.DaqName, 0.0, timestamp);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Unexpected Exception Occurred for OPCUA Device {device.DaqName}: {ex.Message}");
                                continue;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected Exception Occurred when reading config files: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }


        List<Session> opcClients = new();
        foreach ((string serverUrl, OpcClientSubscribeConfig info) in connectionInfo)
        {
            try
            {
                var points = info.points;

                Session session = await OpcuaHelperFunctions.GetSessionByUrl(serverUrl, info.UserIdentity);

                opcClients.Add(session);

                var subscription = new OPCSubscription()
                {
                    DisplayName = $"Subscription to {serverUrl}",
                    PublishingEnabled = true,
                    PublishingInterval = 1000,
                    LifetimeCount = 0,
                    MinLifetimeInterval = 120_000,
                    TimeoutMs = info.TimeoutMs
                };

                session.AddSubscription(subscription);
                subscription.Create();
                subscription.AddItems(points);
                subscription.ApplyChanges();

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                continue;
            }

        }

        try
        {
            int i = 0;
            while (true)
            {
                // Every 12th iteration of 5s (60s)
                if (i == 12)
                {
                    using (var connection = new NpgsqlConnection(_dbConnectionString))
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
                _fileSystemReloadCancel.Token.ThrowIfCancellationRequested();

                // Global Reload, exit
                _globalCancel.ThrowIfCancellationRequested();

                i += 1;
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
            // any other exception
            foreach (Session session in opcClients)
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
        return dbConfig.Connection.ToString();
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

        using (var connection = new NpgsqlConnection(_dbConnectionString))
        {
            connection.Open();
            foreach (var value in opcItem.DequeueValues())
            {

                string timestamp = value.SourceTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
                OpcTemplatePointConfiguration config = opcItem.Config;
                if (Math.Abs((DateTime.UtcNow - value.SourceTimestamp).TotalSeconds) <= 60)
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

    private void ModifyMeasure(NpgsqlConnection connection, string measureName, string daqName, object scaledValue, string timestamp)
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

}
