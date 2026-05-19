namespace GrpcWorkbench.Models.Ui;

public sealed record FrameVm(int Index, string Data);

public sealed class IncomingCallVm(string callId, string service, string method, string type)
{
    public string CallId { get; } = callId;
    public string Service { get; } = service;
    public string Method { get; } = method;
    public string Type { get; } = type;
    public List<FrameVm> Frames { get; } = [];
    public string? Result { get; set; }
    public bool ShowAllFrames { get; set; }
    public bool Expanded { get; set; }
    public bool Pretty { get; set; } = true;
}

public enum LogLevel { Info, Success, Warn, Error, Send }

public sealed record LogEntry(DateTime Time, string Text, LogLevel Level);
