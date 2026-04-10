using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Services;
using Microsoft.AspNetCore.SignalR;

namespace GrpcWorkbench.Hubs;

public class GrpcWorkbenchHub : Hub
{
    private readonly IUnaryGrpcService _unaryGrpcService;
    private readonly IGrpcStreamingService _streamingService;
    private readonly ISessionService _sessionService;
    private readonly ILogger<GrpcWorkbenchHub> _logger;

    public GrpcWorkbenchHub(
        IUnaryGrpcService unaryGrpcService,
        IGrpcStreamingService streamingService,
        ISessionService sessionService,
        ILogger<GrpcWorkbenchHub> logger)
    {
        _unaryGrpcService = unaryGrpcService;
        _streamingService = streamingService;
        _sessionService = sessionService;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task ExecuteUnaryCall(GrpcRequestPayload payload)
    {
        try
        {
            var session = _sessionService.GetSession(payload.SessionId);
            if (session == null)
            {
                await Clients.Caller.SendAsync("UnaryError", "Session not found");
                return;
            }

            var response = await _unaryGrpcService.ExecuteUnaryCallAsync(payload, session);
            await Clients.Caller.SendAsync("UnaryResponse", response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unary call failed");
            await Clients.Caller.SendAsync("UnaryError", ex.Message);
        }
    }

    public async Task StartServerStreaming(GrpcRequestPayload payload)
    {
        try
        {
            var session = _sessionService.GetSession(payload.SessionId);
            if (session == null)
            {
                await Clients.Caller.SendAsync("StreamingError", "Session not found");
                return;
            }

            var streamId = Guid.NewGuid().ToString();

            await _streamingService.ExecuteServerStreamingAsync(
                payload,
                session,
                async msg => await Clients.Caller.SendAsync("StreamingMessage", new { streamId, message = msg }),
                async error =>
                {
                    if (error != null)
                        await Clients.Caller.SendAsync("StreamingError", error.Message);
                    else
                        await Clients.Caller.SendAsync("StreamingCompleted", streamId);
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server streaming failed");
            await Clients.Caller.SendAsync("StreamingError", ex.Message);
        }
    }

    /// <summary>Client/Bidirectional 스트림을 열고 streamId를 반환합니다.</summary>
    public async Task OpenStream(GrpcRequestPayload payload)
    {
        try
        {
            var session = _sessionService.GetSession(payload.SessionId);
            if (session == null)
            {
                await Clients.Caller.SendAsync("StreamingError", "Session not found");
                return;
            }

            var streamId = await _streamingService.OpenStreamAsync(payload, session);
            await Clients.Caller.SendAsync("StreamOpened", streamId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open stream");
            await Clients.Caller.SendAsync("StreamingError", ex.Message);
        }
    }

    /// <summary>열린 스트림에 메시지 1건을 전송합니다.</summary>
    public async Task SendStreamMessage(string streamId, string messageJson)
    {
        try
        {
            await _streamingService.WriteMessageAsync(streamId, messageJson);
            await Clients.Caller.SendAsync("StreamMessageSent", streamId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write stream message");
            await Clients.Caller.SendAsync("StreamingError", ex.Message);
        }
    }

    /// <summary>스트림을 닫고 서버 응답을 반환합니다.</summary>
    public async Task CloseStream(string streamId)
    {
        try
        {
            var result = await _streamingService.CloseStreamAsync(streamId);

            if (result.IsSuccess)
            {
                await Clients.Caller.SendAsync("StreamClosed", new
                {
                    streamId,
                    response = result.Response,
                    messages = result.Messages
                });
            }
            else
            {
                await Clients.Caller.SendAsync("StreamingError", result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close stream");
            await Clients.Caller.SendAsync("StreamingError", ex.Message);
        }
    }
}
