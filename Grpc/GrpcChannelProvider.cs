using Grpc.Net.Client;
using GrpcWorkbench.Models.Session;

namespace GrpcWorkbench.Grpc;

public interface IGrpcChannelProvider
{
    Task<GrpcChannel> GetChannelAsync(GrpcSession session);
    void ClearChannel(string sessionId);
}

public class GrpcChannelProvider : IGrpcChannelProvider
{
    private readonly Dictionary<string, GrpcChannel> _channels = new();
    private readonly ILogger<GrpcChannelProvider> _logger;

    public GrpcChannelProvider(ILogger<GrpcChannelProvider> logger)
    {
        _logger = logger;
    }

    public Task<GrpcChannel> GetChannelAsync(GrpcSession session)
    {
        var scheme = session.UseTls ? "https" : "http";
        var key = $"{scheme}://{session.Address}:{session.Port}";

        if (_channels.TryGetValue(key, out var channel) && channel != null)
        {
            return Task.FromResult(channel);
        }

        var options = new GrpcChannelOptions();

        if (!session.UseTls)
        {
            // Insecure 연결 시 HTTP/2 cleartext 허용
            options.HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true
            };
        }

        var newChannel = GrpcChannel.ForAddress(key, options);
        _channels[key] = newChannel;

        _logger.LogInformation("Created gRPC channel to {Key} (TLS: {UseTls})", key, session.UseTls);

        return Task.FromResult(newChannel);
    }

    public void ClearChannel(string sessionId)
    {
        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }
        _channels.Clear();
        _logger.LogInformation("Cleared gRPC channels");
    }
}
