using Aderis.OpcuaInjection.Interfaces;
using Aderis.OpcuaInjection.Models;
using Aderis.OpcuaInjection.Services;
using AutoMapper;
using Microsoft.AspNetCore.Mvc;

namespace Aderis.OpcuaInjection.Controllers;

public class ClientController : BaseApiController
{
    private readonly IOpcHelperService _opcHelperService;
    private readonly OpcSubscribeService _opcSubscribeService;
    private readonly IMapper _mapper;

    public ClientController(IOpcHelperService opcHelperService, OpcSubscribeService opcSubscribeService, IMapper mapper)
    {
        _opcHelperService = opcHelperService;
        _opcSubscribeService = opcSubscribeService;
        _mapper = mapper;
    }

    [HttpDelete("config/delete/{connectionName}")]
    public async Task<IActionResult> DeleteConnection(string connectionName)
    {
        var saved = await _opcHelperService.RemoveClientConfigByName(connectionName);

        if (saved) {
            _opcSubscribeService.ReloadPolling();
            return Ok();
        }

        return BadRequest();
    }

    [HttpPatch("config/update")]
    // Notify process after changing opcua client config.
    public async Task<IActionResult> UpdateConfig(OpcClientConnectionDto opcClientConnectionDto)
    {
        var opcClientConnection = _mapper.Map<OpcClientConnection>(opcClientConnectionDto);

        opcClientConnection.BrowseExclusionFolders = _mapper.Map<List<BrowseExclusionFolder>>(opcClientConnectionDto.BrowseExclusionFolders);
        
        var saved = await _opcHelperService.UpdateClientConfig(opcClientConnection);
        
        if (saved) {
            _opcSubscribeService.ReloadPolling();
            return Ok();
        }

        return BadRequest();
    }

    [HttpPost("config/add")]
    public async Task<IActionResult> AddConfig(OpcClientConnectionDto opcClientConnectionDto)
    {
        var opcClientConnection = _mapper.Map<OpcClientConnection>(opcClientConnectionDto);

        opcClientConnection.BrowseExclusionFolders = _mapper.Map<List<BrowseExclusionFolder>>(opcClientConnectionDto.BrowseExclusionFolders);
        
        var saved = await _opcHelperService.AddClientConfig(opcClientConnection);

        if (saved) {
            _opcSubscribeService.ReloadPolling();
            return Ok();
        }

        return BadRequest();
    }

    [HttpGet("config/get")]
    public async Task<ActionResult<List<OpcClientConnectionDto>>> GetConnectionsAsync()
    {
        var opcClientConnections = await _opcHelperService.LoadClientConfig();
        var ret = new List<OpcClientConnectionDto>();

        foreach ( var connection in opcClientConnections )
        {
            var dto = _mapper.Map<OpcClientConnectionDto>(connection);
            dto.BrowseExclusionFolders = _mapper.Map<List<string>>(connection.BrowseExclusionFolders);
            
            if (connection.EncryptedPassword != null) dto.Password = _opcHelperService.DecryptPassword(connection.EncryptedPassword);
            
            ret.Add(dto);
        }
        
        return Ok(ret);
    }
}
