
using Aderis.OpcuaInjection.Interfaces;
using Aderis.OpcuaInjection.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

builder.Services.AddSingleton<IBrowseService, BrowseService>();

builder.Services.AddHostedService<OpcSubscribeService>();

var app = builder.Build();

// Configure the HTTP request pipeline.

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
