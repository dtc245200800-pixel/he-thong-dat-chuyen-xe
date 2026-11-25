FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Ride.Contracts/Ride.Contracts.csproj src/Ride.Contracts/
COPY src/Ride.Notifications/Ride.Notifications.csproj src/Ride.Notifications/
RUN dotnet restore src/Ride.Notifications/Ride.Notifications.csproj
COPY src/ src/
RUN dotnet publish src/Ride.Notifications/Ride.Notifications.csproj -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ride.Notifications.dll"]
