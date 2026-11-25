using Ride.MatchingWorker;
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient("api", c => c.BaseAddress = new Uri(builder.Configuration["Services:TripApi"]!));
builder.Services.AddHostedService<Worker>();
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ");
await builder.Build().RunAsync();
