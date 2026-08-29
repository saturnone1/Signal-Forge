using ASAP.Models.Dds;

namespace ASAP.Services;

/// <summary>
/// Defines the exact three-file DDS profile contract shared with DDSClient.
/// </summary>
public static class DdsProfileFiles
{
    public const string DdsSimFileName = "DDSSim.xml";
    public const string TopicsFileName = "topics.xml";
    public const string QosProfilesFileName = "qos_profiles.xml";

    public static readonly IReadOnlyList<string> RequiredFileNames =
    [
        DdsSimFileName,
        TopicsFileName,
        QosProfilesFileName,
    ];

    public static DdsXmlProfile CreateProfile(string profileName, IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var unexpected = files.Keys.Where(fileName => !RequiredFileNames.Contains(fileName, StringComparer.Ordinal)).ToList();
        if (unexpected.Count > 0)
            throw new InvalidOperationException($"허용되지 않은 DDS 프로필 파일: {string.Join(", ", unexpected)}");
        var missing = RequiredFileNames.Where(fileName => !files.ContainsKey(fileName)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"필요한 DDS 프로필 파일이 없습니다: {string.Join(", ", missing)}");

        var profile = new DdsXmlProfile
        {
            Name = string.IsNullOrWhiteSpace(profileName) ? "가져온 DDS 프로필" : profileName.Trim(),
            DdsSimXml = files[DdsSimFileName],
            TopicsXml = files[TopicsFileName],
            QosProfilesXml = files[QosProfilesFileName],
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        DdsProfileService.Validate(profile);
        return profile;
    }
}
