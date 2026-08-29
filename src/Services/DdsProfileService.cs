using System.Text.Json;
using System.Xml.Linq;
using ASAP.Dds;
using ASAP.Models.Dds;
using ASAP.Models.Session;

namespace ASAP.Services;

public sealed class DdsProfileService
{
    private const int MaxProfiles = 100;
    private const int MaxDdsSimXmlChars = 16 * 1024 * 1024;
    private const int MaxTopicsXmlChars = 4 * 1024 * 1024;
    private const int MaxQosProfilesXmlChars = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DdsProfileService> _logger;
    private readonly IDdsSessionService _sessions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storagePath;
    private readonly string _profilesRoot;

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
        _profilesRoot = Path.Combine(
            Path.GetDirectoryName(_storagePath) ?? environment.ContentRootPath,
            Path.GetFileNameWithoutExtension(_storagePath));
    }

    public async Task<DdsProfileCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken);
            if (File.Exists(_storagePath))
            {
                var requiresMigration = await StorageNeedsMigrationAsync(cancellationToken);
                var loaded = await ReadCatalogAsync(cancellationToken);
                if (requiresMigration)
                {
                    await WriteCatalogAsync(loaded, cancellationToken);
                    _logger.LogInformation("Migrated DDS profile store to three-file DDSClient contract at {StoragePath}", _storagePath);
                }
                return loaded;
            }

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
            DdsSimXml = await LoadSampleXmlAsync("DDSSim.xml", cancellationToken),
            TopicsXml = await LoadSampleXmlAsync("topics.xml", cancellationToken),
            QosProfilesXml = await LoadSampleXmlAsync("qos_profiles.xml", cancellationToken),
        };

        return new DdsProfileCatalog
        {
            Profiles = [profile],
        };
    }

    public static DdsProfileValidationResult Validate(DdsXmlProfile profile)
    {
        MigrateLegacyProfile(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("프로필 이름을 입력하세요.");
        if (profile.DdsSimXml.Length > MaxDdsSimXmlChars)
            throw new InvalidOperationException($"DDSSim.xml은 {MaxDdsSimXmlChars / 1024 / 1024}MB를 초과할 수 없습니다.");
        if (profile.TopicsXml.Length > MaxTopicsXmlChars)
            throw new InvalidOperationException($"topics.xml은 {MaxTopicsXmlChars / 1024 / 1024}MB를 초과할 수 없습니다.");
        if (profile.QosProfilesXml.Length > MaxQosProfilesXmlChars)
            throw new InvalidOperationException($"qos_profiles.xml은 {MaxQosProfilesXmlChars / 1024 / 1024}MB를 초과할 수 없습니다.");

        var ddsSimDocument = XDocument.Parse(profile.DdsSimXml);
        XDocument.Parse(profile.TopicsXml);
        XDocument.Parse(profile.QosProfilesXml);
        if (ddsSimDocument.Root?.Name.LocalName != "dds")
            throw new InvalidOperationException("DDSSim.xml root는 <dds>여야 합니다.");
        var msgModule = ddsSimDocument.Root.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "module" &&
                                       string.Equals(element.Attribute("name")?.Value, "MSG", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("DDSSim.xml에 <module name=\"MSG\">가 필요합니다.");
        var msgStructNames = msgModule.Elements()
            .Where(element => element.Name.LocalName == "struct")
            .Select(element => element.Attribute("name")?.Value?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
        if (msgStructNames.Count == 0)
            throw new InvalidOperationException("DDSSim.xml의 MSG 모듈에 struct를 하나 이상 정의하세요.");
        DdsTypeProfileEditor.ValidateState(DdsTypeProfileEditor.Parse(profile.DdsSimXml));

        var types = DdsTypeParser.Parse(profile.DdsSimXml);
        var distinctTypes = types.Values
            .DistinctBy(type => type.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctTypes.Count == 0)
            throw new InvalidOperationException("타입 XML에서 enum 또는 struct 정의를 찾지 못했습니다.");

        var config = DdsConfigParser.Parse(profile.TopicsXml, profile.QosProfilesXml);
        if (config.Topics.Count == 0)
            throw new InvalidOperationException("토픽/QoS XML에서 사용할 토픽을 찾지 못했습니다.");

        var missingTypes = config.Topics
            .Where(topic => !types.ContainsKey(topic.TypeName))
            .Select(topic => topic.TypeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingTypes.Count > 0)
            throw new InvalidOperationException($"타입 XML에 없는 토픽 타입: {string.Join(", ", missingTypes)}");

        var topicNames = config.Topics.Select(topic => topic.TopicName).ToHashSet(StringComparer.Ordinal);
        var missingTopics = msgStructNames.Where(name => !topicNames.Contains(name)).OrderBy(name => name).ToList();
        if (missingTopics.Count > 0)
            throw new InvalidOperationException($"topics.xml에 없는 MSG struct: {string.Join(", ", missingTopics)}");

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

    private async Task<DdsProfileCatalog> ReadCatalogFileAsync(string path, CancellationToken cancellationToken)
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
        await LoadProfileFilesAsync(catalog, cancellationToken);
        return Clone(catalog);
    }

    private async Task WriteCatalogAsync(DdsProfileCatalog catalog, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_storagePath)
                        ?? throw new InvalidOperationException("DDS 프로필 저장 경로가 올바르지 않습니다.");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(_profilesRoot);
        foreach (var profile in catalog.Profiles)
            await WriteProfileFilesAsync(profile, cancellationToken);

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
                var manifest = new
                {
                    catalog.Version,
                    catalog.Revision,
                    Profiles = catalog.Profiles.Select(profile => new
                    {
                        profile.Id,
                        profile.Name,
                        profile.UpdatedAtUtc,
                    }),
                };
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
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

    private async Task LoadProfileFilesAsync(DdsProfileCatalog catalog, CancellationToken cancellationToken)
    {
        foreach (var profile in catalog.Profiles)
        {
            if (!string.IsNullOrWhiteSpace(profile.DdsSimXml) &&
                !string.IsNullOrWhiteSpace(profile.TopicsXml) &&
                !string.IsNullOrWhiteSpace(profile.QosProfilesXml))
                continue;

            var profileDirectory = ProfileDirectory(profile.Id);
            var ddsSimPath = Path.Combine(profileDirectory, "DDSSim.xml");
            var topicsPath = Path.Combine(profileDirectory, "topics.xml");
            var qosPath = Path.Combine(profileDirectory, "qos_profiles.xml");
            if (!File.Exists(ddsSimPath) || !File.Exists(topicsPath) || !File.Exists(qosPath))
                throw new InvalidOperationException($"프로필 '{profile.Name}'의 DDS 정의 3파일이 없습니다: {profileDirectory}");
            profile.DdsSimXml = await File.ReadAllTextAsync(ddsSimPath, cancellationToken);
            profile.TopicsXml = await File.ReadAllTextAsync(topicsPath, cancellationToken);
            profile.QosProfilesXml = await File.ReadAllTextAsync(qosPath, cancellationToken);
        }
    }

    private async Task WriteProfileFilesAsync(DdsXmlProfile profile, CancellationToken cancellationToken)
    {
        var profileDirectory = ProfileDirectory(profile.Id);
        Directory.CreateDirectory(profileDirectory);
        await WriteTextAtomicAsync(Path.Combine(profileDirectory, "DDSSim.xml"), profile.DdsSimXml, cancellationToken);
        await WriteTextAtomicAsync(Path.Combine(profileDirectory, "topics.xml"), profile.TopicsXml, cancellationToken);
        await WriteTextAtomicAsync(Path.Combine(profileDirectory, "qos_profiles.xml"), profile.QosProfilesXml, cancellationToken);
    }

    private static async Task WriteTextAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private string ProfileDirectory(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) ||
            profileId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidOperationException($"DDS 프로필 ID가 파일 경로에 안전하지 않습니다: {profileId}");
        return Path.Combine(_profilesRoot, profileId);
    }

    private async Task<bool> StorageNeedsMigrationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                _storagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() < 2)
                return true;
            if (!root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
                return false;
            return profiles.EnumerateArray().Any(profile =>
                profile.TryGetProperty("typesXml", out _) ||
                profile.TryGetProperty("configXml", out _) ||
                profile.TryGetProperty("ddsSimXml", out _) ||
                profile.TryGetProperty("topicsXml", out _) ||
                profile.TryGetProperty("qosProfilesXml", out _));
        }
        catch (JsonException)
        {
            return false;
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

    private async Task<string> LoadSampleXmlAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = SamplePath(fileName);
        if (!File.Exists(path))
            throw new InvalidOperationException($"기본 DDS 정의 파일을 찾지 못했습니다: {path}");
        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    private string SamplePath(string fileName)
        => Path.Combine(_environment.ContentRootPath, "samples", "dds", fileName);

    private static void Normalize(DdsProfileCatalog catalog)
    {
        catalog.Version = 2;
        catalog.Profiles ??= [];
        catalog.Profiles.RemoveAll(profile => profile is null);

        foreach (var profile in catalog.Profiles)
        {
            MigrateLegacyProfile(profile);
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

    private static void MigrateLegacyProfile(DdsXmlProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.DdsSimXml) && !string.IsNullOrWhiteSpace(profile.LegacyTypesXml))
            profile.DdsSimXml = profile.LegacyTypesXml;

        if ((!string.IsNullOrWhiteSpace(profile.TopicsXml) && !string.IsNullOrWhiteSpace(profile.QosProfilesXml)) ||
            string.IsNullOrWhiteSpace(profile.LegacyConfigXml))
        {
            profile.LegacyTypesXml = null;
            profile.LegacyConfigXml = null;
            return;
        }

        var legacy = XDocument.Parse(profile.LegacyConfigXml);
        var root = legacy.Root ?? throw new InvalidOperationException("기존 토픽/QoS XML root가 없습니다.");
        var topics = root.Name.LocalName == "topics"
            ? root
            : root.Elements().FirstOrDefault(element => element.Name.LocalName == "topics");
        var dds = root.Name.LocalName == "dds"
            ? root
            : root.Elements().FirstOrDefault(element => element.Name.LocalName == "dds");
        var library = dds?.Elements().FirstOrDefault(element => element.Name.LocalName == "qos_library");

        if (topics != null && string.IsNullOrWhiteSpace(profile.TopicsXml))
        {
            profile.TopicsXml = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement("topics", topics.Elements().Where(element => element.Name.LocalName == "topic").Select(ToDdsClientTopic)))
                .ToString();
        }
        if (library != null && string.IsNullOrWhiteSpace(profile.QosProfilesXml))
        {
            library.SetAttributeValue("name", DdsConfigParser.RequiredQosLibraryName);
            profile.QosProfilesXml = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement("dds", new XElement(library))).ToString();
        }

        profile.LegacyTypesXml = null;
        profile.LegacyConfigXml = null;
    }

    private static XElement ToDdsClientTopic(XElement source)
    {
        var name = source.Attribute("name")?.Value?.Trim()
                   ?? source.Elements().FirstOrDefault(element => element.Name.LocalName == "topic_name")?.Value.Trim()
                   ?? string.Empty;
        var qos = source.Attribute("qos_profile")?.Value?.Trim()
                  ?? source.Elements().FirstOrDefault(element => element.Name.LocalName == "qos_profile")?.Value.Trim()
                  ?? string.Empty;
        var direction = source.Attribute("direction")?.Value?.Trim()
                        ?? source.Elements().FirstOrDefault(element => element.Name.LocalName == "direction")?.Value.Trim()
                        ?? string.Empty;
        return new XElement("topic",
            new XAttribute("name", name),
            new XAttribute("qos_profile", qos),
            new XAttribute("direction", direction));
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
           && string.Equals(left.DdsSimXml, right.DdsSimXml, StringComparison.Ordinal)
           && string.Equals(left.TopicsXml, right.TopicsXml, StringComparison.Ordinal)
           && string.Equals(left.QosProfilesXml, right.QosProfilesXml, StringComparison.Ordinal);

    private static DdsXmlProfile CloneProfile(DdsXmlProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        DdsSimXml = profile.DdsSimXml,
        TopicsXml = profile.TopicsXml,
        QosProfilesXml = profile.QosProfilesXml,
        UpdatedAtUtc = profile.UpdatedAtUtc,
    };

    private static DdsProfileCatalog Clone(DdsProfileCatalog catalog)
        => JsonSerializer.Deserialize<DdsProfileCatalog>(JsonSerializer.Serialize(catalog, JsonOptions), JsonOptions)
           ?? throw new InvalidOperationException("DDS 프로필 저장 데이터를 복제하지 못했습니다.");
}
