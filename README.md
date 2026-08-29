# Signal Forge DDS

Signal Forge is a web-based RTI Connext DDS workbench built with ASP.NET Core,
Blazor, and MudBlazor. It provides one workspace for creating DDS sessions,
inspecting XML-defined types and topics, publishing and receiving DynamicData,
and assembling repeatable trigger and scenario workflows.

## Highlights

- RTI Connext DDS domain, transport, topic, QoS, and DynamicData workflows
- DDS publication, subscription, and structured sample inspection
- Schema-driven payload forms and JSON views
- Periodic, bulk, and receive-driven DDS triggers
- Repeatable DDS scenarios with import and export
- Docker and offline-build support for controlled environments

## Technology

- .NET 9 and ASP.NET Core
- Blazor Server and MudBlazor
- RTI Connext DDS 7.3.1
- Docker

## Build and run

```powershell
dotnet build .\ASAP.csproj --configuration Release
dotnet run --project .\ASAP.csproj
```

RTI Connext DDS support requires the appropriate RTI packages and a valid RTI
license for the target environment.
