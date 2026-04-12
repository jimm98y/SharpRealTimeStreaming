using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RTSPServerApp;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<RTSPServerWorker>();

var host = builder.Build();
host.Run();
