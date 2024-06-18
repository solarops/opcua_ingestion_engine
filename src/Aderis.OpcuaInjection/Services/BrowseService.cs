using Aderis.OpcuaInjection.Interfaces;
using Aderis.OpcuaInjection.Helpers;
using System.Collections.Concurrent;

namespace Aderis.OpcuaInjection.Services;

public class BrowseService : IBrowseService
{
    private readonly ConcurrentDictionary<string, bool> _taskStatuses = new ConcurrentDictionary<string, bool>();
    public Task<bool> IsBrowseJobRunning(string connectionId)
    {
        return Task.FromResult(_taskStatuses.TryGetValue(connectionId, out var status) && status);
    }

    public async Task StartBrowseJob(CancellationToken cts, string connectionId)
    {
        OpcuaBrowse opcuaBrowse = new(cts, connectionId);

        await Task.Run(async () => {
            await opcuaBrowse.StartBrowse();

            _taskStatuses.AddOrUpdate(connectionId, false, (key, oldValue) => false);
        });

        _taskStatuses.AddOrUpdate(connectionId, true, (key, oldValue) => true);
    }
}
