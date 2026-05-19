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

    public SessionService(ILogger<SessionService> logger)
    {
        _logger = logger;
    }

    public GrpcSession CreateSession(string unixSocketPath)
    {
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
