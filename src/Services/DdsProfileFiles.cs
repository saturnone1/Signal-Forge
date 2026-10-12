using ASAP.Models.Dds;
using ASAP.Dds;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASAP.Services;

/// <summary>토픽 목록은 JSON 설정으로, QoS는 외부 XML로 관리한다.</summary>
public static class DdsProfileFiles
{
    public const string DdsSimFileName = "DDSSim.xml"; // 구형 저장 경로의 계약 파일 이름
    public const string ConnectionFileName = "connection.json";
    public const string QosProfilesFileName = "qos_profiles.xml";
    public static readonly IReadOnlyList<string> RequiredFileNames = [ConnectionFileName, QosProfilesFileName];
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() },
    };
    public sealed record ConnectionSettings(string? ModuleName, string? QosLibraryName,
        string? ParticipantQosProfile, List<DdsTopicConfig> Topics);

    public static string ExportConnection(DdsXmlProfile profile) => JsonSerializer.Serialize(new ConnectionSettings(
        profile.ModuleName, profile.QosLibraryName, profile.ParticipantQosProfile,
        DdsTopicSettings.Read(profile.TopicSettingsJson)), Options);

    public static void ApplyConnection(DdsXmlProfile profile, string json)
    {
        var settings = JsonSerializer.Deserialize<ConnectionSettings>(json, Options)
            ?? throw new InvalidOperationException("연결 설정이 없습니다.");
        if (settings.Topics is null) throw new InvalidOperationException("토픽 목록이 없습니다.");
        profile.ModuleName = settings.ModuleName;
        profile.QosLibraryName = settings.QosLibraryName;
        profile.ParticipantQosProfile = settings.ParticipantQosProfile;
        profile.TopicSettingsJson = DdsTopicSettings.Write(settings.Topics);
    }

    public static DdsXmlProfile CreateProfile(string profileName, IReadOnlyDictionary<string, string> files)
    {
        foreach (var name in RequiredFileNames)
            if (!files.ContainsKey(name)) throw new InvalidOperationException("필요한 파일이 없습니다: " + name);
        var profile = new DdsXmlProfile
        {
            Name = string.IsNullOrWhiteSpace(profileName) ? "가져온 DDS 프로필" : profileName.Trim(),
            QosProfilesXml = files[QosProfilesFileName],
        };
        ApplyConnection(profile, files[ConnectionFileName]);
        profile.DdsSimXml = BuiltMessageRegistry.Get(profile.ModuleName ?? "").ContractXml;
        DdsProfileService.Validate(profile);
        return profile;
    }
}
