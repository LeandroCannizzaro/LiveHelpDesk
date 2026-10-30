using LiveHelpDesk.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "LiveHelpDesk");
builder.Services.AddHostedService<HeartbeatWorker>();

await builder.Build().RunAsync();
