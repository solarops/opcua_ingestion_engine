using System;
using System.Text;
using System.Threading;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

using System.Text.Json;
using Aderis.OpcuaInjection.Models;
using System.Diagnostics;

public class LowercaseNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name)
    {
        // Convert the property name to lowercase
        return name.ToLower();
    }
}

class Program
{
    // private static void DFS_Starter(Session session, ReferenceDescription rd)
    // {

    // }

    static void DFS_Threaded(Session session, ReferenceDescription rd, JsTreeNode currNode, List<string> exclusionFolders, int searchDepth)
    {
        Console.WriteLine($"Processing: {rd.BrowseName}");
        // Immediately look for next children

        void SessionBrowse(out ReferenceDescriptionCollection nextRefs, out byte[] nextCp)
        {
            session.Browse(
                new RequestHeader()
                {
                    TimeoutHint = 15000
                },
                null,
                ExpandedNodeId.ToNodeId(rd.NodeId, session.NamespaceUris),
                0u,
                BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences,
                true,
                (uint)NodeClass.Variable | (uint)NodeClass.Object,
                out nextCp,
                out nextRefs
            );
        }

        Thread CreateDFSThread(ReferenceDescription nextRd, JsTreeNode jsTreeNode)
        {
            Thread t1 = new Thread(() =>
            {
                DFS_Threaded(session, nextRd, jsTreeNode, exclusionFolders, searchDepth + 1);
            });

            // begin parallel execution
            t1.Start();

            return t1;
        }

        try
        {
            // Keep first 3 (arbitrary) iterations/levels opened, then close all after.
            if (searchDepth > 3)
            {
                currNode.State.Opened = false;
            }

            ReferenceDescriptionCollection nextRefs;
            byte[] nextCp;

            try
            {
                SessionBrowse(out nextRefs, out nextCp);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception Occurred when Browsing: ${ex}");
                Console.WriteLine(ex.StackTrace);
                Thread.Sleep(3000);
                SessionBrowse(out nextRefs, out nextCp);
            }

            if (nextRefs.Count == 0) return;

            List<Thread> childThreads = new();

            foreach (var nextRd in nextRefs)
            {
                string folderText = nextRd.DisplayName.Text;

                if (exclusionFolders.Contains(folderText))
                {
                    // under the child nodes of the current node. If one of its children's title is in exclusionFolders, then skip that leaf of the tree. 
                    // Continue to next child.

                    // saves call to another DFS iteration
                    continue;
                }

                // new Node
                JsTreeNode jsTreeNode = new JsTreeNode()
                {
                    Text = folderText,
                    Id = nextRd.BrowseName.ToString()
                };

                currNode.Children.Add(jsTreeNode);


                // Search for children of current Node
                // nextRd and jsTreeNode are references to the same Node

                Thread t1;

                try
                {
                    t1 = CreateDFSThread(nextRd, jsTreeNode);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception Occurred when Creating Thread: ${ex}");
                    Thread.Sleep(3000);
                    t1 = CreateDFSThread(nextRd, jsTreeNode);
                }
                
                childThreads.Add(t1);
            }

            foreach (var thread in childThreads)
            {
                thread.Join();
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!");
            // Handle exceptions and print the error

            // Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);

            Console.WriteLine(rd.ToString());
            Console.WriteLine(currNode.ToString());

            Console.WriteLine($"Number of threads: {Process.GetCurrentProcess().Threads.Count}");

            if (ex is ServiceResultException sre)
            {
                Console.WriteLine("OPC UA server error");
                Console.WriteLine($"Status Code: {sre.StatusCode}");
            }

            Console.WriteLine("#########################");

            return;
        }
    }

    private static void DFS(Session session, ReferenceDescription rd, JsTreeNode currNode, List<string> exclusionFolders, int searchDepth)
    {
        // Already Variable or Object
        // if (rd.NodeClass == NodeClass.Variable || rd.NodeClass == NodeClass.Object)

        // Keep first 3 (arbitrary) iterations/levels opened, then close all after.
        if (searchDepth > 3)
        {
            currNode.State.Opened = false;
        }

        ReferenceDescriptionCollection nextRefs;
        byte[] nextCp;

        // Immediately look for next children
        session.Browse(null, null, ExpandedNodeId.ToNodeId(rd.NodeId, session.NamespaceUris), 0u, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Variable | (uint)NodeClass.Object, out nextCp, out nextRefs);

        if (nextRefs.Count == 0) return;

        foreach (var nextRd in nextRefs)
        {
            string folderText = nextRd.DisplayName.Text;

            if (exclusionFolders.Contains(folderText))
            {
                // under the child nodes of the current node. If one of its children's title is in exclusionFolders, then skip that leaf of the tree. 
                // Continue to next child.

                // saves call to another DFS iteration
                continue;
            }

            // new Node
            JsTreeNode jsTreeNode = new JsTreeNode()
            {
                Text = folderText,
                Id = nextRd.BrowseName.ToString()
            };


            currNode.Children.Add(jsTreeNode);

            // Search for children of current Node
            // nextRd and jsTreeNode are references to the same Node
            DFS(session, nextRd, jsTreeNode, exclusionFolders, searchDepth + 1);
        }
        // return;
    }

    static async Task Main(string[] args)
    {
        // Define the server URL
        string serverUrl = "opc.tcp://localhost:53530";
        // string serverUrl = "opc.tcp://localhost:62541/discovery";

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

        List<ReferenceDescription> nodesToPoll = new();

        List<string> browseExclusionFolders = new()
        {
            "Devices",
            "Server"
        };

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

                JsTreeExport jsTreeExport = new();

                NodeId objectsFolderId = ObjectIds.ObjectsFolder;

                ReferenceDescriptionCollection refs;
                Byte[] cp;
                session.Browse(
                    new RequestHeader()
                    {
                        TimeoutHint = 15000
                    },
                    null,
                    ObjectIds.ObjectsFolder,
                    0u,
                    BrowseDirection.Forward,
                    ReferenceTypeIds.HierarchicalReferences,
                    true,
                    (uint)NodeClass.Variable | (uint)NodeClass.Object,
                    out cp,
                    out refs
                );

                // reference to root of ObjectsFolder
                // session.Browse(null, null, ObjectIds.ObjectsFolder, 0u, BrowseDirection.Forward, ReferenceTypeIds.HierarchicalReferences, true, (uint)NodeClass.Variable | (uint)NodeClass.Object, out cp, out refs);
                Console.WriteLine("DisplayName: BrowseName, NodeClass");

                // TODO: evaluate searching algorithms for trees, need to pick NodeClass: Variable (not VariableType)

                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();
                // Setup 1st level of jsTreeExport
                foreach (var rd in refs)
                {
                    string folderText = rd.DisplayName.Text;

                    if (browseExclusionFolders.Contains(folderText))
                    {
                        // under the child nodes of the current node. If one of its children's title is in exclusionFolders, then skip that leaf of the tree. 
                        // Continue to next child.

                        // saves call to another function
                        continue;
                    }

                    // Top Level Objects, immediately add to jsTreeExport.Core.Data
                    JsTreeNode jsTreeNode = new JsTreeNode()
                    {
                        Text = folderText,
                        Id = rd.BrowseName.ToString()
                    };

                    jsTreeExport.Core.Data.Add(jsTreeNode);

                    // rd and jsTreeNode are references to the same Node
                    // DFS(session, rd, jsTreeNode, browseExclusionFolders, 1);
                    DFS_Threaded(session, rd, jsTreeNode, browseExclusionFolders, 1);
                }

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

                // var subscription = new Subscription()
                // {
                //     DisplayName = "Console ReferenceClient Subscription",
                //     PublishingEnabled = true,
                //     PublishingInterval = 1000,
                //     LifetimeCount = 0,
                //     MinLifetimeInterval = 120_000,
                // };

                // session.AddSubscription(subscription);
                // subscription.Create();

                // // BEGIN Custom
                // var monitoredItem = new MonitoredItem(subscription.DefaultItem)
                // {
                //     StartNodeId = "ns=2;s=[default]/Inverter_Tags/PCS_1_1_1/000 - Raw Tags/ACTIVE_POWER",
                //     AttributeId = Attributes.Value,
                //     DisplayName = "power_true_kw",
                //     SamplingInterval = 1000, // ms
                //     QueueSize = 10,
                //     DiscardOldest = true
                // };

                // monitoredItem.Notification += OnMonitoredItemNotification;
                // subscription.AddItem(monitoredItem);
                // subscription.ApplyChanges();

                // var monitoredItem2 = new MonitoredItem(subscription.DefaultItem)
                // {
                //     StartNodeId = "ns=2;s=[default]/Inverter_Tags/PCS_1_1_1/ACTIVE_POWER",
                //     AttributeId = Attributes.Value,
                //     DisplayName = "power_true_kw_1",
                //     SamplingInterval = 1000, // ms
                //     QueueSize = 10,
                //     DiscardOldest = true
                // };

                // monitoredItem2.Notification += OnMonitoredItemNotification;
                // subscription.AddItem(monitoredItem);
                // subscription.ApplyChanges();

                // END Custom


                // BEGIN logic to write to JsTree format
                var options = new JsonSerializerOptions()
                {
                    PropertyNamingPolicy = new LowercaseNamingPolicy(),
                    WriteIndented = true
                };

                string json = JsonSerializer.Serialize(jsTreeExport, options);
                // END JsTree format

                File.WriteAllText("./nodes-threaded.json", json);
                stopwatch.Stop();
                Console.WriteLine($"Elapsed Time: {stopwatch.Elapsed.TotalMilliseconds} ms");

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
                // Console.WriteLine("Press any key to exit...");
                // Console.ReadKey();
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
