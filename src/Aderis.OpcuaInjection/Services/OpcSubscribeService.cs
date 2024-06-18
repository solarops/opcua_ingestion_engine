using Aderis.OpcuaInjection.Helpers;

namespace Aderis.OpcuaInjection.Services;

public class OpcSubscribeService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await OpcuaSubscribe.Start(stoppingToken);
    }

    public Task StartSubscribe(CancellationToken stoppingToken)
    {
        return ExecuteAsync(stoppingToken);
    }
}
