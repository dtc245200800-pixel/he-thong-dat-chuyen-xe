using Microsoft.AspNetCore.SignalR;
using Ride.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy.WithOrigins("http://localhost:3000").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
var app = builder.Build();
app.UseCors();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapHub<TripHub>("/hubs/trips");
app.MapPost("/internal/events", async (TripStatusChanged evt, IHubContext<TripHub> hub, ILogger<Program> log) =>
{
    await hub.Clients.Group($"trip:{evt.TripId}").SendAsync("TripStatusChanged", evt);
    await hub.Clients.Group($"passenger:{evt.PassengerId}").SendAsync("TripStatusChanged", evt);
    log.LogInformation("Published {Status} for trip {TripId}", evt.Status, evt.TripId);
    return Results.Accepted();
});
app.Run();

public sealed class TripHub : Hub
{
    public Task JoinTrip(Guid tripId) => Groups.AddToGroupAsync(Context.ConnectionId, $"trip:{tripId}");
    public Task JoinPassenger(string passengerId) => Groups.AddToGroupAsync(Context.ConnectionId, $"passenger:{passengerId}");
}
