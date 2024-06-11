using System;
using System.Text;
using System.Threading;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

class Program
{

    private static void DFS(Session session, ReferenceDescription rd, List<ReferenceDescription> NodesToPoll, int numSpaces)
    {
        if (rd.NodeClass == NodeClass.Variable)
        {
            // Add to polling nodes
            NodesToPoll.Add(rd);
        }


        if (rd.NodeClass == NodeClass.Variable || rd.NodeClass == NodeClass.Object)
        {
            // print
            // StringBuilder sb = new StringBuilder();
            // sb.Append(new string(' ', numSpaces));
            // sb.Append(rd.DisplayName);
            // sb.Append(", ");
            // sb.Append(rd.BrowseName);
            // sb.Append(", ");
            // sb.Append(rd.NodeClass);
            // sb.Append(", ");
            // sb.Append(rd.NodeId);
            // Console.WriteLine(sb.ToString());
        }


        ReferenceDescriptionCollection nextRefs;
        byte[] nextCp;

        // Looks for next level 
        session.Browse(null, null, ExpandedNodeId.ToNodeId(rd.NodeId, session.NamespaceUris), 0u, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Variable | (uint)NodeClass.Object, out nextCp, out nextRefs);

        if (nextRefs.Count == 0) return;

        foreach (var nextRd in nextRefs)
        {
            DFS(session, nextRd, NodesToPoll, numSpaces + 2);
            
        }
        // return;
    }

    static async Task Main(string[] args)
    {
        // Define the server URL
        // string serverUrl = "opc.tcp://localhost:53530";
        string serverUrl = "opc.tcp://localhost:62541/discovery";

        // EXAMPLE is for Anonymous user, no certificate

        // Create an OPC UA application configuration
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

        // Validate the application certificate
        await config.Validate(ApplicationType.Client);

        // Discover available endpoints at the specified server URL
        // Console.WriteLine($"Discovering endpoints at {serverUrl}...");

        List<ReferenceDescription> NodesToPoll = new();

        try
        {
            // Select the endpoint with no security for simplicity
            var selectedEndpoint = CoreClientUtils.SelectEndpoint(serverUrl, useSecurity: false, discoverTimeout: 5000);

            // Output the selected endpoint details
            Console.WriteLine($"Selected Endpoint: {selectedEndpoint.EndpointUrl}");
            Console.WriteLine($"Security Mode: {selectedEndpoint.SecurityMode}");
            Console.WriteLine($"Security Policy: {selectedEndpoint.SecurityPolicyUri}");

            // Establish a session with the server
            using (var session = await Session.Create(config,
                                                      new ConfiguredEndpoint(null, selectedEndpoint, EndpointConfiguration.Create(config)),
                                                      false,
                                                      "OPC UA Client Session",
                                                      60000,
                                                      new UserIdentity(new AnonymousIdentityToken()),
                                                      null))
            {
                // session.TransferSubscriptionsOnReconnect = true;

                Console.WriteLine("Connection to the OPC UA server established successfully.");

                Console.WriteLine("Step 3 - Browse the server namespace.");
                ReferenceDescriptionCollection refs;
                Byte[] cp;
                session.Browse(null, null, ObjectIds.ObjectsFolder, 0u, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method, out cp, out refs);

                // reference to root of ObjectsFolder
                // session.Browse(null, null, ObjectIds.ObjectsFolder, 0u, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Variable | (uint)NodeClass.Object, out cp, out refs);
                Console.WriteLine("DisplayName: BrowseName, NodeClass");

                // TODO: evaluate searching algorithms for trees, need to pick NodeClass: Variable (not VariableType)

                // foreach (var rd in refs)
                // {
                //     DFS(session, rd, NodesToPoll, 0);
                // }

                Console.WriteLine("########################");

                static void OnMonitoredItemNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
                {
                    foreach (var value in item.DequeueValues())
                    {
                        Console.WriteLine($"{item.DisplayName} Value: {value.Value}, Status: {value.StatusCode}, Timestamp: {value.SourceTimestamp}");
                    }
                }

                // List<NodeId> RawNodes = NodesToPoll.Select(n => (NodeId)n.NodeId).ToList();
                // while (true)
                // {

                //     // Call session.ReadValues to read the values
                //     DataValueCollection values;
                //     IList<ServiceResult> errors;
                //     session.ReadValues(RawNodes, out values, out errors);

                //     foreach (DataValue dataValue in values)
                //     {
                //         Console.WriteLine($"Value: {dataValue.Value}");
                //     }

                //     Thread.Sleep(2000);
                // }

                var subscription = new Subscription()
                {
                    DisplayName = "Console ReferenceClient Subscription",
                    PublishingEnabled = true,
                    PublishingInterval = 1000,
                    LifetimeCount = 0,
                    MinLifetimeInterval = 120_000,
                };

                session.AddSubscription(subscription);
                subscription.Create();

                // BEGIN Custom
                var monitoredItem = new MonitoredItem(subscription.DefaultItem)
                {
                    StartNodeId = "ns=2;s=[default]/Inverter_Tags/PCS_1_1_1/000 - Raw Tags/ACTIVE_POWER",
                    AttributeId = Attributes.Value,
                    DisplayName = "power_true_kw",
                    SamplingInterval = 1000, // ms
                    QueueSize = 10,
                    DiscardOldest = true
                };

                monitoredItem.Notification += OnMonitoredItemNotification;
                subscription.AddItem(monitoredItem);
                subscription.ApplyChanges();

                var monitoredItem2 = new MonitoredItem(subscription.DefaultItem)
                {
                    StartNodeId = "ns=2;s=[default]/Inverter_Tags/PCS_1_1_1/ACTIVE_POWER",
                    AttributeId = Attributes.Value,
                    DisplayName = "power_true_kw_1",
                    SamplingInterval = 1000, // ms
                    QueueSize = 10,
                    DiscardOldest = true
                };

                monitoredItem2.Notification += OnMonitoredItemNotification;
                subscription.AddItem(monitoredItem);
                subscription.ApplyChanges();

                // END Custom

                // foreach (var node in NodesToPoll)
                // {
                //     var monitoredItem = new MonitoredItem(subscription.DefaultItem)
                //     {
                //         StartNodeId = (NodeId)node.NodeId,
                //         AttributeId = Attributes.Value,
                //         DisplayName = node.DisplayName.Text,
                //         SamplingInterval = 1000, // ms
                //         QueueSize = 10,
                //         DiscardOldest = true
                //     };

                //     monitoredItem.Notification += OnMonitoredItemNotification;
                //     subscription.AddItem(monitoredItem);
                //     subscription.ApplyChanges();
                // }

                // wait for timeout or Ctrl-C
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }
        catch (Exception ex)
        {
            // Handle exceptions and print the error
            Console.WriteLine("Failed to connect to the OPC UA server.");
            Console.WriteLine($"Error: {ex.Message}");
            if (ex is ServiceResultException sre)
            {
                Console.WriteLine($"Status Code: {sre.StatusCode}");
            }
        }
    }
}
