using DdsAmbassador.DDSClient;
namespace DdsAmbassador.Messages;
// 앱 재빌드 없이 같은 계약의 새 모듈 DLL이 선택되는지 확인하는 독립 빌드 대역이다.
public static class TelemetryMessages
{
    public static DdsMessageModule Module { get; } = new("Telemetry", "replacement-probe",
        new Dictionary<string, Type> { ["Heartbeat"] = typeof(DdsModules_Telemetry.Telemetry.Heartbeat) },
        DdsModules_Telemetry.ModuleContract.SchemaSha256);
}
