using Aderis.OpcuaInjection.Models;
using Aderis.OpcuaInjection.Interfaces;
using Aderis.OpcuaInjection.Extensions;

using Aderis.OpcuaInjection.Data;
using Microsoft.EntityFrameworkCore;
using AutoMapper;

using System.Security.Cryptography;
using System.Text;

namespace Aderis.OpcuaInjection.Services;

public class OpcuaHelperService : IOpcHelperService
{
    // private readonly ApplicationDbContext _context;
    private readonly IServiceProvider _provider;
    private readonly IMapper _mapper;
    private readonly byte[] _key;
    private readonly byte[] _iv;

    public OpcuaHelperService(IServiceProvider provider, IMapper mapper)
    {
        _provider = provider;
        _mapper = mapper;

        var keyFilePath = Environment.GetEnvironmentVariable("OPCUA_PW_ENCRYPTION_KEY");
        var ivFilePath = Environment.GetEnvironmentVariable("OPCUA_IV");

        if (string.IsNullOrEmpty(keyFilePath) || string.IsNullOrEmpty(ivFilePath))
        {
            throw new InvalidOperationException("Key file paths are not set in environment variables.");
        }

        _key = File.ReadAllBytes(keyFilePath);
        _iv = File.ReadAllBytes(ivFilePath);
    }

    public async Task<List<OpcClientConnection>> LoadClientConfig()
    {
        var scope = _provider.CreateScope();
        var _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var config = await _context.OpcClientConnections
            .Include(x => x.BrowseExclusionFolders)
            .ToListAsync();
        scope.Dispose();

        return config;
    }
    public async Task<bool> AddClientConfig(OpcClientConnection connection)
    {
        var scope = _provider.CreateScope();
        var _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var existingEntry = await _context.OpcClientConnections.FirstOrDefaultAsync(x => x.ConnectionName == connection.ConnectionName);
        
        if (existingEntry != null) return false;

        if (connection.EncryptedPassword != null)
        {
            connection.EncryptedPassword = Encrypt(connection.EncryptedPassword);
        }

        _context.OpcClientConnections.Add(connection);
        var result = await _context.SaveChangesAsync() > 0;
        scope.Dispose();

        return result;
    }

    public async Task<bool> UpdateClientConfig(OpcClientConnection connection)
    {
        var scope = _provider.CreateScope();
        var _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        try
        {
            var existingEntry = await _context.OpcClientConnections
            .Include(x => x.BrowseExclusionFolders)
            .FirstOrDefaultAsync(x => x.ConnectionName == connection.ConnectionName);

            if (existingEntry == null) return false;

            _mapper.Map(connection, existingEntry);

            if (connection.EncryptedPassword != null)
            {
                existingEntry.EncryptedPassword = Encrypt(connection.EncryptedPassword);
            }

            // Handle Folders
            var foldersVenn = existingEntry.BrowseExclusionFolders.GetVennSet(
                connection.BrowseExclusionFolders, x => x.ExclusionFolder, o => o.ExclusionFolder
            );

            foreach (var folder in foldersVenn.OnlyInMyItems)
            {
                existingEntry.BrowseExclusionFolders.Remove(folder);
            }
            foreach (var match in foldersVenn.InBoth)
            {
                match.MyItem.ExclusionFolder = match.OtherItem.ExclusionFolder; 
            }
            foreach (var sharedItem in foldersVenn.OnlyInOtherItems)
            {
                var newItem = new BrowseExclusionFolder() {
                    ExclusionFolder = sharedItem.ExclusionFolder,
                    ConnectionOpcClientConnectionId = existingEntry.Id,
                    OpcClientConnection = existingEntry
                };
               existingEntry.BrowseExclusionFolders.Add(newItem);
            }

            return await _context.SaveChangesAsync() > 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Requested edit {connection.ConnectionName} that does not exist.");
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return false;
        } 
        finally
        {
            scope.Dispose();
        }
    }

    public async Task<OpcClientConnection> LoadClientConfigByName(string connectionName)
    {
        var scope = _provider.CreateScope();
        var _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var config = await _context.OpcClientConnections
            .Include(x => x.BrowseExclusionFolders)
            .FirstOrDefaultAsync(x => x.ConnectionName == connectionName)
            ?? throw new Exception($"Could not find entry for {connectionName}");
        
        scope.Dispose();
        
        return config;
    }

    private byte[] Encrypt(byte[] plainBytes)
    {
        using (Aes aesAlg = Aes.Create())
        {
            aesAlg.KeySize = 256;
            aesAlg.BlockSize = 128;
            aesAlg.Key = _key;
            aesAlg.IV = _iv;

            ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);

            using (MemoryStream msEncrypt = new MemoryStream())
            {
                using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                {
                    csEncrypt.Write(plainBytes, 0, plainBytes.Length);
                    csEncrypt.FlushFinalBlock();
                    return msEncrypt.ToArray();
                }
            }
        }
    }

    public string DecryptPassword(byte[]? encryptedPassword)
    {
        if (encryptedPassword == null) return "";

        using (Aes aesAlg = Aes.Create())
        {
            aesAlg.KeySize = 256;
            aesAlg.BlockSize = 128;
            aesAlg.Key = _key;
            aesAlg.IV = _iv;

            ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

            using (MemoryStream msDecrypt = new MemoryStream(encryptedPassword))
            {
                using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                {
                    using (StreamReader srDecrypt = new StreamReader(csDecrypt))
                    {
                        return srDecrypt.ReadToEnd();
                    }
                }
            }
        }
    }

    public async Task<bool> RemoveClientConfigByName(string connectionName)
    {
        var scope = _provider.CreateScope();
        try
        {
            var _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var config = await _context.OpcClientConnections
            .Include(x => x.BrowseExclusionFolders)
            .FirstOrDefaultAsync(x => x.ConnectionName == connectionName);
            
            if (config == null) return false;

            _context.OpcClientConnections.Remove(config);

            return await _context.SaveChangesAsync() > 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Requested delete {connectionName} that does not exist.");
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            
            return false;
        }
        finally
        {
            scope.Dispose();
        };        
    }
}
