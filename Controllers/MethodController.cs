using GrpcWorkbench.Models.Session;
using GrpcWorkbench.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrpcWorkbench.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MethodController : ControllerBase
{
    private readonly ISessionService _sessionService;
    private readonly ILogger<MethodController> _logger;

    public MethodController(
        ISessionService sessionService,
        ILogger<MethodController> logger)
    {
        _sessionService = sessionService;
        _logger = logger;
    }

    [HttpGet("{sessionId}/services")]
    public IActionResult GetServices(string sessionId)
    {
        try
        {
            var session = _sessionService.GetSession(sessionId);
            if (session == null)
                return NotFound("Session not found");

            return Ok(session.Services);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get services");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{sessionId}/services/{serviceName}/methods")]
    public IActionResult GetMethods(string sessionId, string serviceName)
    {
        try
        {
            var session = _sessionService.GetSession(sessionId);
            if (session == null)
                return NotFound("Session not found");

            var service = session.Services.FirstOrDefault(s => s.ServiceName == serviceName);
            if (service == null)
                return NotFound("Service not found");

            return Ok(service.Methods);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get methods");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{sessionId}/services/{serviceName}/methods/{methodName}")]
    public IActionResult GetMethodDetail(string sessionId, string serviceName, string methodName)
    {
        try
        {
            var session = _sessionService.GetSession(sessionId);
            if (session == null)
                return NotFound("Session not found");

            var service = session.Services.FirstOrDefault(s => s.ServiceName == serviceName);
            if (service == null)
                return NotFound("Service not found");

            var method = service.Methods.FirstOrDefault(m => m.MethodName == methodName);
            if (method == null)
                return NotFound("Method not found");

            return Ok(method);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get method detail");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
