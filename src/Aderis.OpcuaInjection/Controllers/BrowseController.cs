using Aderis.OpcuaInjection.Interfaces;
using Aderis.OpcuaInjection.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aderis.OpcuaInjection.Controllers;

public class BrowseController : BaseApiController
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IBrowseService _browseService;

    public BrowseController(IBrowseService browseService, IHostApplicationLifetime applicationLifetime)
    {
        _browseService = browseService;
        _applicationLifetime = applicationLifetime;
    }

    [HttpGet("startBrowseJob/{connectionId}")]
    public IActionResult StartBrowseJob(string connectionId)
    {
        // how does ApplicationStopping work?
        _browseService.StartBrowseJob(_applicationLifetime.ApplicationStopping, connectionId);
        
        return Ok(new { Message = "Long-running process started." });
    }
}
