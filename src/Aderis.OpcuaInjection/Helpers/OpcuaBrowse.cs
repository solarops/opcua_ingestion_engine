
using Opc.Ua;
using Opc.Ua.Client;
using Aderis.OpcuaInjection.Models;
using System.Diagnostics;
using System.Text.Json;

namespace Aderis.OpcuaInjection.Helpers;

public class OpcuaBrowse
{
    // Initiates a Browse function, saving the output under /opt/sos-config/opcua_nodes/<connection_name>.json
    private static readonly string SosNodesPrefix = "/opt/sos-config/opcua_nodes";
    
    // Connection Specific
    private OpcClientConnection opcClientConnection;
    private CustomThreadPool customThreadPool;
    private string _connectionId;
    private CancellationToken _globalCancel;
    private void DFS_Threaded(Session session, ReferenceDescription rd, JsTreeNode currNode, List<string> exclusionFolders, int searchDepth)
    {
        // Global Cancel, begin scaffold return 
        if (_globalCancel.IsCancellationRequested) return;

        void Browse(Session session, ReferenceDescription rd, out ReferenceDescriptionCollection nextRefs, out byte[] nextCp)
        {
            session.Browse(
                            new RequestHeader()
                            {
                                TimeoutHint = 30000
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

        Console.WriteLine($"Processing: {rd.BrowseName}");
        // Immediately look for next children

        try
        {
            // Keep first 3 (arbitrary) iterations/levels opened, then close all after.
            if (searchDepth > 3)
            {
                currNode.State.Opened = false;
            }

            // Will be "Object" or "Variable"
            currNode.Data.Type = rd.NodeClass.ToString();
            currNode.Data.Points_node_id = rd.NodeId.ToString();

            ReferenceDescriptionCollection nextRefs;
            byte[] nextCp;

            try
            {
                Browse(session, rd, out nextRefs, out nextCp);
            }
            catch
            {
                // Timeout / other exception, 1 retry per node
                Thread.Sleep(2500);
                Browse(session, rd, out nextRefs, out nextCp);
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

                try
                {
                    childThreads.Add(customThreadPool.AskForThread(() => DFS_Threaded(session, nextRd, jsTreeNode, exclusionFolders, searchDepth + 1)));
                }
                catch
                {
                    // Cannot get thread, re-use current.
                    DFS_Threaded(session, nextRd, jsTreeNode, exclusionFolders, searchDepth + 1);
                }
            }

            foreach (Thread t1 in childThreads)
            {
                t1.Join();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!");
            // Handle exceptions and print the error

            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.WriteLine(rd.ToString());

            if (ex is ServiceResultException sre)
            {
                Console.WriteLine("OPC UA server error");
                Console.WriteLine($"Status Code: {sre.StatusCode}");
            }

            Console.WriteLine("#########################");

            return;
        }

        return;
    }

    public OpcuaBrowse(CancellationToken GlobalCancel, string connectionId)
    {
        _connectionId = connectionId;
        _globalCancel = GlobalCancel;
        // BEGIN: Can move this to private setter? (For encapsulation)
        OpcClientConfig clientConfig = OpcuaHelperFunctions.LoadClientConfig();
        opcClientConnection = clientConfig.Connections.Find(x => x.ConnectionName == _connectionId) ?? throw new Exception($"Connection with ID '{_connectionId}' not found.");
        // END

        customThreadPool = new CustomThreadPool(new object(), opcClientConnection.MaxSearch);
    }
    public async Task<bool> StartBrowse()
    {
        string tempFilePath = $"{SosNodesPrefix}/temp_{_connectionId}.json";
        
        // consider instead exceptions here..
        if (File.Exists(tempFilePath))
        {
            Console.WriteLine($"Browse job for {_connectionId} already exists..");
            throw new Exception($"Browse job for {_connectionId} already exists..");
        }

        File.WriteAllText(tempFilePath, "");

        // Get Fresh session
        Session session = await OpcuaHelperFunctions.GetSessionByUrl(opcClientConnection.Url);

        JsTreeExport jsTreeExport = new();

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

        List<Thread> childThreads = new();

        Stopwatch stopwatch = new Stopwatch();
        stopwatch.Start();
        // Setup 1st level of jsTreeExport
        foreach (var rd in refs)
        {
            string folderText = rd.DisplayName.Text;

            if (opcClientConnection.BrowseExclusionFolders.Contains(folderText))
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
            try
            {
                childThreads.Add(customThreadPool.AskForThread(() => DFS_Threaded(session, rd, jsTreeNode, opcClientConnection.BrowseExclusionFolders, 1)));
            }
            catch
            {
                DFS_Threaded(session, rd, jsTreeNode, opcClientConnection.BrowseExclusionFolders, 1);
            }

        }

        // Block in main Thread
        // Because we're blocking here, the first children (length n) of ObjectNode will always consume and hold n number of MaxThreads.
        // I.E. The rest of the tree can execute concurrently MaxThread - n Threads.
        foreach (Thread t1 in childThreads)
        {
            t1.Join();
        }

        string json = JsonSerializer.Serialize(jsTreeExport, new JsonSerializerOptions()
        {
            PropertyNamingPolicy = new OpcuaHelperFunctions.LowercaseNamingPolicy(),
            WriteIndented = true
        });

        string filePath = $"{SosNodesPrefix}/{_connectionId}.json";

        string directoryPath = Path.GetDirectoryName(filePath) ?? throw new Exception($"Bad Path: {filePath}");
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        File.WriteAllText(filePath, json);
        stopwatch.Stop();
        Console.WriteLine($"Elapsed Time: {stopwatch.Elapsed.TotalMilliseconds} ms");

        session.Close();
        session.Dispose();

        return true;
    }

    private class CustomThreadPool
    {
        private int maxThreads;
        private int activeThreadCount { get; set; } = 0;
        public int MaxThreads
        {
            get => maxThreads;
            set
            {
                if (activeThreadCount > 0) return;
                // cannot change if active threads
                maxThreads = value;
            }
        }
        private object threadLock;

        public CustomThreadPool(object threadLock, int maxThreads = 1)
        {
            this.maxThreads = maxThreads;
            this.threadLock = threadLock;
        }

        public Thread AskForThread(Action action)
        {
            lock (threadLock)
            {
                if (activeThreadCount < maxThreads)
                {
                    Thread t1 = new Thread(() => action());
                    t1.Start();

                    activeThreadCount += 1;

                    return t1;
                }
            }

            throw new Exception("Thread cannot be paritioned, use existing.");
        }
    }
}