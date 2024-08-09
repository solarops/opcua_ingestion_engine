using Aderis.OpcuaInjection.Interfaces;
using Aderis.OpcuaInjection.Models;
using Aderis.OpcuaInjection.Services;
using AutoMapper;
using Microsoft.AspNetCore.Mvc;

namespace Aderis.OpcuaInjection.Controllers;

public class ManualReadController : BaseApiController
{
    private readonly ManualReadService _manualReadSvc;

    public ManualReadController(ManualReadService manualReadService)
    {
        _manualReadSvc = manualReadService;
    }

    /// <summary>
    /// Retrieves the latest data point value from a specific OPC UA server and node.
    /// </summary>
    /// <param name="serverUrl">The URL of the OPC UA server.</param>
    /// <param name="nodeId">The identifier of the node in the OPC UA server.</param>
    /// <remarks>
    /// EXAMPLE USE:
    /// (port forwarded to 8080)(requires comment out https redirection line in Program.cs)
    /// curl "http://127.0.0.1:8080/api/ManualRead/read?serverUrl=opc.tcp%3A%2F%2Flocalhost%3A62541%2Fdiscovery&nodeId=ns%3D2%3Bs%3D%5Bdefault%5D%2FInverter_Tags%2FPCS_1_1_6%2F000%20-%20Raw%20Tags%2FINV1_ACTIVE_POWER"
    /// (or with default config serverUrl)
    /// curl "http://127.0.0.1:8080/api/ManualRead/read?serverUrl=opc.tcp%3A%2F%2F10.10.100.1%3A62541%2Fdiscovery&nodeId=ns%3D2%3Bs%3D%5Bdefault%5D%2FInverter_Tags%2FPCS_1_1_6%2F000%20-%20Raw%20Tags%2FINV1_ACTIVE_POWER"
    /// 
    /// EXAMPLE RESPONSE:
    /// NodeId: ns=2;s=[default]/Inverter_Tags/PCS_1_1_6/000 - Raw Tags/INV1_ACTIVE_POWER, VALUE: 106, Status: Good,
    /// StatusString: Good, OPC DATA POINT TIMESTAMP: 08/08/2024 20:46:01.041000,
    /// OpcServerTimestamp: 08/08/2024 20:46:01.041000, DotnetEngineTimestamp: 08/08/2024 20:46:00.987597
    /// </remarks>
    [HttpGet("read")]
    public async Task<IActionResult> ReadDataPoint([FromQuery] string serverUrl, [FromQuery] string nodeId)
    {
      //Console.WriteLine($"Controller HttpGet(read) hit, method: ReadDataPoint: {serverUrl} {nodeId}");
        try
        {
            CancellationToken ct = HttpContext.RequestAborted; //cancel token specific to this request
            var result = await _manualReadSvc.ReadDataPoint(serverUrl, nodeId, ct);
            //Console.WriteLine($"ReadDataPoint: {result}");
            //return Ok(result);
            return Content(result.ToString(), "text/plain");
        }
        catch (System.Exception ex)
        {
            return BadRequest($"Error reading data point: {ex.Message}");
        }
    }
}
