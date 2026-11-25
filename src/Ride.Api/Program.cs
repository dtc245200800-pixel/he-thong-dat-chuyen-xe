using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using Ride.Contracts;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<RideDb>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("Rides")));
builder.Services.AddSingleton<RabbitPublisher>();
builder.Services.AddHttpClient("notifications", c => c.BaseAddress = new Uri(builder.Configuration["Services:Notifications"]!));
builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy.WithOrigins("http://localhost:3000").AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
app.UseCors();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RideDb>();
    await db.Database.EnsureCreatedAsync();
    if (!await db.Drivers.AnyAsync())
    {
        db.Drivers.AddRange(new Driver { Name = "Minh Nguyễn", Latitude = 10.7769, Longitude = 106.7009, IsAvailable = true }, new Driver { Name = "An Trần", Latitude = 10.7812, Longitude = 106.6951, IsAvailable = true }, new Driver { Name = "Linh Phạm", Latitude = 10.7700, Longitude = 106.7080, IsAvailable = true });
        await db.SaveChangesAsync();
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/trips", async (CreateTrip request, RideDb db, RabbitPublisher queue, ILogger<Program> log) =>
{
    if (string.IsNullOrWhiteSpace(request.PassengerId) || string.IsNullOrWhiteSpace(request.Pickup) || string.IsNullOrWhiteSpace(request.Destination)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["trip"] = ["PassengerId, pickup and destination are required."] });
    if (!ValidCoordinates(request.PickupLatitude, request.PickupLongitude) || !ValidCoordinates(request.DestinationLatitude, request.DestinationLongitude)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["coordinates"] = ["Latitude or longitude is outside its valid range."] });
    var trip = new Trip { PassengerId = request.PassengerId.Trim(), Pickup = request.Pickup.Trim(), Destination = request.Destination.Trim(), PickupLatitude = request.PickupLatitude, PickupLongitude = request.PickupLongitude, DestinationLatitude = request.DestinationLatitude, DestinationLongitude = request.DestinationLongitude };
    db.Trips.Add(trip); await db.SaveChangesAsync();
    try { await queue.PublishAsync(new TripRequested(trip.Id)); } catch (Exception ex) { log.LogError(ex, "Failed to enqueue trip {TripId}", trip.Id); }
    log.LogInformation("Trip {TripId} created for passenger {PassengerId}", trip.Id, trip.PassengerId);
    return Results.Accepted($"/trips/{trip.Id}", ToDto(trip));
});
app.MapGet("/trips/{id:guid}", async (Guid id, string? passengerId, RideDb db) => { var t = await db.Trips.Include(x => x.Driver).FirstOrDefaultAsync(x => x.Id == id && (passengerId == null || x.PassengerId == passengerId)); return t is null ? Results.NotFound() : Results.Ok(ToDto(t)); });
app.MapGet("/users/{userId}/trips", async (string userId, RideDb db) => Results.Ok(await db.Trips.Include(t => t.Driver).Where(t => t.PassengerId == userId).OrderByDescending(t => t.CreatedAt).Select(t => ToDto(t)).ToListAsync()));
app.MapGet("/drivers/available", async (RideDb db) => Results.Ok(await db.Drivers.Where(d => d.IsAvailable).Select(d => new DriverDto(d.Id, d.Name, d.Latitude, d.Longitude, d.IsAvailable)).ToListAsync()));

app.MapPatch("/trips/{id:guid}/status", async (Guid id, UpdateStatus body, RideDb db, IHttpClientFactory clients, ILogger<Program> log) =>
{
    var status = Normalize(body.Status); if (status is not ("InProgress" or "Completed" or "Cancelled")) return Results.BadRequest(new { error = "Status must be InProgress, Completed, or Cancelled." });
    var t = await db.Trips.Include(x => x.Driver).FirstOrDefaultAsync(x => x.Id == id); if (t is null) return Results.NotFound();
    if (!CanTransition(t.Status, status)) return Results.Conflict(new { error = $"Cannot change status from {t.Status} to {status}." });
    t.Status = status; if ((status is "Completed" or "Cancelled") && t.Driver is not null) t.Driver.IsAvailable = true; t.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
    await PublishStatus(clients, log, t, status); return Results.Ok(ToDto(t));
});

app.MapGet("/internal/drivers/available", async (RideDb db) => Results.Ok(await db.Drivers.Where(d => d.IsAvailable).Select(d => new DriverDto(d.Id, d.Name, d.Latitude, d.Longitude, d.IsAvailable)).ToListAsync()));
app.MapGet("/internal/trips/{id:guid}", async (Guid id, RideDb db) => { var t = await db.Trips.FindAsync(id); return t is null ? Results.NotFound() : Results.Ok(new MatchTripDto(t.Id, t.PassengerId, t.PickupLatitude, t.PickupLongitude)); });
app.MapPost("/internal/trips/{id:guid}/match", async (Guid id, MatchRequest body, RideDb db, IHttpClientFactory clients, ILogger<Program> log) =>
{
    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
    var t = await db.Trips.Include(x => x.Driver).FirstOrDefaultAsync(x => x.Id == id); var d = await db.Drivers.FirstOrDefaultAsync(x => x.Id == body.DriverId && x.IsAvailable);
    if (t is null || d is null || t.Status != "Requested") return Results.Conflict(new { error = "Trip or driver is no longer available." });
    d.IsAvailable = false; t.DriverId = d.Id; t.Driver = d; t.Status = "Matched"; t.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); await tx.CommitAsync();
    await PublishStatus(clients, log, t, "Matched"); return Results.Ok(ToDto(t));
});
app.MapPost("/internal/trips/{id:guid}/unmatched", async (Guid id, RideDb db, IHttpClientFactory clients, ILogger<Program> log) =>
{
    var t = await db.Trips.FirstOrDefaultAsync(x => x.Id == id); if (t is null || t.Status != "Requested") return Results.NotFound();
    t.Status = "Cancelled"; t.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); await PublishStatus(clients, log, t, "Cancelled", "No available driver was found."); return Results.Ok();
});
app.Run();

