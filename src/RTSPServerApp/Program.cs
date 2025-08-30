using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RTSPServerApp;

SharpH26X.Log.SinkDebug = (o, e) => { };
SharpH26X.Log.SinkInfo = (o, e) => { };
SharpAV1.Log.SinkInfo = (o, e) => { };
SharpAV1.Log.SinkDebug = (o, e) => { };
SharpISOBMFF.Log.SinkInfo = (o, e) => { };
SharpISOBMFF.Log.SinkDebug = (o, e) => { };
SharpMP4.Log.SinkInfo = (o, e) => { };
SharpMP4.Log.SinkDebug = (o, e) => { };

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<RTSPServerWorker>();

var host = builder.Build();
host.Run();
