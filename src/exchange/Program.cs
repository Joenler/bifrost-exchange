using Bifrost.Exchange;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<StartupLogger>();

var host = builder.Build();
await host.RunAsync();
