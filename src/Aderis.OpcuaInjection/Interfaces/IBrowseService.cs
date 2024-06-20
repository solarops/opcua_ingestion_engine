namespace Aderis.OpcuaInjection.Interfaces;

public interface IBrowseService
{
    Task StartBrowseJob(CancellationToken cts, string connectionId);
    Task<bool> IsBrowseJobRunning(string connectionId);
}
