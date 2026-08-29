using System.Text.Json;
using System.Xml.Linq;
using ASAP.Dds;
using ASAP.Models.Dds;
using ASAP.Models.Session;

namespace ASAP.Services;

public sealed class DdsProfileService
{
    private const int MaxProfiles = 100;
    private const int MaxTypesXmlChars = 16 * 1024 * 1024;
    private const int MaxConfigXmlChars = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DdsProfileService> _logger;
    private readonly IDdsSessionService _sessions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storagePath;

    public DdsProfileService(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        IDdsSessionService sessions,
        ILogger<DdsProfileService> logger)
    {
        _environment = environment;
        _sessions = sessions;
        _logger = logger;

        var configuredPath = configuration["DdsProfiles:StoragePath"];
        var relativeOrAbsolutePath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine("data", "dds-profiles.json")
            : configuredPath.Trim();
        _storagePath = Path.GetFullPath(
            Path.IsPathRooted(relativeOrAbsolutePath)
                ? relativeOrAbsolutePath
                : Path.Combine(environment.ContentRootPath, relativeOrAbsolutePath));
    }

    public async Task<DdsProfileCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken);
            if (File.Exists(_storagePath))
                return await ReadCatalogAsync(cancellationToken);

            var seeded = await CreateDefaultCatalogAsync(cancellationToken);
            seeded.Revision = 1;
            await WriteCatalogAsync(seeded, cancellationToken);
            _logger.LogInformation("Seeded DDS profile store at {StoragePath}", _storagePath);
            return Clone(seeded);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DdsProfileCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken);
            var currentRevision = 0L;
            if (File.Exists(_storagePath))
            {
                var current = await ReadCatalogAsync(cancellationToken);
                currentRevision = current.Revision;
            }

            if (catalog.Revision != currentRevision)
            {
                throw new InvalidOperationException(
                    "다른 사용자가 DDS 프로필을 먼저 변경했습니다. 새로고침한 뒤 다시 편집하세요.");
            }

            Normalize(catalog);
            foreach (var profile in catalog.Profiles)
                Validate(profile);
            if (File.Exists(_storagePath))
            {
                var current = await ReadCatalogAsync(cancellationToken);
                EnsureActiveProfilesAreUnchanged(current, catalog);
            }

            catalog.Revision = currentRevision + 1;
            await WriteCatalogAsync(catalog, cancellationToken);
            _logger.LogInformation(
                "Saved {ProfileCount} DDS profiles at revision {Revision}",
                catalog.Profiles.Count,
                catalog.Revision);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DdsXmlProfile> LoadProfileSnapshotAsync(
        string profileId,
        DateTimeOffset expectedUpdatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken);
            if (!File.Exists(_storagePath))
                throw new InvalidOperationException("DDS 프로필 저장소가 아직 생성되지 않았습니다.");

            var catalog = await ReadCatalogAsync(cancellationToken);
            var profile = catalog.Profiles.FirstOrDefault(item => item.Id == profileId)
                          ?? throw new InvalidOperationException("선택한 DDS 프로필이 삭제되었습니다. 새로고침하세요.");
            if (profile.UpdatedAtUtc != expectedUpdatedAtUtc)
            {
                throw new InvalidOperationException(
                    "선택한 DDS 프로필이 변경되었습니다. 최신 설정을 확인할 수 있도록 새로고침한 뒤 세션을 생성하세요.");
            }

            Validate(profile);
            return CloneProfile(profile);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DdsSession> CreateSessionAsync(
        string profileId,
        DateTimeOffset expectedUpdatedAtUtc,
        Func<DdsXmlProfile, DdsSession> factory,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken);
            var catalog = await ReadCatalogAsync(cancellationToken);
            var profile = catalog.Profiles.FirstOrDefault(item => item.Id == profileId)
                          ?? throw new InvalidOperationException("선택한 DDS 프로필이 삭제되었습니다. 새로고침하세요.");
            if (profile.UpdatedAtUtc != expectedUpdatedAtUtc)
                throw new InvalidOperationException("선택한 DDS 프로필이 변경되었습니다. 새로고침한 뒤 세션을 생성하세요.");
            Validate(profile);
            return factory(CloneProfile(profile));
        }
        finally { _gate.Release(); }
    }

    public async Task<DdsProfileCatalog> CreateDefaultCatalogAsync(CancellationToken cancellationToken = default)
    {
        var profile = new DdsXmlProfile
        {
            Name = "기본 DDSSim",
            TypesXml = await LoadDefaultTypesXmlAsync(cancellationToken),
            ConfigXml = await LoadDefaultConfigXmlAsync(cancellationToken),
        };

        return new DdsProfileCatalog
        {
            Profiles = [profile],
        };
    }

    public static DdsProfileValidationResult Validate(DdsXmlProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("프로필 이름을 입력하세요.");
        if (profile.TypesXml.Length > MaxTypesXmlChars)
            throw new InvalidOperationException($"타입 XML은 {MaxTypesXmlChars / 1024 / 1024}MB를 초과할 수 없습니다.");
        if (profile.ConfigXml.Length > MaxConfigXmlChars)
            throw new InvalidOperationException($"토픽/QoS XML은 {MaxConfigXmlChars / 1024 / 1024}MB를 초과할 수 없습니다.");

        XDocument.Parse(profile.TypesXml);
        XDocument.Parse(profile.ConfigXml);
        DdsTypeProfileEditor.ValidateState(DdsTypeProfileEditor.Parse(profile.TypesXml));

        var types = DdsTypeParser.Parse(profile.TypesXml);
        var distinctTypes = types.Values
            .DistinctBy(type => type.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctTypes.Count == 0)
            throw new InvalidOperationException("타입 XML에서 enum 또는 struct 정의를 찾지 못했습니다.");

        var config = DdsConfigParser.Parse(profile.ConfigXml);
        if (config.Topics.Count == 0)
            throw new InvalidOperationException("토픽/QoS XML에서 사용할 토픽을 찾지 못했습니다.");

        var missingTypes = config.Topics
            .Where(topic => !types.ContainsKey(topic.TypeName))
            .Select(topic => topic.TypeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingTypes.Count > 0)
            throw new InvalidOperationException($"타입 XML에 없는 토픽 타입: {string.Join(", ", missingTypes)}");

        var qosNames = config.QosProfileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingQos = config.Topics
            .Where(topic => !qosNames.Contains(topic.QosProfileName))
            .Select(topic => topic.QosProfileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingQos.Count > 0)
            throw new InvalidOperationException($"QoS XML에 없는 프로필: {string.Join(", ", missingQos)}");

        return new DdsProfileValidationResult(distinctTypes.Count, config.Topics.Count, config.QosProfileNames.Count);
    }

    private async Task<DdsProfileCatalog> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCatalogFileAsync(_storagePath, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            var backupPath = _storagePath + ".bak";
            if (File.Exists(backupPath))
            {
                _logger.LogWarning(ex, "DDS 프로필 저장 파일 손상, 백업으로 복구: {BackupPath}", backupPath);
                return await ReadCatalogFileAsync(backupPath, cancellationToken);
            }
            throw new InvalidOperationException($"DDS 프로필 저장 파일이 손상되었습니다: {_storagePath}", ex);
        }
    }

    private static async Task<DdsProfileCatalog> ReadCatalogFileAsync(string path, CancellationToken cancellationToken)
    {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var catalog = await JsonSerializer.DeserializeAsync<DdsProfileCatalog>(stream, JsonOptions, cancellationToken)
                          ?? throw new InvalidOperationException("DDS 프로필 저장 파일이 비어 있습니다.");
            Normalize(catalog);
            if (catalog.Profiles.Count == 0)
                throw new InvalidOperationException("DDS 프로필 저장 파일에 프로필이 없습니다.");
            return Clone(catalog);
    }

    private async Task WriteCatalogAsync(DdsProfileCatalog catalog, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_storagePath)
                        ?? throw new InvalidOperationException("DDS 프로필 저장 경로가 올바르지 않습니다.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_storagePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(_storagePath))
                File.Copy(_storagePath, _storagePath + ".bak", overwrite: true);
            File.Move(temporaryPath, _storagePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task<FileStream> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_storagePath) ?? _environment.ContentRootPath;
        Directory.CreateDirectory(directory);
        var lockPath = _storagePath + ".lock";
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 100)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private async Task<string> LoadDefaultTypesXmlAsync(CancellationToken cancellationToken)
    {
        var path = SamplePath("DDSSim.xml");
        if (File.Exists(path))
            return await File.ReadAllTextAsync(path, cancellationToken);

        return """
<?xml version="1.0" encoding="UTF-8"?>
<dds>
  <types>
    <module name="STRUCT">
      <struct name="Position8">
        <member name="X" type="float64"/>
        <member name="Y" type="float64"/>
      </struct>
    </module>
  </types>
</dds>
""";
    }

    private async Task<string> LoadDefaultConfigXmlAsync(CancellationToken cancellationToken)
    {
        var qosPath = SamplePath("qos_profiles.xml");
        var topicsPath = SamplePath("topics.xml");
        if (File.Exists(qosPath) && File.Exists(topicsPath))
        {
            var qosElement = XElement.Parse(await File.ReadAllTextAsync(qosPath, cancellationToken));
            var topicsElement = XElement.Parse(await File.ReadAllTextAsync(topicsPath, cancellationToken));
            var normalizedTopics = new XElement("topics",
                topicsElement.Elements("topic")
                    .Select(ToConfigTopic)
                    .Where(topic => topic is not null)!);

            return new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement("dds-ambassador-config",
                    new XElement("dds", qosElement),
                    normalizedTopics)).ToString();
        }

        return """
<?xml version="1.0" encoding="UTF-8"?>
<dds-ambassador-config>
  <dds>
    <qos_library name="AmbassadorProfiles">
      <qos_profile name="DefaultProfile" is_default_qos="true"/>
    </qos_library>
  </dds>
  <topics>
    <topic>
      <topic_name>PositionTopic</topic_name>
      <type_name>STRUCT::Position8</type_name>
      <direction>Both</direction>
      <qos_profile>DefaultProfile</qos_profile>
    </topic>
  </topics>
</dds-ambassador-config>
""";
    }

    private string SamplePath(string fileName)
        => Path.Combine(_environment.ContentRootPath, "samples", "dds", fileName);

    private static XElement? ToConfigTopic(XElement topic)
    {
        var topicName = topic.Attribute("name")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(topicName))
            return null;

        var qosProfile = topic.Attribute("qos_profile")?.Value?.Trim();
        var direction = topic.Attribute("direction")?.Value?.Trim();
        return new XElement("topic",
            new XElement("topic_name", topicName),
            new XElement("type_name", $"MSG::{topicName}"),
            new XElement("direction", string.IsNullOrWhiteSpace(direction) ? "Both" : direction),
            new XElement("qos_profile", string.IsNullOrWhiteSpace(qosProfile) ? "ReliableRealtime" : qosProfile));
    }

    private static void Normalize(DdsProfileCatalog catalog)
    {
        catalog.Version = 1;
        catalog.Profiles ??= [];
        catalog.Profiles.RemoveAll(profile => profile is null);

        foreach (var profile in catalog.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
                profile.Id = Guid.NewGuid().ToString("N");
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "DDS 프로필" : profile.Name.Trim();
        }
        if (catalog.Profiles.Count > MaxProfiles)
            throw new InvalidOperationException($"DDS 프로필은 최대 {MaxProfiles}개까지 저장할 수 있습니다.");
        var duplicateId = catalog.Profiles.GroupBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateId != null) throw new InvalidOperationException($"중복 DDS 프로필 ID가 있습니다: {duplicateId.Key}");
        var duplicateName = catalog.Profiles.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateName != null) throw new InvalidOperationException($"중복 DDS 프로필 이름이 있습니다: {duplicateName.Key}");
    }

    private void EnsureActiveProfilesAreUnchanged(DdsProfileCatalog current, DdsProfileCatalog proposed)
    {
        foreach (var currentProfile in current.Profiles)
        {
            var activeSessions = _sessions.GetByProfileId(currentProfile.Id);
            if (activeSessions.Count == 0)
                continue;

            var proposedProfile = proposed.Profiles.FirstOrDefault(profile => profile.Id == currentProfile.Id);
            if (proposedProfile == null || !ProfileContentEquals(currentProfile, proposedProfile))
            {
                throw new InvalidOperationException(
                    $"프로필 '{currentProfile.Name}'은(는) 활성 DDS 세션 {activeSessions.Count}개에서 사용 중이라 수정하거나 삭제할 수 없습니다. 세션을 닫거나 프로필을 복제해 편집하세요.");
            }
        }
    }

    private static bool ProfileContentEquals(DdsXmlProfile left, DdsXmlProfile right)
        => string.Equals(left.Name, right.Name, StringComparison.Ordinal)
           && string.Equals(left.TypesXml, right.TypesXml, StringComparison.Ordinal)
           && string.Equals(left.ConfigXml, right.ConfigXml, StringComparison.Ordinal);

    private static DdsXmlProfile CloneProfile(DdsXmlProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        TypesXml = profile.TypesXml,
        ConfigXml = profile.ConfigXml,
        UpdatedAtUtc = profile.UpdatedAtUtc,
    };

    private static DdsProfileCatalog Clone(DdsProfileCatalog catalog)
        => JsonSerializer.Deserialize<DdsProfileCatalog>(JsonSerializer.Serialize(catalog, JsonOptions), JsonOptions)
           ?? throw new InvalidOperationException("DDS 프로필 저장 데이터를 복제하지 못했습니다.");
}
