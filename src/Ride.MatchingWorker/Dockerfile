FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Ride.Contracts/Ride.Contracts.csproj src/Ride.Contracts/
COPY src/Ride.MatchingWorker/Ride.MatchingWorker.csproj src/Ride.MatchingWorker/
RUN dotnet restore src/Ride.MatchingWorker/Ride.MatchingWorker.csproj
COPY src/ src/
RUN dotnet publish src/Ride.MatchingWorker/Ride.MatchingWorker.csproj -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Ride.MatchingWorker.dll"]
