using Aderis.OpcuaInjection.Models;
using Aderis.OpcuaInjection.Interfaces;
using Aderis.OpcuaInjection.Extensions;

using Aderis.OpcuaInjection.Data;
using Microsoft.EntityFrameworkCore;
using AutoMapper;

using System.Security.Cryptography;
using System.Text;
using Opc.Ua;

namespace Aderis.OpcuaInjection.Services;

public class OpcuaHelperService : IOpcHelperService
{
    // private readonly ApplicationDbContext _context;
    private readonly IServiceProvider _provider;
    private readonly IMapper _mapper;
    private readonly OpcUserIdentityConfig _opcUserIdentityConfig;

    private class OpcUserIdentityConfig
    {
        public bool UserConfig(out byte[] Key, out byte[] Iv)
        {
            Key = _key ?? [];
            Iv = _iv ?? [];
            return _key != null && _iv != null;
        }
        private readonly byte[]? _key;
        private readonly byte[]? _iv;
        public OpcUserIdentityConfig(string keyEnv, string ivEnv)
        {
            _key = setFromEnv(keyEnv);
            _iv = setFromEnv(ivEnv);
        }

        private byte[]? setFromEnv(string env)
        {
            try
            {
                var fp = Environment.GetEnvironmentVariable(env) ?? throw new NullReferenceException();
                return File.ReadAllBytes(fp);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception occurred when parsing keyfile: {ex.Message}");
                Console.Error.WriteLine($"Exception occurred when parsing keyfile: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
            }

            return null;
        }
    }

    public OpcuaHelperService(IServiceProvider provider, IMapper mapper)
    {
        _provider = provider;
        _mapper = mapper;

        _opcUserIdentityConfig = new("OPCUA_PW_ENCRYPTION_KEY", "OPCUA_IV");
    }

    public async Task<List<OpcClientConnection>> LoadClientConfig()
    {
        var scope = _provider.CreateScope();
        var _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var config = await _context.OpcClientConnections
            .Include(x => x.BrowseExclusionFolders)
            .ToListAsync();
        scope.Dispose();

        if (!config.Any())
        {
            Console.WriteLine("No client configurations found. Adding default configuration with Ignition server at 62541");
            var defaultConnection = new OpcClientConnection
            {
                ConnectionName = "Ignition",
                Url = "opc.tcp://10.10.100.1:62541/discovery",
                MaxSearch = 600,
                TimeoutMs = 60000,  // Adjusted the property name to match the class definition
                BrowseExclusionFolders = new List<BrowseExclusionFolder>() //could add "server" and "devices" folders here to exclude by default
            };

            // Add to the list to return
            config.Add(defaultConnection);
        }
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
            if (!_opcUserIdentityConfig.UserConfig(out var key, out var iv))
            {
                Console.WriteLine("Requested Password, but has no encryption keys generated.");
                Console.Error.WriteLine("Requested Password, but has no encryption keys generated.");
                return false;
            }

            connection.EncryptedPassword = Encrypt(connection.EncryptedPassword, key, iv);
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
                if (!_opcUserIdentityConfig.UserConfig(out var key, out var iv)) {
                    Console.WriteLine("Requested Password, but has no encryption keys generated.");
                    Console.Error.WriteLine("Requested Password, but has no encryption keys generated.");
                    return false;
                }
                existingEntry.EncryptedPassword = Encrypt(connection.EncryptedPassword, key, iv);
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
                var newItem = new BrowseExclusionFolder()
                {
                    ExclusionFolder = sharedItem.ExclusionFolder,
                    OpcClientConnectionId = existingEntry.Id,
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

    private byte[] Encrypt(byte[] plainBytes, byte[] key, byte[] iv)
    {
        using (Aes aesAlg = Aes.Create())
        {
            aesAlg.KeySize = 256;
            aesAlg.BlockSize = 128;
            aesAlg.Key = key;
            aesAlg.IV = iv;

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
        if (!_opcUserIdentityConfig.UserConfig(out var key, out var iv))
        {
            Console.WriteLine("Requested Password, but has no decryption keys generated.");
            Console.Error.WriteLine("Requested Password, but has no decryption keys generated.");
            return "";
        }

        using (Aes aesAlg = Aes.Create())
        {
            aesAlg.KeySize = 256;
            aesAlg.BlockSize = 128;
            aesAlg.Key = key;
            aesAlg.IV = iv;

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

    public UserIdentity GetUserIdentity(OpcClientConnection connection)
    {
        var password = DecryptPassword(connection.EncryptedPassword).Trim();
        
        if (!string.IsNullOrEmpty(connection.UserName) && !string.IsNullOrEmpty(password))
        {
            return new UserIdentity(
                connection.UserName.Trim(),
                password.Trim()
            );
        }

        return new UserIdentity(new AnonymousIdentityToken());

    }
}
