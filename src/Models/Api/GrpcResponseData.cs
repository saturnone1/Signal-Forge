namespace ASAP.Models.Api;

public class GrpcResponseData
{
    public bool IsSuccess { get; set; }
    public string ResponseJson { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public long ElapsedMilliseconds { get; set; }
}
