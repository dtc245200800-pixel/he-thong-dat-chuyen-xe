using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ride.Contracts;

public sealed class OutboxDispatcher(IServiceScopeFactory scopes, RabbitPublisher publisher, IHttpClientFactory clients, ILogger<OutboxDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchBatch(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Outbox poll failed; the dispatcher will retry");
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task DispatchBatch(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RideDb>();
        var now = DateTimeOffset.UtcNow;
        var messages = await db.OutboxMessages.Where(x => x.ProcessedAt == null && x.NextAttemptAt <= now).OrderBy(x => x.CreatedAt).Take(20).ToListAsync(ct);
        foreach (var message in messages)
        {
            try
            {
                switch (message.Type)
                {
                    case "TripRequested":
                        var tripRequest = JsonSerializer.Deserialize<TripRequested>(message.Payload) ?? throw new InvalidOperationException("Invalid TripRequested outbox payload.");
                        await publisher.PublishAsync(tripRequest);
                        break;
                    case "TripStatusChanged":
                        var statusEvent = JsonSerializer.Deserialize<TripStatusChanged>(message.Payload) ?? throw new InvalidOperationException("Invalid TripStatusChanged outbox payload.");
                        var response = await clients.CreateClient("notifications").PostAsJsonAsync("/internal/events", statusEvent, ct);
                        response.EnsureSuccessStatusCode();
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown outbox message type: {message.Type}");
                }
                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.LastError = null;
                log.LogInformation("Dispatched outbox message {MessageId} ({MessageType})", message.Id, message.Type);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.AttemptCount++;
                var delaySeconds = Math.Min(60, Math.Pow(2, Math.Min(message.AttemptCount, 6)));
                message.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
                message.LastError = ex.Message.Length <= 2000 ? ex.Message : ex.Message[..2000];
                log.LogWarning(ex, "Outbox message {MessageId} failed on attempt {Attempt}; retry in {DelaySeconds}s", message.Id, message.AttemptCount, delaySeconds);
            }
        }
        if (messages.Count > 0) await db.SaveChangesAsync(ct);
    }
}
