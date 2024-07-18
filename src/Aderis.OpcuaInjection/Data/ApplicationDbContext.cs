using Microsoft.EntityFrameworkCore;
using Aderis.OpcuaInjection.Models;

namespace Aderis.OpcuaInjection.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<OpcClientConnection> OpcClientConnections { get; set; }
    public DbSet<BrowseExclusionFolder> BrowseExclusionFolders { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}
