# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/HealthSwitch.Api/HealthSwitch.Api.csproj src/HealthSwitch.Api/
RUN dotnet restore src/HealthSwitch.Api/HealthSwitch.Api.csproj
COPY src/ src/
RUN dotnet publish src/HealthSwitch.Api/HealthSwitch.Api.csproj -c Release -o /app --no-restore

# Run
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV PORT=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "HealthSwitch.Api.dll"]
