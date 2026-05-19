using Grpc.Net.Client;
using GrpcWorkbench.Models.Session;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace GrpcWorkbench.Grpc;

public interface IGrpcChannelProvider
{
    Task<GrpcChannel> GetChannelAsync(GrpcSession session);
    void ClearChannel(string sessionId);
}

public class GrpcChannelProvider : IGrpcChannelProvider
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();
    private readonly ILogger<GrpcChannelProvider> _logger;

    public GrpcChannelProvider(ILogger<GrpcChannelProvider> logger)
    {
        _logger = logger;
    }

    public Task<GrpcChannel> GetChannelAsync(GrpcSession session)
    {
        if (string.IsNullOrWhiteSpace(session.UnixSocketPath))
            throw new InvalidOperationException("UnixSocketPath is required.");

        return GetUnixDomainSocketChannelAsync(session);
    }

    private Task<GrpcChannel> GetUnixDomainSocketChannelAsync(GrpcSession session)
    {
        // UnixDomainSocketEndPoint은 순수 파일 경로만 받으므로 "unix:" 접두사를 제거한다.
        var socketPath = session.UnixSocketPath!.TrimStart();
        if (socketPath.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
            socketPath = socketPath["unix:".Length..].TrimStart('/');
        if (!socketPath.StartsWith('/'))
            socketPath = "/" + socketPath;

        var key = $"uds:{socketPath}";
        if (_channels.TryGetValue(key, out var existing))
            return Task.FromResult(existing);

        var udsEndPoint = new UnixDomainSocketEndPoint(socketPath);
        var connectionFactory = new UnixDomainSocketConnectionFactory(udsEndPoint);

        var socketsHttpHandler = new SocketsHttpHandler
        {
            ConnectCallback = connectionFactory.ConnectAsync,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5)
        };

        var channelOptions = new GrpcChannelOptions
        {
            HttpHandler = socketsHttpHandler,
            MaxReceiveMessageSize = 100 * 1024 * 1024,
            MaxSendMessageSize = 100 * 1024 * 1024,
            MaxRetryAttempts = 3,
            MaxRetryBufferSize = 16 * 1024 * 1024,
            MaxRetryBufferPerCallSize = 1024 * 1024
        };

        // UDS는 http://로 시작해야 함 (실제 연결은 ConnectCallback에서 처리)
        var newChannel = GrpcChannel.ForAddress("http://localhost", channelOptions);
        var channel = _channels.GetOrAdd(key, newChannel);

        // 경쟁으로 다른 채널이 먼저 삽입됐으면 방금 만든 것을 버린다.
        if (!ReferenceEquals(channel, newChannel))
            newChannel.Dispose();

        _logger.LogInformation("Created gRPC channel for UDS: {SocketPath}", socketPath);
        return Task.FromResult(channel);
    }

    public void ClearChannel(string sessionId)
    {
        // sessionId 기반으로 채널 키를 특정할 수 없으므로(키는 주소 기반),
        // 모든 채널을 정리한다. 필요하다면 세션별 키를 별도 관리할 수 있다.
        foreach (var key in _channels.Keys.ToArray())
        {
            if (_channels.TryRemove(key, out var ch))
                ch.Dispose();
        }
        _logger.LogInformation("Cleared all gRPC channels (triggered by session: {SessionId})", sessionId);
    }
}

/// <summary>
/// Unix Domain Socket을 위한 연결 팩토리
/// </summary>
internal class UnixDomainSocketConnectionFactory
{
    private readonly EndPoint _endPoint;

    public UnixDomainSocketConnectionFactory(EndPoint endPoint)
    {
        _endPoint = endPoint;
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(_endPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
