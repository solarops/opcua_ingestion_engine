using Aderis.OpcuaInjection.Models;
using Opc.Ua.Client;

namespace Aderis.OpcuaInjection.Interfaces;

public interface IOpcHelperService
{
    Task<List<OpcClientConnection>> LoadClientConfig();
    Task<OpcClientConnection> LoadClientConfigByName(string connectionName);
    Task<bool> AddClientConfig(OpcClientConnection connection);
    Task<bool> UpdateClientConfig(OpcClientConnection connection);
    Task<bool> RemoveClientConfigByName(string connectionName);
    string DecryptPassword(byte[]? encryptedPassword);
    
}
