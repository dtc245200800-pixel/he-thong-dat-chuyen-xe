FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Ride.Contracts/Ride.Contracts.csproj src/Ride.Contracts/
COPY src/Ride.Api/Ride.Api.csproj src/Ride.Api/
RUN dotnet restore src/Ride.Api/Ride.Api.csproj
COPY src/ src/
RUN dotnet publish src/Ride.Api/Ride.Api.csproj -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ride.Api.dll"]
