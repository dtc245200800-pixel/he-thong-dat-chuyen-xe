FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Ride.Web/Ride.Web.csproj src/Ride.Web/
RUN dotnet restore src/Ride.Web/Ride.Web.csproj
COPY src/Ride.Web/ src/Ride.Web/
RUN dotnet publish src/Ride.Web/Ride.Web.csproj -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ride.Web.dll"]
