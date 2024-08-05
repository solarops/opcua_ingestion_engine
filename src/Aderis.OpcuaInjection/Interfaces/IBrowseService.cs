namespace Aderis.OpcuaInjection.Interfaces;

public interface IBrowseService
{
    Task StartBrowseJob(CancellationToken cts, string connectionName);
    Task<bool> IsBrowseJobRunning(string connectionName);
}
