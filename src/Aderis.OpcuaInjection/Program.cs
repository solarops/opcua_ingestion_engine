
using Aderis.OpcuaInjection.Data;
using Aderis.OpcuaInjection.Interfaces;
using Aderis.OpcuaInjection.Helpers;
using Aderis.OpcuaInjection.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

builder.Services.AddSingleton<IOpcHelperService, OpcuaHelperService>();
builder.Services.AddSingleton<IBrowseService, BrowseService>();

builder.Services.AddSingleton<OpcSubscribeService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<OpcSubscribeService>());

builder.Services.AddSingleton<ManualReadService>();

builder.Services.AddAutoMapper(AppDomain.CurrentDomain.GetAssemblies());

var dbConfig = OpcuaHelperFunctions.LoadDbConfig();

builder.Services.AddDbContext<ApplicationDbContext>(opt => {
    opt.UseNpgsql(dbConfig.ClientConfigConnection.ToString());
});


// SEED users / connections?

var app = builder.Build();

// Apply migrations
await using var scope = app.Services.CreateAsyncScope();
await using var _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
await _context.Database.MigrateAsync();

// Configure the HTTP request pipeline.

//app.UseHttpsRedirection(); //if hit endpoint with http, tries respond https. commented out so can use curl with read point

app.UseAuthorization();

app.MapControllers();

app.Run();
