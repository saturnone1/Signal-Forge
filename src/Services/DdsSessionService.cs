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
    public required string TopicSettingsJsonContent { get; init; }
    public required string QosProfilesXmlContent { get; init; }
    public string? ModuleName { get; init; }
    public string? QosLibraryName { get; init; }
}

public sealed class DdsSessionService : IDdsSessionService, IAsyncDisposable
{
    public event Action<string>? SessionDeleting;
    private readonly DdsParticipantHostFactory _hostFactory;
    private readonly ILogger<DdsSessionService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionEntry> _entries = new();
    private TaskCompletionSource? _shutdown;
    private Task? _shutdownWait;
    private readonly TimeSpan _closeWaitTimeout;

    private sealed class SessionEntry(DdsSession session)
    {
        public string Id { get; } = session.SessionId;
        public DdsSession Session { get; } = session;
        public IAsyncDisposable? Host;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Closing;
        public Task? Waiting;
    }

    public DdsSessionService(DdsParticipantHostFactory hostFactory, ILogger<DdsSessionService> logger, TimeSpan? closeWaitTimeout = null)
    {
        _hostFactory = hostFactory;
        _logger = logger;
        _closeWaitTimeout = closeWaitTimeout ?? TimeSpan.FromSeconds(10);
        if (_closeWaitTimeout <= TimeSpan.Zero || _closeWaitTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(closeWaitTimeout));
    }

