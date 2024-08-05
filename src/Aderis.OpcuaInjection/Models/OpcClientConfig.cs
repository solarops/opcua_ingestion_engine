namespace Aderis.OpcuaInjection.Models;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

public class OpcClientConnection
{
    [Key]
    public int Id { get; set; }
    public required string ConnectionName { get; set; } = "";
    
    public required string Url { get; set; }
    
    public required List<BrowseExclusionFolder> BrowseExclusionFolders { get; set; }
    public required int MaxSearch { get; set; }
    public required int TimeoutMs { get; set; }

    public string? UserName { get; set; }
    public byte[]? EncryptedPassword { get; set; }

    // Enable configuration when ready
    // public int TcpDeltaSeconds1 { get; set; }
    // public int TcpDeltaIterations1 { get; set; }
    // public int TcpDeltaSeconds2 { get; set; }
    // public int TcpDeltaIterations2 { get; set; }

    public List<string> GetBrowseFolderValues()
    {
        var ret = new List<string>();

        foreach (var folder in BrowseExclusionFolders)
        {
            ret.Add(folder.ExclusionFolder);
        }

        return ret;
    }
}

public class BrowseExclusionFolder
{
    [Key]
    public int Id { get; set; }
    public required int OpcClientConnectionId { get; set; }
    public required OpcClientConnection OpcClientConnection { get; set; }
    public required string ExclusionFolder { get; set; }
}

public class OpcClientConnectionDto
{
    public required string ConnectionName { get; set; } = "";
    
    public required string Url { get; set; }
    
    public required List<string> BrowseExclusionFolders { get; set; }
    public required int MaxSearch { get; set; }
    public required int TimeoutMs { get; set; }

    public string? UserName { get; set; }
    public string? Password { get; set; }
}