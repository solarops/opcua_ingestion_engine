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

    [HttpPatch("config/update")]
    // Notify process after changing opcua client config.
    public async Task<IActionResult> UpdateConfig(OpcClientConnectionDto opcClientConnectionDto)
    {
        var opcClientConnection = _mapper.Map<OpcClientConnection>(opcClientConnectionDto);
        
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
        
        var saved = await _opcHelperService.AddClientConfig(opcClientConnection);

        if (saved) {
            _opcSubscribeService.ReloadPolling();
            return Ok();
        }

        return BadRequest();
    }
}
