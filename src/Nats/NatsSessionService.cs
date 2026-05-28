using System.Collections.Concurrent;
using System.Text;
using ASAP.Models.Nats;
using ASAP.Models.Session;
using NATS.Client.Core;

namespace ASAP.Nats;

public interface INatsSessionService
{
    event Action<string, NatsMessageEntry>? MessageReceived;

    Task<NatsSession> CreateAsync(string url, string? displayName = null, CancellationToken cancellationToken = default);
    NatsSession? Get(string sessionId);
    IReadOnlyList<NatsSession> GetAll();
    Task DeleteAsync(string sessionId);
    Task PublishTextAsync(string sessionId, string subject, string payload, CancellationToken cancellationToken = default);
    Task PublishBinaryAsync(string sessionId, string subject, byte[] payload, string? payloadText = null, CancellationToken cancellationToken = default);
    Task StartSubscriptionAsync(string sessionId, string subject, CancellationToken cancellationToken = default);
    Task StopSubscriptionAsync(string sessionId, string subject);
    IReadOnlyList<NatsSubscriptionInfo> SnapshotSubscriptions(string sessionId);
    IReadOnlyList<NatsMessageEntry> SnapshotInbound(string sessionId, int take = 100);
    IReadOnlyList<NatsMessageEntry> SnapshotOutbound(string sessionId, int take = 100);
}

public sealed class NatsSessionService : INatsSessionService
{
    private const int MaxMessagesPerDirection = 200;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ConcurrentDictionary<string, SessionRuntime> _sessions = new();
    private readonly ILogger<NatsSessionService> _logger;

    public event Action<string, NatsMessageEntry>? MessageReceived;

    public NatsSessionService(ILogger<NatsSessionService> logger)
    {
        _logger = logger;
    }

    public Task<NatsSession> CreateAsync(string url, string? displayName = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedUrl = string.IsNullOrWhiteSpace(url) ? "nats://localhost:4222" : url.Trim();
        var session = new NatsSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(displayName) ? $"NATS {DateTime.UtcNow:HHmmss}" : displayName.Trim(),
            Url = normalizedUrl,
        };

        var runtime = new SessionRuntime(session, new NatsConnection(new NatsOpts
        {
            Url = normalizedUrl,
        }));

