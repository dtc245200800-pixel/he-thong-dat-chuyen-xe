namespace Ride.Contracts;

public sealed record TripRequested(Guid TripId);
public sealed record TripStatusChanged(Guid TripId, string PassengerId, string Status, Guid? DriverId, string? DriverName, DateTimeOffset ChangedAt, string? Message = null);
