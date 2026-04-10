using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrpcWorkbench.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StreamingController : ControllerBase
{
    private readonly IStreamingGrpcService _streamingGrpcService;
    private readonly ISessionService _sessionService;
    private readonly ILogger<StreamingController> _logger;

    public StreamingController(
        IStreamingGrpcService streamingGrpcService,
        ISessionService sessionService,
        ILogger<StreamingController> logger)
    {
        _streamingGrpcService = streamingGrpcService;
        _sessionService = sessionService;
        _logger = logger;
    }

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

            await _streamingGrpcService.ExecuteServerStreamingAsync(
                payload,
                session,
                async msg =>
                {
                    messages.Add(msg);
                    await Task.CompletedTask;
                },
                async error =>
                {
                    if (error != null)
                        tcs.SetException(error);
                    else
                        tcs.SetResult(true);
                    await Task.CompletedTask;
                });

            await tcs.Task;

            return Ok(new
            {
                isSuccess = true,
                messages = messages
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server streaming failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("client-streaming")]
    public async Task<IActionResult> ClientStreaming([FromBody] ClientStreamingRequest request)
    {
        try
        {
            var session = _sessionService.GetSession(request.Payload.SessionId);
            if (session == null)
                return NotFound(new { error = "Session not found" });

            if (session.ProtoContent == null || session.ProtoContent.Length == 0)
                return BadRequest(new { error = "Proto file not uploaded" });

            var response = string.Empty;
            var tcs = new TaskCompletionSource<bool>();

            await _streamingGrpcService.ExecuteClientStreamingAsync(
                request.Payload,
                session,
                request.Messages,
                request.IntervalMs,
                async msg =>
                {
                    response = msg;
                    await Task.CompletedTask;
                },
                async error =>
                {
                    if (error != null)
                        tcs.SetException(error);
                    else
                        tcs.SetResult(true);
                    await Task.CompletedTask;
                });

            await tcs.Task;

            return Ok(new
            {
                isSuccess = true,
                response = response
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client streaming failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("bidirectional-streaming")]
    public async Task<IActionResult> BidirectionalStreaming([FromBody] ClientStreamingRequest request)
    {
        try
        {
            var session = _sessionService.GetSession(request.Payload.SessionId);
            if (session == null)
                return NotFound(new { error = "Session not found" });

            if (session.ProtoContent == null || session.ProtoContent.Length == 0)
                return BadRequest(new { error = "Proto file not uploaded" });

            var messages = new List<string>();
            var tcs = new TaskCompletionSource<bool>();

            await _streamingGrpcService.ExecuteBidirectionalStreamingAsync(
                request.Payload,
                session,
                request.Messages,
                request.IntervalMs,
                async msg =>
                {
                    messages.Add(msg);
                    await Task.CompletedTask;
                },
                async error =>
                {
                    if (error != null)
                        tcs.SetException(error);
                    else
                        tcs.SetResult(true);
                    await Task.CompletedTask;
                });

            await tcs.Task;

            return Ok(new
            {
                isSuccess = true,
                messages = messages
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bidirectional streaming failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

public class ClientStreamingRequest
{
    public GrpcRequestPayload Payload { get; set; } = new();
    public List<string> Messages { get; set; } = [];
    public int IntervalMs { get; set; } = 500;
}