static bool ValidCoordinates(double lat, double lon) => lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
static string Normalize(string s) => s.Trim().ToLowerInvariant() switch { "inprogress" => "InProgress", "completed" => "Completed", "cancelled" => "Cancelled", _ => s };
static bool CanTransition(string a, string b) => (a, b) is ("Matched", "InProgress") or ("InProgress", "Completed") or ("Requested", "Cancelled") or ("Matched", "Cancelled") or ("InProgress", "Cancelled");
static TripDto ToDto(Trip t) => new(t.Id, t.PassengerId, t.Pickup, t.Destination, t.PickupLatitude, t.PickupLongitude, t.DestinationLatitude, t.DestinationLongitude, t.Status, t.Driver is null ? null : new DriverDto(t.Driver.Id, t.Driver.Name, t.Driver.Latitude, t.Driver.Longitude, t.Driver.IsAvailable), t.CreatedAt, t.UpdatedAt);
static async Task PublishStatus(IHttpClientFactory clients, ILogger log, Trip t, string status, string? message = null)
{
    var evt = new TripStatusChanged(t.Id, t.PassengerId, status, t.Driver?.Id, t.Driver?.Name, DateTimeOffset.UtcNow, message);
    try { var response = await clients.CreateClient("notifications").PostAsJsonAsync("/internal/events", evt); response.EnsureSuccessStatusCode(); } catch (Exception ex) { log.LogWarning(ex, "Notification delivery failed for trip {TripId}", t.Id); }
}

public sealed class RideDb(DbContextOptions<RideDb> options) : DbContext(options)
{
    public DbSet<Trip> Trips => Set<Trip>(); public DbSet<Driver> Drivers => Set<Driver>();
    protected override void OnModelCreating(ModelBuilder m) { m.Entity<Trip>().HasIndex(t => new { t.PassengerId, t.CreatedAt }); m.Entity<Trip>().HasOne(t => t.Driver).WithMany().HasForeignKey(t => t.DriverId).OnDelete(DeleteBehavior.SetNull); m.Entity<Driver>().Property(d => d.Name).HasMaxLength(100); }
}
public sealed class Trip { public Guid Id { get; set; } = Guid.NewGuid(); public string PassengerId { get; set; } = ""; public string Pickup { get; set; } = ""; public string Destination { get; set; } = ""; public double PickupLatitude { get; set; } public double PickupLongitude { get; set; } public double DestinationLatitude { get; set; } public double DestinationLongitude { get; set; } public string Status { get; set; } = "Requested"; public Guid? DriverId { get; set; } public Driver? Driver { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class Driver { public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get; set; } = ""; public double Latitude { get; set; } public double Longitude { get; set; } public bool IsAvailable { get; set; } }
public sealed record CreateTrip(string PassengerId, string Pickup, string Destination, double PickupLatitude, double PickupLongitude, double DestinationLatitude, double DestinationLongitude);
public sealed record UpdateStatus(string Status);
public sealed record MatchRequest(Guid DriverId);
public sealed record MatchTripDto(Guid Id, string PassengerId, double PickupLatitude, double PickupLongitude);
public sealed record DriverDto(Guid Id, string Name, double Latitude, double Longitude, bool IsAvailable);
public sealed record TripDto(Guid Id, string PassengerId, string Pickup, string Destination, double PickupLatitude, double PickupLongitude, double DestinationLatitude, double DestinationLongitude, string Status, DriverDto? Driver, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed class RabbitPublisher(IConfiguration cfg, ILogger<RabbitPublisher> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1); private IConnection? _connection; private IChannel? _channel;
    public async Task PublishAsync(TripRequested message)
    {
        await _lock.WaitAsync(); try {
            if (_connection is null || !_connection.IsOpen) { var f = new ConnectionFactory { Uri = new Uri(cfg["RabbitMq:Uri"]!), ClientProvidedName = "ride-trip-api" }; _connection = await f.CreateConnectionAsync(); _channel = await f.CreateChannelAsync(); await _channel.QueueDeclareAsync("trip-requests", durable: true, exclusive: false, autoDelete: false); }
            await _channel!.BasicPublishAsync(exchange: "", routingKey: "trip-requests", body: JsonSerializer.SerializeToUtf8Bytes(message), mandatory: true); log.LogInformation("Queued trip {TripId}", message.TripId);
        } finally { _lock.Release(); }
    }
}
