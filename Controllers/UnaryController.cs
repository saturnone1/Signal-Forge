using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrpcWorkbench.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UnaryController : ControllerBase
{
    private readonly IUnaryGrpcService _unaryGrpcService;
    private readonly ISessionService _sessionService;
    private readonly ILogger<UnaryController> _logger;

    public UnaryController(
        IUnaryGrpcService unaryGrpcService,
        ISessionService sessionService,
        ILogger<UnaryController> logger)
    {
        _unaryGrpcService = unaryGrpcService;
        _sessionService = sessionService;
        _logger = logger;
    }

    [HttpPost("call")]
    public async Task<IActionResult> CallUnary([FromBody] GrpcRequestPayload payload)
    {
        try
        {
            var session = _sessionService.GetSession(payload.SessionId);
            if (session == null)
                return NotFound(new { error = "Session not found" });

            if (session.ProtoContent == null || session.ProtoContent.Length == 0)
                return BadRequest(new { error = "Proto file not uploaded" });

            var response = await _unaryGrpcService.ExecuteUnaryCallAsync(payload, session);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unary call failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
