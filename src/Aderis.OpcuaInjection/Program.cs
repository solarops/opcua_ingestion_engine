using System;
using System.Threading.Tasks;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

class Program
{
    static async Task Main(string[] args)
    {
        // Define the server URL
        string serverUrl = "opc.tcp://localhost:53530";

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
                Console.WriteLine("Connection to the OPC UA server established successfully.");

                Console.WriteLine("Step 3 - Browse the server namespace.");
                ReferenceDescriptionCollection refs;
                Byte[] cp;
                // session.Browse(null, null, ObjectIds.ObjectsFolder, 0u, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method, out cp, out refs);
                session.Browse(null, null, ObjectIds.ObjectsFolder, 0u, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Variable | (uint)NodeClass.Object, out cp, out refs);
                Console.WriteLine("DisplayName: BrowseName, NodeClass");

                // TODO: evaluate searching algorithms for trees, need to pick NodeClass: Variable (not VariableType)

                foreach (var rd in refs)
                {
                    // rd = parent nodes
                    if (rd.NodeClass == NodeClass.Variable)
                    {
                        ExpandedNodeId expandedNodeId = rd.NodeId;
                        // Can cast ExpandedNodeId to NodeId
                        NodesToPoll.Add(rd);
                    }


                    // Console.WriteLine("{0}: {1}, {2}, {3}", rd.DisplayName, rd.BrowseName, rd.NodeClass, rd.NodeId);
                    ReferenceDescriptionCollection nextRefs;
                    byte[] nextCp;
                    // session.Browse(null, null, ExpandedNodeId.ToNodeId(rd.NodeId, session.NamespaceUris), 0u, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method, out nextCp, out nextRefs);

                    // Looks for second level 
                    session.Browse(null, null, ExpandedNodeId.ToNodeId(rd.NodeId, session.NamespaceUris), 0u, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Variable | (uint)NodeClass.Object, out nextCp, out nextRefs);
                    foreach (var nextRd in nextRefs)
                    {
                        // nextRd = child nodes
                        if (nextRd.NodeClass == NodeClass.Variable)
                        {
                            ExpandedNodeId expandedNodeId = nextRd.NodeId;
                            // Can cast ExpandedNodeId to NodeId
                            NodesToPoll.Add(nextRd);
                        }

                        // child nodes
                        // Console.WriteLine("+ {0}: {1}, {2}: {3}", nextRd.DisplayName, nextRd.BrowseName, nextRd.NodeClass, nextRd.NodeId);
                    }
                }

                Console.WriteLine("########################");

                static void OnMonitoredItemNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
                {
                    foreach (var value in item.DequeueValues())
                    {
                        Console.WriteLine($"Value: {value.Value}, Status: {value.StatusCode}, Timestamp: {value.SourceTimestamp}");
                    }
                }

                

                // var subscription = new Subscription()
                // {
                //     PublishingInterval = 1000, // in milliseconds
                //     KeepAliveCount = 10,
                //     LifetimeCount = 1000,
                //     MaxNotificationsPerPublish = 1000,
                //     Priority = 0
                // };

                // session.AddSubscription(subscription);
                // subscription.Create();

                // foreach (var node in NodesToPoll)
                // {
                //     // Console.WriteLine($"Node Id: {node}");
                //     // DataValue value = session.ReadValue(node);
                //     // Console.WriteLine($"Node Value: {value.WrappedValue}");
                //     var monitoredItem = new MonitoredItem(subscription.DefaultItem)
                //     {
                //         StartNodeId = (NodeId)node.NodeId,
                //         AttributeId = Attributes.Value,
                //         DisplayName = node.DisplayName.Text,
                //         SamplingInterval = 1000, // ms
                //         QueueSize = 10,
                //         DiscardOldest = true
                //     };

                //     monitoredItem.Notification += (item, args) => OnMonitoredItemNotification(item, args);
                //     subscription.AddItem(monitoredItem);
                //     subscription.ApplyChanges();
                // }
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
