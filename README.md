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
- Service-persisted DDS XML profiles with create, clone, edit, import, and export workflows
- Structured topic and QoS editing, including direction, reliability, history, and durability
- Docker and offline-build support for controlled environments

## DDS XML profiles

The new-session screen seeds a `기본 DDSSim` profile from the same three-file
contract used by DDSClient: `DDSSim.xml`, `topics.xml`, and `qos_profiles.xml`.
Each profile stores those files separately so a user can keep multiple message
contracts without rebuilding Signal Forge. `topics.xml` names must match direct
`MSG` module structs and QoS references resolve through the fixed
`AmbassadorProfiles` library, matching DDSClient validation.
Each profile is stored as physical `DDSSim.xml`, `topics.xml`, and
`qos_profiles.xml` files under `data/dds-profiles/<profile-id>/`. The sibling
`data/dds-profiles.json` file contains only the profile catalog metadata and
revision. Profiles are shared across connected browsers and validated before
saving. Import selects, and export downloads, the exact three DDSClient XML
files directly; no Signal Forge-specific JSON or archive format is involved. Set
`DdsProfiles__StoragePath` to move the catalog; the profile directories use the
catalog filename without its extension.

The profile store keeps a `.bak` recovery copy, uses an inter-process lock, and limits profile/XML sizes.
For a shared deployment, set `AccessControl__Username` and `AccessControl__Password` to require HTTP Basic authentication.
Run one application replica per profile volume; use a database-backed store before scaling replicas across hosts.
The profile editor provides topic CRUD and common writer/reader QoS controls.
Each source file has its own raw XML tab. Advanced RTI policies remain available
in `qos_profiles.xml` and are preserved when structured changes are applied.

Message types are edited with structured forms instead of requiring full XML
editing. The editor covers the Connext 7.3 runtime type constructs used by
DynamicData: nested modules, constants, typedefs, enums, structs, unions,
primitive and referenced member types, bounded/unbounded strings and sequences,
multidimensional arrays, inheritance, extensibility, auto IDs, keys, optional
members, explicit member IDs, defaults, and numeric ranges. Existing annotations,
includes, directives, and other RTI-specific XML that is not modeled by the form
is preserved during round trips and remains available in the advanced XML tab.

The editor intentionally does not offer map, bitmask, or bitset as normal
Connext 7.3 message constructs because that runtime version does not support
them through the Extensible Types implementation, even though some names remain
in its XML schema for tooling or compatibility.

For Docker deployments, mount a persistent volume at `/app/data` so profiles
survive container replacement. Concurrent editors are protected by a revision
check; refresh the profile list before retrying if another user saved first.

An existing DDS session keeps the XML that was active when the session was
created as an immutable session snapshot. While a session is active, its source
profile cannot be edited or deleted; clone the profile to prepare a different
configuration. Close the active sessions before changing the original profile,
then create a new session to apply the changed XML and QoS.

Session creation rechecks the selected profile against the service store so a
stale browser cannot start DDS with a profile that another user has changed or
deleted in the meantime.

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
