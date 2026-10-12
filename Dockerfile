# From the parent directory: docker build -f Signal-Forge/Dockerfile -t signal-forge:ddsclient .
# Context is filtered by Dockerfile.dockerignore. No credentials or running app data are copied.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS sources
WORKDIR /src/Signal-Forge
COPY Signal-Forge/ /src/Signal-Forge/
COPY DDSClient/DDS_Nuget/ /src/DDSClient/DDS_Nuget/
COPY DDSClient/definitions/ /src/DDSClient/definitions/
ARG DDS_MODULE_SELECTION=

FROM sources AS build
RUN dotnet publish ASAP.csproj -c Release -o /app/publish --configfile /src/Signal-Forge/nuget.config \
    -p:DirectoryBuildPropsPath=/src/Signal-Forge/eng/Integration.Build.props -p:DdsModuleSelectionFile="$DDS_MODULE_SELECTION" /p:UseAppHost=false

FROM sources AS test-build
RUN dotnet publish tests/SignalForge.DdsIntegrationTests/SignalForge.DdsIntegrationTests.csproj -c Release -o /app/tests \
    --configfile /src/Signal-Forge/nuget.config -p:DirectoryBuildPropsPath=/src/Signal-Forge/eng/Integration.Build.props -p:DdsModuleSelectionFile="$DDS_MODULE_SELECTION" /p:UseAppHost=false

# Dedicated short-lived test image: no web service, published ports, or persistent app volume.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS integration-tests
WORKDIR /app
COPY --from=test-build /app/tests/ ./
ENTRYPOINT ["dotnet", "SignalForge.DdsIntegrationTests.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:5226
EXPOSE 5226
VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "ASAP.dll"]
