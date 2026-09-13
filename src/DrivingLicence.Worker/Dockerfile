FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/DrivingLicence.Worker/DrivingLicence.Worker.csproj -c Release -o /app
# ServiceDefaults uses ASP.NET Core health-check/OpenTelemetry assemblies, so the
# Worker needs the shared Microsoft.AspNetCore.App framework at runtime.
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "DrivingLicence.Worker.dll"]
