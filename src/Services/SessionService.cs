using GrpcWorkbench.Models.Session;
using System.Collections.Concurrent;

namespace GrpcWorkbench.Services;

public interface ISessionService
{
    GrpcSession CreateSession(string unixSocketPath);
    GrpcSession? GetSession(string sessionId);
    Task UpdateSessionProtoAsync(string sessionId, byte[] protoContent, string fileName);
    void DeleteSession(string sessionId);
    List<GrpcSession> GetAllSessions();
}

public class SessionService : ISessionService
{
    private readonly ConcurrentDictionary<string, GrpcSession> _sessions = new();
    private readonly ILogger<SessionService> _logger;

    // 싱글톤 + 무한 누적 방지: 유휴 세션은 새 세션 생성 시 기회적으로 정리한다.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(1);
    private const int MaxSessions = 50;

    public SessionService(ILogger<SessionService> logger)
    {
        _logger = logger;
    }

    public GrpcSession CreateSession(string unixSocketPath)
    {
        PruneStaleSessions();

        var session = new GrpcSession
        {
            SessionId = Guid.NewGuid().ToString(),
            UnixSocketPath = unixSocketPath
        };

        _sessions[session.SessionId] = session;
        _logger.LogInformation("Created session {SessionId} for UDS: {UnixSocketPath}", session.SessionId, unixSocketPath);
        return session;
    }

    public GrpcSession? GetSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastAccessedAt = DateTime.UtcNow;
            return session;
        }

        return null;
    }

    public async Task UpdateSessionProtoAsync(string sessionId, byte[] protoContent, string fileName)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session {sessionId} not found");

        session.ProtoContent = protoContent;
        session.ProtoFileName = fileName;
        session.LastAccessedAt = DateTime.UtcNow;

        _logger.LogInformation("Updated proto for session {SessionId}: {FileName}", sessionId, fileName);
        await Task.CompletedTask;
    }

    // 유휴 시간 초과 세션 제거 + 상한 초과 시 가장 오래 접근 안 된 세션부터 제거.
    private void PruneStaleSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _sessions)
        {
            if (now - kvp.Value.LastAccessedAt > IdleTimeout &&
                _sessions.TryRemove(kvp.Key, out _))
                _logger.LogInformation("Evicted idle session {SessionId}", kvp.Key);
        }

        if (_sessions.Count < MaxSessions) return;

        foreach (var kvp in _sessions.OrderBy(s => s.Value.LastAccessedAt)
                                     .Take(_sessions.Count - MaxSessions + 1))
        {
            if (_sessions.TryRemove(kvp.Key, out _))
                _logger.LogInformation("Evicted session {SessionId} (max {Max} reached)",
                    kvp.Key, MaxSessions);
        }
    }

    public void DeleteSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _logger.LogInformation("Deleted session {SessionId}", sessionId);
    }

    public List<GrpcSession> GetAllSessions()
    {
        return [.. _sessions.Values];
    }
}
