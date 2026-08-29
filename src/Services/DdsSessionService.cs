using System.Collections.Concurrent;
using ASAP.Dds;
using ASAP.Models.Dds;
using ASAP.Models.Session;

namespace ASAP.Services;

public interface IDdsSessionService
{
    DdsSession Create(DdsSessionCreateRequest request);
    DdsSession? Get(string sessionId);
    DdsParticipantHost? GetHost(string sessionId);
    IReadOnlyList<DdsSession> GetByProfileId(string profileId);
    Task DeleteAsync(string sessionId);
    IReadOnlyList<DdsSession> GetAll();
    event Action<string>? SessionDeleting;
}

public sealed class DdsSessionCreateRequest
{
    public required string Name { get; init; }
    public required string ProfileId { get; init; }
    public required string ProfileName { get; init; }
    public required DateTimeOffset ProfileUpdatedAtUtc { get; init; }
    public required DdsTransportSettings Transport { get; init; }
    public required string DdsSimXmlContent { get; init; }
    public required string TopicsXmlContent { get; init; }
    public required string QosProfilesXmlContent { get; init; }
}

public sealed class DdsSessionService : IDdsSessionService, IAsyncDisposable
{
    public event Action<string>? SessionDeleting;
    private readonly DdsParticipantHostFactory _hostFactory;
    private readonly ILogger<DdsSessionService> _logger;

    private readonly ConcurrentDictionary<string, DdsSession> _sessions = new();
    private readonly ConcurrentDictionary<string, DdsParticipantHost> _hosts = new();

    public DdsSessionService(
        DdsParticipantHostFactory hostFactory,
        ILogger<DdsSessionService> logger)
    {
        _hostFactory = hostFactory;
        _logger = logger;
    }

    public DdsSession Create(DdsSessionCreateRequest request)
    {
        var configParse = DdsConfigParser.Parse(request.TopicsXmlContent, request.QosProfilesXmlContent);
        var types = DdsTypeParser.Parse(request.DdsSimXmlContent);

        var host = _hostFactory.Create(request.Transport, request.DdsSimXmlContent, configParse.QosProfilesXml);
        try
        {
            if (string.IsNullOrWhiteSpace(configParse.QosLibraryName))
                throw new InvalidOperationException("QoS 라이브러리 이름이 없습니다.");
            foreach (var profileName in configParse.QosProfileNames.Distinct(StringComparer.OrdinalIgnoreCase))
                host.ValidateQosProfile($"{configParse.QosLibraryName}::{profileName}");
        }
        catch
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        var session = new DdsSession
        {
            SessionId = System.Guid.NewGuid().ToString(),
            Name = request.Name,
            ProfileId = request.ProfileId,
            ProfileName = request.ProfileName,
            ProfileUpdatedAtUtc = request.ProfileUpdatedAtUtc,
            Transport = request.Transport,
            DdsSimXmlContent = System.Text.Encoding.UTF8.GetBytes(request.DdsSimXmlContent),
            TopicsXmlContent = System.Text.Encoding.UTF8.GetBytes(request.TopicsXmlContent),
            QosProfilesXmlContent = System.Text.Encoding.UTF8.GetBytes(request.QosProfilesXmlContent),
            Topics = configParse.Topics.ToList(),
            Types = types,
            QosProfiles = configParse.QosProfileNames.ToList(),
            QosLibraryName = configParse.QosLibraryName,
        };

        _sessions[session.SessionId] = session;
        _hosts[session.SessionId] = host;

        _logger.LogInformation(
            "DDS 세션 생성: {Name} ({Id}) — domain={Domain}, topics={Topics}, types={Types}",
            session.Name, session.SessionId, request.Transport.DomainId,
            session.Topics.Count, session.Types.Count);
        return session;
    }

    public DdsSession? Get(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var s))
        {
            s.LastAccessedAt = DateTime.UtcNow;
            return s;
        }
        return null;
    }

    public DdsParticipantHost? GetHost(string sessionId)
        => _hosts.TryGetValue(sessionId, out var h) ? h : null;

    public IReadOnlyList<DdsSession> GetByProfileId(string profileId)
        => _sessions.Values
            .Where(session => string.Equals(session.ProfileId, profileId, StringComparison.Ordinal))
            .OrderBy(session => session.CreatedAt)
            .ToList();

    public async Task DeleteAsync(string sessionId)
    {
        SessionDeleting?.Invoke(sessionId);
        if (_hosts.TryRemove(sessionId, out var host))
        {
            try { await host.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "DDS host dispose 실패: {Id}", sessionId); }
        }
        _sessions.TryRemove(sessionId, out _);
        _logger.LogInformation("DDS 세션 삭제: {Id}", sessionId);
    }

    public IReadOnlyList<DdsSession> GetAll() => _sessions.Values.ToList();

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
            await DeleteAsync(id);
    }
}
