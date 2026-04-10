using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrpcWorkbench.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StreamingController : ControllerBase
{
    private readonly IGrpcStreamingService _streamingService;
    private readonly ISessionService _sessionService;
    private readonly ILogger<StreamingController> _logger;

    public StreamingController(
        IGrpcStreamingService streamingService,
        ISessionService sessionService,
        ILogger<StreamingController> logger)
    {
        _streamingService = streamingService;
        _sessionService = sessionService;
        _logger = logger;
    }

    /// <summary>
    /// Server Streaming RPC를 실행합니다. 서버로부터 수신된 모든 메시지를 모아 반환합니다.
    /// Client/Bidirectional Streaming은 SignalR Hub(GrpcWorkbenchHub)을 통해 처리됩니다.
    /// </summary>
    [HttpPost("server-streaming")]
    public async Task<IActionResult> ServerStreaming([FromBody] GrpcRequestPayload payload)
    {
        try
        {
            var session = _sessionService.GetSession(payload.SessionId);
            if (session == null)
                return NotFound(new { error = "Session not found" });

            if (session.ProtoContent == null || session.ProtoContent.Length == 0)
                return BadRequest(new { error = "Proto file not uploaded" });

            var messages = new List<string>();
            var tcs = new TaskCompletionSource<bool>();

            await _streamingService.ExecuteServerStreamingAsync(
                payload,
                session,
                msg => { messages.Add(msg); return Task.CompletedTask; },
                error =>
                {
                    if (error != null) tcs.SetException(error);
                    else tcs.SetResult(true);
                    return Task.CompletedTask;
                });

            await tcs.Task;

            return Ok(new { isSuccess = true, messages });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server streaming failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
