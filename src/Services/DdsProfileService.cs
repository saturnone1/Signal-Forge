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
    private const int MaxTopicSettingsJsonChars = 4 * 1024 * 1024;
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

        var originalRevision = catalog.Revision;
        var originalStorage = catalog.Profiles.Select(profile => (Profile: profile, profile.StorageRevision)).ToArray();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken);
            var currentRevision = 0L;
            DdsProfileCatalog? current = null;
            if (File.Exists(_storagePath))
            {
                current = await ReadCatalogAsync(cancellationToken);
                currentRevision = current.Revision;
            }

            if (catalog.Revision != currentRevision)
            {
                throw new InvalidOperationException(
                    "다른 사용자가 DDS 프로필을 먼저 변경했습니다. 새로고침한 뒤 다시 편집하세요.");
            }

            Normalize(catalog);
            foreach (var profile in catalog.Profiles)
            {
                var currentProfile = current?.Profiles.FirstOrDefault(item => item.Id == profile.Id);
                if (currentProfile == null || !ProfileContentEquals(currentProfile, profile))
                    Validate(profile);
            }
            if (current != null)
            {
                EnsureActiveProfilesAreUnchanged(current, catalog);
            }

            catalog.Revision = currentRevision + 1;
            await WriteCatalogAsync(catalog, cancellationToken, current);
            _logger.LogInformation(
                "Saved {ProfileCount} DDS profiles at revision {Revision}",
                catalog.Profiles.Count,
                catalog.Revision);
        }
        catch
        {
            catalog.Revision = originalRevision;
            foreach (var previous in originalStorage) previous.Profile.StorageRevision = previous.StorageRevision;
            throw;
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
        var qosXml = await LoadSampleXmlAsync(DdsProfileFiles.QosProfilesFileName, cancellationToken);
        var library = DdsConfigParser.SelectLibrary(XDocument.Parse(qosXml));
        var libraryName = (string)library.Attribute("name")!;
        var qosName = (string?)library.Elements("qos_profile").FirstOrDefault()?.Attribute("name")
            ?? throw new InvalidOperationException("기본 QoS 프로파일이 없습니다.");
        // 기본 프로필도 현재 빌드에 포함된 모듈로 만든다. DDSSim이 없는 배포도 초기화할 수 있다.
        return new DdsProfileCatalog { Profiles = BuiltMessageRegistry.Modules.Select(module => new DdsXmlProfile
        {
            Name = "기본 " + module.Module.Name,
            ModuleName = module.Module.Name,
            DdsSimXml = module.ContractXml,
            QosLibraryName = libraryName,
            ParticipantQosProfile = libraryName + "::" + qosName,
            QosProfilesXml = qosXml,
            TopicSettingsJson = DdsTopicSettings.Write(module.Module.Types.Select(type => new DdsTopicConfig
            {
                TopicName = type.Key, TypeName = BuiltMessageRegistry.WireTypeName(type.Value),
                QosProfileName = qosName, Direction = DdsTopicDirection.Both,
            })),
        }).ToList() };
    }

    public static DdsProfileValidationResult Validate(DdsXmlProfile profile)
    {
        MigrateLegacyProfile(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("프로필 이름을 입력하세요.");
        if (profile.DdsSimXml.Length > MaxDdsSimXmlChars)
            throw new InvalidOperationException($"DDSSim.xml은 {MaxDdsSimXmlChars / 1024 / 1024}MB를 초과할 수 없습니다.");
        if (profile.TopicSettingsJson.Length > MaxTopicSettingsJsonChars)
            throw new InvalidOperationException($"토픽 설정은 {MaxTopicSettingsJsonChars / 1024 / 1024}MB를 초과할 수 없습니다.");
        if (profile.QosProfilesXml.Length > MaxQosProfilesXmlChars)
            throw new InvalidOperationException($"qos_profiles.xml은 {MaxQosProfilesXmlChars / 1024 / 1024}MB를 초과할 수 없습니다.");

        var builtModule = BuiltMessageRegistry.Select(profile.DdsSimXml, profile.ModuleName);
        profile.ModuleName = builtModule.Module.Name;
        var ddsSimDocument = XDocument.Parse(profile.DdsSimXml);
        DdsTopicSettings.Read(profile.TopicSettingsJson);
        XDocument.Parse(profile.QosProfilesXml);
        if (ddsSimDocument.Root?.Name.LocalName != "dds")
            throw new InvalidOperationException("DDSSim.xml root는 <dds>여야 합니다.");
        // 컴파일된 계약과 일치함을 위에서 확인했다. 구형 구조 편집기의 제약을 다시 적용하지 않는다.

        var types = DdsTypeParser.Parse(profile.DdsSimXml);
        var distinctTypes = types.Values
            .DistinctBy(type => type.QualifiedName, StringComparer.Ordinal)
            .ToList();
        if (distinctTypes.Count == 0)
            throw new InvalidOperationException("타입 XML에서 enum 또는 struct 정의를 찾지 못했습니다.");

        var config = DdsConfigParser.Parse(profile.TopicSettingsJson, profile.QosProfilesXml, builtModule, profile.QosLibraryName);
        profile.QosLibraryName = config.QosLibraryName;
        if (!string.IsNullOrWhiteSpace(profile.ParticipantQosProfile))
        {
            var selected = profile.ParticipantQosProfile.Contains("::", StringComparison.Ordinal)
                ? profile.ParticipantQosProfile : config.QosLibraryName + "::" + profile.ParticipantQosProfile;
            if (!config.QosProfileNames.Any(name => selected == config.QosLibraryName + "::" + name))
                throw new InvalidOperationException("선택한 Participant QoS 프로파일이 없습니다: " + selected);
            profile.ParticipantQosProfile = selected;
        }
        if (config.Topics.Count == 0)
            throw new InvalidOperationException("토픽 설정에서 사용할 토픽을 찾지 못했습니다.");

        var missingTypes = config.Topics
            .Where(topic => !types.ContainsKey(topic.TypeName))
            .Select(topic => topic.TypeName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (missingTypes.Count > 0)
            throw new InvalidOperationException($"타입 XML에 없는 토픽 타입: {string.Join(", ", missingTypes)}");

        var qosNames = config.QosProfileNames.ToHashSet(StringComparer.Ordinal);
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
        Normalize(catalog);
        return Clone(catalog);
    }

    private async Task WriteCatalogAsync(
        DdsProfileCatalog catalog,
        CancellationToken cancellationToken,
        DdsProfileCatalog? previousCatalog = null)
    {
        var directory = Path.GetDirectoryName(_storagePath)
                        ?? throw new InvalidOperationException("DDS 프로필 저장 경로가 올바르지 않습니다.");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(_profilesRoot);
        var previousProfiles = previousCatalog?.Profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal)
                               ?? new Dictionary<string, DdsXmlProfile>(StringComparer.Ordinal);
        foreach (var profile in catalog.Profiles)
        {
            var filesExist = ProfileFilesExist(profile);
            if (!filesExist || !previousProfiles.TryGetValue(profile.Id, out var previous) ||
                !ProfileXmlContentEquals(previous, profile) || profile.StorageRevision is null)
            {
                await WriteProfileFilesAsync(profile, cancellationToken);
            }
            else profile.StorageRevision = previous.StorageRevision;
        }

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
                        profile.ModuleName,
                        profile.QosLibraryName,
                        profile.ParticipantQosProfile,
                        profile.StorageRevision,
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
                !string.IsNullOrWhiteSpace(profile.TopicSettingsJson) &&
                !string.IsNullOrWhiteSpace(profile.QosProfilesXml))
                continue;

            var profileDirectory = ProfileDirectory(profile.Id, profile.StorageRevision);
            var ddsSimPath = Path.Combine(profileDirectory, DdsProfileFiles.DdsSimFileName);
            var topicsPath = Path.Combine(profileDirectory, DdsProfileFiles.ConnectionFileName);
            var legacyTopicsPath = Path.Combine(profileDirectory, "topics.xml");
            var qosPath = Path.Combine(profileDirectory, DdsProfileFiles.QosProfilesFileName);
            if (!File.Exists(ddsSimPath) || (!File.Exists(topicsPath) && !File.Exists(legacyTopicsPath)) || !File.Exists(qosPath))
                throw new InvalidOperationException($"프로필 '{profile.Name}'의 DDS 정의 3파일이 없습니다: {profileDirectory}");
            profile.DdsSimXml = await File.ReadAllTextAsync(ddsSimPath, cancellationToken);
            if (File.Exists(topicsPath))
                DdsProfileFiles.ApplyConnection(profile, await File.ReadAllTextAsync(topicsPath, cancellationToken));
            else
            {
                profile.TopicSettingsJson = DdsTopicSettings.Migrate(await File.ReadAllTextAsync(legacyTopicsPath, cancellationToken));
            }
            profile.QosProfilesXml = await File.ReadAllTextAsync(qosPath, cancellationToken);
        }
    }

    private async Task WriteProfileFilesAsync(DdsXmlProfile profile, CancellationToken cancellationToken)
    {
        var revision = Guid.NewGuid().ToString("N");
        var versionDirectory = ProfileDirectory(profile.Id, revision);
        // 기존 목록과 백업이 참조하는 파일은 절대 덮어쓰지 않는다. 세 파일이 완성된 버전만 새 목록에 게시한다.
        var staging = versionDirectory + ".pending";
        Directory.CreateDirectory(staging);
        await WriteTextAtomicAsync(Path.Combine(staging, DdsProfileFiles.DdsSimFileName), profile.DdsSimXml, cancellationToken);
        await WriteTextAtomicAsync(Path.Combine(staging, DdsProfileFiles.ConnectionFileName), DdsProfileFiles.ExportConnection(profile), cancellationToken);
        await WriteTextAtomicAsync(Path.Combine(staging, DdsProfileFiles.QosProfilesFileName), profile.QosProfilesXml, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(staging, versionDirectory);
        profile.StorageRevision = revision;
    }

    private bool ProfileFilesExist(DdsXmlProfile profile)
    {
        var directory = ProfileDirectory(profile.Id, profile.StorageRevision);
        return File.Exists(Path.Combine(directory, DdsProfileFiles.DdsSimFileName))
               && (File.Exists(Path.Combine(directory, DdsProfileFiles.ConnectionFileName)) || File.Exists(Path.Combine(directory, "topics.xml")))
               && File.Exists(Path.Combine(directory, DdsProfileFiles.QosProfilesFileName));
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

    private string ProfileDirectory(string profileId, string? revision = null)
    {
        if (string.IsNullOrWhiteSpace(profileId) ||
            profileId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidOperationException($"DDS 프로필 ID가 파일 경로에 안전하지 않습니다: {profileId}");
        var directory = Path.Combine(_profilesRoot, profileId);
        if (revision is null) return directory;
        if (revision.Length != 32 || !revision.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("잘못된 DDS 프로필 저장 버전입니다.");
        return Path.Combine(directory, "versions", revision);
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
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() < 4)
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
        catalog.Version = 4;
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
        if (string.IsNullOrWhiteSpace(profile.TopicSettingsJson) && profile.LegacyTopicsXml is not null)
            profile.TopicSettingsJson = profile.LegacyTopicsXml;
        if (!string.IsNullOrWhiteSpace(profile.TopicSettingsJson))
            profile.TopicSettingsJson = DdsTopicSettings.Migrate(profile.TopicSettingsJson);
        profile.LegacyTopicsXml = null;
        if (profile.ModuleName is not null && BuiltMessageRegistry.Modules.Any(module => module.Module.Name == profile.ModuleName))
            profile.DdsSimXml = BuiltMessageRegistry.Get(profile.ModuleName).ContractXml;
        if (string.IsNullOrWhiteSpace(profile.DdsSimXml) && !string.IsNullOrWhiteSpace(profile.LegacyTypesXml))
            profile.DdsSimXml = profile.LegacyTypesXml;

        if ((!string.IsNullOrWhiteSpace(profile.TopicSettingsJson) && !string.IsNullOrWhiteSpace(profile.QosProfilesXml)) ||
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

        if (topics != null && string.IsNullOrWhiteSpace(profile.TopicSettingsJson))
        {
            profile.TopicSettingsJson = DdsTopicSettings.Migrate(new XElement("topics",
                topics.Elements().Where(element => element.Name.LocalName == "topic").Select(ToDdsClientTopic)).ToString());
        }
        if (library != null && string.IsNullOrWhiteSpace(profile.QosProfilesXml))
        {
            // 라이브러리 이름을 바꾸면 base_name 참조가 깨진다. 이전 파일의 원래 이름을 유지한다.
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
            if (proposedProfile == null)
            {
                throw new InvalidOperationException(
                    $"프로필 '{currentProfile.Name}'은(는) 활성 DDS 세션 {activeSessions.Count}개에서 사용 중이라 삭제할 수 없습니다. 설정 변경은 새 세션에 적용됩니다.");
            }
        }
    }

    private static bool ProfileContentEquals(DdsXmlProfile left, DdsXmlProfile right)
        => string.Equals(left.Name, right.Name, StringComparison.Ordinal)
           && ProfileSelectionEquals(left, right)
           && string.Equals(left.DdsSimXml, right.DdsSimXml, StringComparison.Ordinal)
           && string.Equals(left.TopicSettingsJson, right.TopicSettingsJson, StringComparison.Ordinal)
           && string.Equals(left.QosProfilesXml, right.QosProfilesXml, StringComparison.Ordinal);

    private static bool ProfileXmlContentEquals(DdsXmlProfile left, DdsXmlProfile right)
        => string.Equals(left.DdsSimXml, right.DdsSimXml, StringComparison.Ordinal)
           && ProfileSelectionEquals(left, right)
           && string.Equals(left.TopicSettingsJson, right.TopicSettingsJson, StringComparison.Ordinal)
           && string.Equals(left.QosProfilesXml, right.QosProfilesXml, StringComparison.Ordinal);

    // XML이 같아도 모듈 또는 Participant 선택이 다르면 별개의 망 설정이다.
    private static bool ProfileSelectionEquals(DdsXmlProfile left, DdsXmlProfile right)
        => left.ModuleName == right.ModuleName
           && left.QosLibraryName == right.QosLibraryName
           && left.ParticipantQosProfile == right.ParticipantQosProfile;

    private static DdsXmlProfile CloneProfile(DdsXmlProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        StorageRevision = profile.StorageRevision,
        ModuleName = profile.ModuleName, QosLibraryName = profile.QosLibraryName, ParticipantQosProfile = profile.ParticipantQosProfile,
        DdsSimXml = profile.DdsSimXml,
        TopicSettingsJson = profile.TopicSettingsJson,
        QosProfilesXml = profile.QosProfilesXml,
        UpdatedAtUtc = profile.UpdatedAtUtc,
    };

    private static DdsProfileCatalog Clone(DdsProfileCatalog catalog)
        => JsonSerializer.Deserialize<DdsProfileCatalog>(JsonSerializer.Serialize(catalog, JsonOptions), JsonOptions)
           ?? throw new InvalidOperationException("DDS 프로필 저장 데이터를 복제하지 못했습니다.");
}
