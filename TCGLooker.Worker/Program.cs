using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TCGLooker.Infra;
using TCGLooker.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<ScrapeSchedulerWorker>();
builder.Services.AddHostedService<ListingRetentionWorker>();
builder.Services.AddHostedService<NotificationWorker>();

await builder.Build().RunAsync();
