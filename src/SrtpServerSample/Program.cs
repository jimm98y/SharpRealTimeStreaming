using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SrtpServerSample;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<RTSPServerWorker>();

var host = builder.Build();
host.Run();
