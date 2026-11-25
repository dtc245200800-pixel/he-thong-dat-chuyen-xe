using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ride.Contracts;

namespace Ride.MatchingWorker;

public sealed class Worker(IConfiguration config, IHttpClientFactory clients, ILogger<Worker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory { Uri = new Uri(config["RabbitMq:Uri"]!), ClientProvidedName = "ride-matching-worker", AutomaticRecoveryEnabled = false };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync("trip-requests", durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
                await channel.BasicQosAsync(0, 1, false, stoppingToken);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    try
                    {
                        var request = JsonSerializer.Deserialize<TripRequested>(ea.Body.Span);
                        if (request is null) { await channel.BasicAckAsync(ea.DeliveryTag, false, stoppingToken); return; }
                        await MatchAsync(request.TripId, stoppingToken);
                        await channel.BasicAckAsync(ea.DeliveryTag, false, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Matching failed; request will be retried");
                        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                        await channel.BasicNackAsync(ea.DeliveryTag, false, true, stoppingToken);
                    }
                };
                await channel.BasicConsumeAsync("trip-requests", autoAck: false, consumer, stoppingToken);
                log.LogInformation("Listening for trip requests");
                while (!stoppingToken.IsCancellationRequested && connection.IsOpen && channel.IsOpen)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                if (!stoppingToken.IsCancellationRequested) throw new IOException("RabbitMQ connection closed; reconnecting.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Queue connection failed; reconnecting in 5 seconds"); await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        }
    }

    private async Task MatchAsync(Guid tripId, CancellationToken ct)
    {
        var api = clients.CreateClient("api");
        var trip = await api.GetFromJsonAsync<MatchTripDto>($"/internal/trips/{tripId}", ct);
        if (trip is null) return;
        var drivers = await api.GetFromJsonAsync<List<DriverDto>>("/internal/drivers/available", ct) ?? [];
        var driver = drivers.OrderBy(d => DistanceKm(trip.PickupLatitude, trip.PickupLongitude, d.Latitude, d.Longitude)).FirstOrDefault();
        if (driver is null)
        {
            await api.PostAsync($"/internal/trips/{tripId}/unmatched", content: null, ct);
            log.LogWarning("No driver available for trip {TripId}", tripId); return;
        }
        var response = await api.PostAsJsonAsync($"/internal/trips/{tripId}/match", new MatchRequest(driver.Id), ct);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 409) response.EnsureSuccessStatusCode();
        log.LogInformation("Selected driver {DriverId} for trip {TripId}; result {Status}", driver.Id, tripId, response.StatusCode);
    }

    private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371; var dLat = ToRad(lat2 - lat1); var dLon = ToRad(lon2 - lon1);
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
    private static double ToRad(double degrees) => degrees * Math.PI / 180;
    private sealed record MatchTripDto(Guid Id, string PassengerId, double PickupLatitude, double PickupLongitude);
    private sealed record DriverDto(Guid Id, string Name, double Latitude, double Longitude, bool IsAvailable);
    private sealed record MatchRequest(Guid DriverId);
}
