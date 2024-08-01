using Aderis.OpcuaInjection.Interfaces;
using Aderis.OpcuaInjection.Helpers;
using System.Collections.Concurrent;

using Opc.Ua;

namespace Aderis.OpcuaInjection.Services;

public class BrowseService : IBrowseService
{   
    private readonly IOpcHelperService _opcHelperService;
    
    // Globals
    private readonly ConcurrentDictionary<string, bool> _taskStatuses = new ConcurrentDictionary<string, bool>();

    public BrowseService(IOpcHelperService opcHelperService)
    {
        _opcHelperService = opcHelperService;
    }
    public Task<bool> IsBrowseJobRunning(string connectionId)
    {
        return Task.FromResult(_taskStatuses.TryGetValue(connectionId, out var status) && status);
    }

    public async Task StartBrowseJob(CancellationToken cts, string connectionId)
    {
        var clientConnection = await _opcHelperService.LoadClientConfigByName(connectionId);

        var userIdentity = _opcHelperService.GetUserIdentity(clientConnection);

        OpcuaBrowse opcuaBrowse = new(cts, clientConnection, userIdentity);

        await Task.Run(async () => {
            
            await opcuaBrowse.StartBrowse();

            _taskStatuses.AddOrUpdate(connectionId, false, (key, oldValue) => false);
        });

        _taskStatuses.AddOrUpdate(connectionId, true, (key, oldValue) => true);
    }
}