        _sessions[session.SessionId] = runtime;
        _logger.LogInformation("Created NATS session {SessionId} for {Url}", session.SessionId, normalizedUrl);
        return Task.FromResult(session);
    }

    public NatsSession? Get(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var runtime))
            return null;

        Touch(runtime.Session);
        return runtime.Session;
    }

    public IReadOnlyList<NatsSession> GetAll()
        => _sessions.Values.Select(runtime => runtime.Session)
            .OrderBy(session => session.CreatedAt)
            .ToList();

    public async Task DeleteAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var runtime))
            return;

        await runtime.DisposeAsync();
        _logger.LogInformation("Deleted NATS session {SessionId}", sessionId);
    }

    public async Task PublishTextAsync(string sessionId, string subject, string payload, CancellationToken cancellationToken = default)
    {
        var normalizedPayload = payload ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(normalizedPayload);

        await PublishAsync(sessionId, subject, bytes, normalizedPayload, cancellationToken);
    }

    public Task PublishBinaryAsync(string sessionId, string subject, byte[] payload, string? payloadText = null, CancellationToken cancellationToken = default)
        => PublishAsync(sessionId, subject, payload ?? [], payloadText, cancellationToken);

    private async Task PublishAsync(string sessionId, string subject, byte[] payload, string? payloadText, CancellationToken cancellationToken)
    {
        var runtime = GetRuntime(sessionId);
        var normalizedSubject = NormalizeSubject(subject);

        await runtime.Connection.PublishAsync(normalizedSubject, payload, cancellationToken: cancellationToken);
        Touch(runtime.Session);
        Append(runtime.Outbound, new NatsMessageEntry
        {
            Subject = normalizedSubject,
            Direction = "Outbound",
            PayloadText = payloadText ?? DecodePayload(payload),
            PayloadBase64 = Convert.ToBase64String(payload),
            PayloadSize = payload.Length,
        });

        _logger.LogInformation("NATS outbound {SessionId} {Subject} ({Bytes} bytes)", sessionId, normalizedSubject, payload.Length);
    }

    public Task StartSubscriptionAsync(string sessionId, string subject, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var runtime = GetRuntime(sessionId);
        var normalizedSubject = NormalizeSubject(subject);

        lock (runtime.Gate)
        {
            if (runtime.Subscriptions.ContainsKey(normalizedSubject))
                return Task.CompletedTask;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(runtime.SessionLifetime.Token);
            var info = new NatsSubscriptionInfo
            {
                Subject = normalizedSubject,
                StartedAt = DateTime.UtcNow,
            };

            var pumpTask = Task.Run(() => PumpSubscriptionAsync(runtime, info, cts.Token), CancellationToken.None);
            runtime.Subscriptions[normalizedSubject] = new SubscriptionRuntime(info, cts, pumpTask);
            Touch(runtime.Session);
        }

        _logger.LogInformation("NATS subscription started {SessionId} {Subject}", sessionId, normalizedSubject);
        return Task.CompletedTask;
    }

    public async Task StopSubscriptionAsync(string sessionId, string subject)
    {
        var runtime = GetRuntime(sessionId);
        var normalizedSubject = NormalizeSubject(subject);
        SubscriptionRuntime? subscription;

        lock (runtime.Gate)
        {
            if (!runtime.Subscriptions.Remove(normalizedSubject, out subscription))
                return;
        }

        subscription.Cancellation.Cancel();
        try
        {
            await subscription.PumpTask;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            subscription.Cancellation.Dispose();
        }

        Touch(runtime.Session);
        _logger.LogInformation("NATS subscription stopped {SessionId} {Subject}", sessionId, normalizedSubject);
    }

    public IReadOnlyList<NatsSubscriptionInfo> SnapshotSubscriptions(string sessionId)
    {
        var runtime = GetRuntime(sessionId);
        lock (runtime.Gate)
        {
            return runtime.Subscriptions.Values
                .Select(item => new NatsSubscriptionInfo
                {
                    Subject = item.Info.Subject,
                    StartedAt = item.Info.StartedAt,
                })
                .OrderBy(item => item.Subject)
                .ToList();
        }
    }

    public IReadOnlyList<NatsMessageEntry> SnapshotInbound(string sessionId, int take = 100)
    {
        var runtime = GetRuntime(sessionId);
        lock (runtime.Gate)
        {
            return runtime.Inbound.TakeLast(Math.Max(1, take))
                .Select(CloneMessage)
                .ToList();
        }
    }

    public IReadOnlyList<NatsMessageEntry> SnapshotOutbound(string sessionId, int take = 100)
    {
        var runtime = GetRuntime(sessionId);
        lock (runtime.Gate)
        {
            return runtime.Outbound.TakeLast(Math.Max(1, take))
                .Select(CloneMessage)
                .ToList();
        }
    }

    private async Task PumpSubscriptionAsync(SessionRuntime runtime, NatsSubscriptionInfo info, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in runtime.Connection.SubscribeAsync<byte[]>(info.Subject, cancellationToken: cancellationToken))
            {
                var payload = message.Data ?? [];
                var entry = new NatsMessageEntry
                {
                    Subject = message.Subject,
                    Direction = "Inbound",
                    PayloadText = DecodePayload(payload),
                    PayloadBase64 = Convert.ToBase64String(payload),
                    PayloadSize = payload.Length,
                };

                lock (runtime.Gate)
                {
                    Append(runtime.Inbound, entry);
                }

                Touch(runtime.Session);
                MessageReceived?.Invoke(runtime.Session.SessionId, CloneMessage(entry));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NATS subscription pump failed for {SessionId} {Subject}", runtime.Session.SessionId, info.Subject);
        }
    }

    private static void Append(List<NatsMessageEntry> target, NatsMessageEntry entry)
    {
        target.Add(entry);
        if (target.Count > MaxMessagesPerDirection)
            target.RemoveRange(0, target.Count - MaxMessagesPerDirection);
    }

    private static NatsMessageEntry CloneMessage(NatsMessageEntry entry)
        => new()
        {
            MessageId = entry.MessageId,
            Subject = entry.Subject,
            Direction = entry.Direction,
            PayloadText = entry.PayloadText,
            PayloadBase64 = entry.PayloadBase64,
            PayloadSize = entry.PayloadSize,
            Timestamp = entry.Timestamp,
        };

    private static string DecodePayload(byte[] payload)
    {
        try
        {
            return StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return $"base64:{Convert.ToBase64String(payload)}";
        }
    }

    private static string NormalizeSubject(string subject)
    {
        var normalized = subject?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("subject는 비워 둘 수 없습니다.");

        return normalized;
    }

    private SessionRuntime GetRuntime(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var runtime))
            return runtime;

        throw new KeyNotFoundException($"NATS session {sessionId} not found");
    }

    private static void Touch(NatsSession session)
        => session.LastAccessedAt = DateTime.UtcNow;

    private sealed class SessionRuntime : IAsyncDisposable
    {
        public SessionRuntime(NatsSession session, NatsConnection connection)
        {
            Session = session;
            Connection = connection;
        }

        public NatsSession Session { get; }
        public NatsConnection Connection { get; }
        public object Gate { get; } = new();
        public List<NatsMessageEntry> Inbound { get; } = [];
        public List<NatsMessageEntry> Outbound { get; } = [];
        public Dictionary<string, SubscriptionRuntime> Subscriptions { get; } = new(StringComparer.Ordinal);
        public CancellationTokenSource SessionLifetime { get; } = new();

        public async ValueTask DisposeAsync()
        {
            List<SubscriptionRuntime> subscriptions;
            lock (Gate)
            {
                subscriptions = Subscriptions.Values.ToList();
                Subscriptions.Clear();
            }

            SessionLifetime.Cancel();

            foreach (var subscription in subscriptions)
            {
                subscription.Cancellation.Cancel();
            }

            foreach (var subscription in subscriptions)
            {
                try
                {
                    await subscription.PumpTask;
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    subscription.Cancellation.Dispose();
                }
            }

            SessionLifetime.Dispose();
            await Connection.DisposeAsync();
        }
    }

    private sealed record SubscriptionRuntime(NatsSubscriptionInfo Info, CancellationTokenSource Cancellation, Task PumpTask);
}