    public DdsSession Create(DdsSessionCreateRequest request)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_shutdown is not null, this);
        var module = BuiltMessageRegistry.Select(request.DdsSimXmlContent, request.ModuleName);
        var config = DdsConfigParser.Parse(request.TopicSettingsJsonContent, request.QosProfilesXmlContent, module, request.QosLibraryName);
        var transport = request.Transport.Snapshot();
        var session = new DdsSession
        {
            SessionId = System.Guid.NewGuid().ToString(),
            Name = request.Name,
            ModuleName = module.Module.Name,
            ProfileId = request.ProfileId,
            ProfileName = request.ProfileName,
            ProfileUpdatedAtUtc = request.ProfileUpdatedAtUtc,
            Transport = transport,
            DdsSimXmlContent = System.Text.Encoding.UTF8.GetBytes(request.DdsSimXmlContent),
            TopicSettingsJsonContent = System.Text.Encoding.UTF8.GetBytes(request.TopicSettingsJsonContent),
            QosProfilesXmlContent = System.Text.Encoding.UTF8.GetBytes(request.QosProfilesXmlContent),
            Topics = config.Topics.ToList(),
            Types = DdsTypeParser.Parse(request.DdsSimXmlContent),
            QosProfiles = config.QosProfileNames.ToList(),
            QosLibraryName = config.QosLibraryName,
        };
        return CreateOwnedSession(session, () => _hostFactory.Create(transport,
            request.DdsSimXmlContent, request.TopicSettingsJsonContent, request.QosProfilesXmlContent, module.Module.Name, config.QosLibraryName));
    }

    // Reservation precedes native creation, so shutdown also owns in-progress connections.
    internal DdsSession CreateOwnedSession(DdsSession session, Func<IAsyncDisposable> createHost)
    {
        var entry = new SessionEntry(session);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            session.State = DdsSessionState.Creating;
            _entries.Add(session.SessionId, entry);
        }
        try
        {
            var host = createHost(); // Never hold the registry lock across middleware calls.
            bool closing;
            lock (_gate)
            {
                entry.Host = host;
                closing = entry.Closing is not null;
                if (!closing) session.State = DdsSessionState.Active;
            }
            entry.Ready.TrySetResult();
            if (closing) throw new ObjectDisposedException(nameof(DdsSessionService), "Session closed during creation.");
            return session;
        }
        catch
        {
            lock (_gate)
            {
                if (entry.Closing is null)
                {
                    _entries.Remove(entry.Id);
                    session.State = DdsSessionState.Faulted;
                }
            }
            entry.Ready.TrySetResult();
            throw;
        }
    }

    public DdsSession? Get(string sessionId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var entry)) return null;
            RefreshConnectionState(entry);
            if (entry.Session.State != DdsSessionState.Active) return null;
            entry.Session.LastAccessedAt = DateTime.UtcNow;
            return entry.Session;
        }
    }

    public DdsParticipantHost? GetHost(string sessionId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var entry)) return null;
            RefreshConnectionState(entry);
            return entry.Session.State == DdsSessionState.Active ? entry.Host as DdsParticipantHost : null;
        }
    }

    private static void RefreshConnectionState(SessionEntry entry)
    {
        if (entry.Session.State == DdsSessionState.Active && entry.Host is DdsParticipantHost host &&
            host.ConnectionState == DdsAmbassador.DDSClient.DdsShutdownState.Faulted)
        {
            entry.Session.State = DdsSessionState.Faulted;
            entry.Session.CleanupError = host.ConnectionError?.Message ?? "DDS 연결이 오류 상태입니다.";
        }
    }

    public IReadOnlyList<DdsSession> GetByProfileId(string profileId) =>
        GetAll().Where(s => string.Equals(s.ProfileId, profileId, StringComparison.Ordinal)).OrderBy(s => s.CreatedAt).ToList();

    public IReadOnlyList<DdsSession> GetAll()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values) RefreshConnectionState(entry);
            return _entries.Values.Select(e => e.Session).ToList();
        }
    }

    public Task DeleteAsync(string sessionId)
    {
        SessionEntry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out entry!)) return Task.CompletedTask;
            if (entry.Closing is not null) return BoundedWait(entry);
            entry.Closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.Session.State = DdsSessionState.Closing;
        }
        _ = Task.Run(() => CloseEntryAsync(entry));
        lock (_gate) return BoundedWait(entry);
    }

    // Timeout only releases the caller; the entry continues to own native cleanup.
    private Task BoundedWait(SessionEntry entry)
    {
        if (entry.Waiting is null || entry.Waiting.IsCompleted)
            entry.Waiting = entry.Closing!.Task.WaitAsync(_closeWaitTimeout);
        return entry.Waiting;
    }

    private async Task CloseEntryAsync(SessionEntry entry)
    {
        Exception? failure = null;
        try
        {
            await entry.Ready.Task.ConfigureAwait(false);
            var observers = SessionDeleting;
            if (observers is not null)
                foreach (Action<string> observer in observers.GetInvocationList())
                    try { observer(entry.Id); }
                    catch (Exception ex) { _logger.LogWarning(ex, "DDS deletion observer failed: {Id}", entry.Id); }
            if (entry.Host is DdsParticipantHost nativeHost) await nativeHost.BeginShutdown().ConfigureAwait(false);
            else if (entry.Host is not null) await entry.Host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            _logger.LogWarning(ex, "DDS session cleanup failed: {Id}", entry.Id);
        }
        finally
        {
            lock (_gate)
            {
                if (failure is null) _entries.Remove(entry.Id);
                entry.Session.State = failure is null ? DdsSessionState.Closed : DdsSessionState.Faulted;
                entry.Session.CleanupError = failure?.Message;
            }
            if (failure is null) entry.Closing!.TrySetResult();
            else entry.Closing!.TrySetException(failure);
        }
    }

    public ValueTask DisposeAsync()
    {
        string[] ids;
        lock (_gate)
        {
            if (_shutdown is not null) return new(_shutdownWait!);
            _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownWait = _shutdown.Task.WaitAsync(_closeWaitTimeout);
            ids = _entries.Keys.ToArray();
        }
        _ = ShutdownAsync(ids);
        return new(_shutdownWait);
    }

    private async Task ShutdownAsync(string[] ids)
    {
        try
        {
            await Task.WhenAll(ids.Select(DeleteAsync)).ConfigureAwait(false);
            _shutdown!.TrySetResult();
        }
        catch (Exception ex) { _shutdown!.TrySetException(ex); }
    }
}
