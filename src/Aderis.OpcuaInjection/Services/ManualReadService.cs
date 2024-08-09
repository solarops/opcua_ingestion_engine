using Opc.Ua;
using Opc.Ua.Client;


namespace Aderis.OpcuaInjection.Services;

/*
    NOTE: For site with slow updating devices, the timeout may need to be extended, as the timeout callback is in
    danger of being fired.
        - Have the main loop ask for a "alive?" message to servers every x sec

    NOTE: Consider implementing a queue (Kafka?) where the SubscribedItemChange and TimestampUpdate both push
    needed changes and a timestamp to, and the queue reconciles which value gets set
*/

public class ManualReadService
{
    private readonly OpcSubscribeService _opcSubscribeService;

    public ManualReadService(OpcSubscribeService opcSubscribeService)
    {
        _opcSubscribeService = opcSubscribeService;
    }

    public async Task<ReadResult> ReadDataPoint(string serverUrl, string nodeId, CancellationToken ct)
  {
      Console.WriteLine($"In ReadDataPoint(serverUrl,nodeId): Reading data point {nodeId} from server {serverUrl}...");
      Session session;
      try
      {
          session = _opcSubscribeService.GetOpcClientByUrl(serverUrl);
      }
      catch (Exception ex)
      {
          // session = await OpcuaHelperFunctions.GetNewSessionByUrl(serverUrl);
          // _opcClientsByUrl[serverUrl] = session;
          throw new Exception("No existing connection to server: " + serverUrl + ". Exception message: " + ex.Message);
      }

      NodeId targetNodeId = new NodeId(nodeId);
      ReadValueId readValueId = new ReadValueId()
      {
          NodeId = targetNodeId,
          AttributeId = Attributes.Value
      };

      ReadRequest readRequest = new ReadRequest()
      {
          NodesToRead = new ReadValueIdCollection() { readValueId },
          MaxAge = 0, // Request the most recent value
          TimestampsToReturn = TimestampsToReturn.Both //return server and source timestamps
      };

      RequestHeader requestHeader = new RequestHeader(); // Create a request header
      //CancellationToken ct = new CancellationToken(); // Typically obtained from the calling context or passed as a parameter

      try
      {
          // Adjust the method call to include all required parameters
          ReadResponse readResponse = await session.ReadAsync(requestHeader, 0, TimestampsToReturn.Both, readRequest.NodesToRead, ct);
          if (readResponse.Results.Count > 0 && StatusCode.IsGood(readResponse.Results[0].StatusCode))
          {
              return new ReadResult
              {
                  NodeId = targetNodeId.ToString(),
                  Value = readResponse.Results[0].Value,
                  Status = readResponse.Results[0].StatusCode,
                  OpcDataSourceTimestamp = readResponse.Results[0].SourceTimestamp,
                  OpcServerTimestamp = readResponse.Results[0].ServerTimestamp,
                  DotnetEngineTimestamp = DateTime.UtcNow
              };
          }
          else
          {
              throw new Exception("Failed to read the data point.");
          }
      }
      catch (Exception ex)
      {
          throw new Exception($"Error reading data from OPC UA server: {ex.Message}", ex);
      }
  }


  public class ReadResult
  {
      public string NodeId { get; set; } 
      public object Value { get; set; }
      public StatusCode Status { get; set; }
      public DateTime OpcDataSourceTimestamp { get; set; } //when data last changed by source
      public DateTime OpcServerTimestamp { get; set; } //time on opc server when method called
      public DateTime DotnetEngineTimestamp { get; set; } //time on computer running this code when method called

      public override string ToString()
      {
          return $"NODEID: {NodeId}, \n\n" +
              $"VALUE: {Value}, STATUS: {Status}, \n\n" +
              $"OPC DATA POINT TIMESTAMP: {OpcDataSourceTimestamp.ToString("MM/dd/yyyy HH:mm:ss.ffffff")}, \n" +
              $"OpcServerTimestamp: {OpcServerTimestamp.ToString("MM/dd/yyyy HH:mm:ss.ffffff")}, \n" +
              $"DotnetEngineTimestamp: {DotnetEngineTimestamp.ToString("MM/dd/yyyy HH:mm:ss.ffffff")}\n";
      }
  }
}