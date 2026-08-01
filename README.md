# Signal Forge

Signal Forge is a web-based integration workbench for developing, testing, and
automating distributed messaging workflows across DDS, NATS, and gRPC.

The application is built with ASP.NET Core, Blazor, and MudBlazor. It provides a
single workspace for inspecting schemas, creating protocol sessions, publishing
and receiving messages, and assembling repeatable test scenarios.

## Highlights

- Dynamic gRPC service discovery, unary calls, and streaming workflows
- NATS connection, publish/subscribe, trigger, and scenario tooling
- RTI Connext DDS topic, QoS, DynamicData, session, and trigger workflows
- Schema-driven forms and structured message inspection
- Docker and offline-build support for controlled development environments
- DDS Ambassador integration for local Unix domain socket deployments

## Technology

- .NET 9 and ASP.NET Core
- Blazor Server and MudBlazor
- gRPC and Protocol Buffers
- NATS
- RTI Connext DDS 7.3.1
- Docker and Kubernetes

## Build and run

Build the standalone Signal Forge application:

```powershell
dotnet build .\ASAP.csproj --configuration Release
dotnet run --project .\ASAP.csproj
```

`ASAP.sln` also references optional DDS Ambassador projects through a sibling
checkout. Use the project-level command above when those projects are not
available beside this repository.

RTI Connext DDS support requires the appropriate RTI packages and a valid RTI
license for the target environment.

## Repository history

Signal Forge evolved from the
[`soseongha/GrpcWorkbench`](https://github.com/soseongha/GrpcWorkbench)
codebase. This repository preserves the original commit history and the work
developed on its `blazor` branch, including the subsequent DDS and NATS
integration work.

