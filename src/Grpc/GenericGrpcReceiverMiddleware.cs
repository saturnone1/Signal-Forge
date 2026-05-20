using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Services;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace GrpcWorkbench.Grpc;

/// <summary>
/// Intercepts all incoming gRPC requests on the UDS endpoint.
/// For each frame: decodes protobuf→JSON using known DdsSim types, then
/// broadcasts to the Blazor UI via WorkbenchNotificationService.
/// </summary>
public class GenericGrpcReceiverMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GenericGrpcReceiverMiddleware> _logger;
    private static readonly ConcurrentDictionary<Type, MessageParser?> RequestParserCache = new();
    private static readonly JsonFormatter JsonFormatter = JsonFormatter.Default;

    // gRPC reflection handshake paths — not visualized (skipped from interception so we
    // don't protobuf-decode reflection traffic). No reflection service is registered, so
    // these fall through routing and return UNIMPLEMENTED, which is expected/harmless.
    private static readonly HashSet<string> BuiltInServicePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "grpc.reflection.v1alpha.ServerReflection",
        "grpc.reflection.v1.ServerReflection",
    };

    public GenericGrpcReceiverMiddleware(RequestDelegate next, ILogger<GenericGrpcReceiverMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, WorkbenchNotificationService notify)
    {
        // Only intercept gRPC requests.
        var contentType = context.Request.ContentType ?? "";
        if (!contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Parse /{ServiceName}/{MethodName} from the request path.
        var path  = context.Request.Path.Value?.TrimStart('/') ?? "";
        var slash = path.LastIndexOf('/');
        if (slash <= 0) { await _next(context); return; }

        var serviceName = path[..slash];
        var methodName  = path[(slash + 1)..];

        // Don't intercept reflection handshake traffic — let it fall through routing.
        if (BuiltInServicePaths.Contains(serviceName)) { await _next(context); return; }

        // Disable Kestrel's MinRequestBodyDataRate — gRPC streams can be idle
        // for long periods without sending DATA frames (user hasn't clicked send yet).
        // Without this, Kestrel aborts the stream with HTTP/2 INTERNAL_ERROR ~5s after open.
        var minDataRate = context.Features.Get<IHttpMinRequestBodyDataRateFeature>();
        if (minDataRate != null) minDataRate.MinDataRate = null;
        var minResponseDataRate = context.Features.Get<IHttpMinResponseDataRateFeature>();
        if (minResponseDataRate != null) minResponseDataRate.MinDataRate = null;

        var callId    = Guid.NewGuid().ToString("N")[..8];
        var sw        = System.Diagnostics.Stopwatch.StartNew();
        context.Request.Headers.TryGetValue(GrpcRequestPayload.SessionIdMetadataKey, out var sessionIdValues);
        var sessionId = sessionIdValues.FirstOrDefault();
        // proto 메타데이터에서 정확한 RPC 타입 조회 (폴백: 이름 heuristic)
        var callType  = notify.GetRpcType(serviceName, methodName);
        _logger.LogInformation("[gRPC RX] {CallId} {ServiceName}/{MethodName} ({CallType})",
            callId, serviceName, methodName, callType);

        // ── Start HTTP/2 response immediately ────────────────────────────────
        // Required for bidirectional: response stream must be open while we
        // are still reading from the request stream.
        try
        {
            context.Response.DeclareTrailer("grpc-status");
            context.Response.DeclareTrailer("grpc-message");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[gRPC RX] {CallId} DeclareTrailer failed (non-fatal)", callId);
        }
        context.Response.ContentType = "application/grpc";
        context.Response.StatusCode  = 200;
        try
        {
            await context.Response.StartAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[gRPC RX] {CallId} StartAsync failed", callId);
            throw;
        }
        _logger.LogInformation("[gRPC RX] {CallId} response headers sent", callId);

        SemaphoreSlim? responseWriteLock = null;
        if (callType == "BidirectionalStreaming")
        {
            responseWriteLock = new SemaphoreSlim(1, 1);
            notify.RegisterInboundResponseStream(callId, serviceName, methodName, async payload =>
            {
                await responseWriteLock.WaitAsync();
                try
                {
                    var header = new byte[5];
                    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
                    await context.Response.Body.WriteAsync(header);
                    await context.Response.Body.WriteAsync(payload);
                    await context.Response.Body.FlushAsync();
                }
                finally
                {
                    responseWriteLock.Release();
                }
            });
        }

        // ── Broadcast call started ────────────────────────────────────────────
        notify.NotifyCallStarted(new IncomingCallStartedEvent(
            CallId: callId,
            SessionId: string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
            Service: serviceName,
            Method: methodName,
            Type: callType,
            Req: "스트림 수신 중..."));

        var frameCount = 0;
        var totalBytes = 0;
        var notifiedCount = 0;
        long lastNotifyMs = -1000;
        const int NotifyMinIntervalMs = 16; // ≈60 notify/s/call — UDS 왕복 지연 체감 최소화
        byte[]? pendingPayload = null;
        var pendingLen = 0;
        var pendingIndex = 0;

        // protobuf → JSON (실패 시 hex 폴백)
        string Decode(byte[] p, int len)
        {
            try
            {
                var t = notify.GetOrResolveRequestType(methodName, FindRequestMessageType);
                var parser = GetParser(t);
                if (parser != null)
                    return JsonFormatter.Format(parser.ParseFrom(p));
            }
            catch { /* fallthrough */ }
            return FallbackHex(p, len);
        }

        void Emit(byte[] p, int len, int idx)
        {
            notify.NotifyStreamMessage(new IncomingStreamMessageEvent(
                CallId: callId, FrameIndex: idx, Data: Decode(p, len)));
            notifiedCount++;
            lastNotifyMs = sw.ElapsedMilliseconds;
        }

        try
        {
            var body = context.Request.Body;
            while (true)
            {
                // 5-byte gRPC frame header: [compressed:1][length:4 big-endian]
                var header = new byte[5];
                if (await ReadExactAsync(body, header, 5) < 5) break;

                var length = (header[1] << 24) | (header[2] << 16) | (header[3] << 8) | header[4];
                if (length < 0 || length > 64 * 1024 * 1024) break; // 64 MB sanity cap

                var payload = new byte[length];
                await ReadExactAsync(body, payload, length);

                frameCount++;
                totalBytes += length;

                // 시간 스로틀: 간격 충족 시에만 디코드+통지, 아니면 최신 프레임만 보관
                if (sw.ElapsedMilliseconds - lastNotifyMs >= NotifyMinIntervalMs)
                {
                    Emit(payload, length, frameCount);
                    pendingPayload = null;
                }
                else
                {
                    pendingPayload = payload;
                    pendingLen = length;
                    pendingIndex = frameCount;
                }
            }
        }
        catch { /* ignore stream read/write errors */ }

        // 스로틀로 누락된 마지막 프레임은 항상 한 번 표시 → 최신 상태 보장
        if (pendingPayload != null) Emit(pendingPayload, pendingLen, pendingIndex);

        // Unary / ClientStreaming: caller expects exactly 1 response message.
        // Send a 5-byte gRPC frame with empty body (valid for any proto3 message
        // since all fields are optional by default).
        if (callType is "Unary" or "ClientStreaming")
        {
            try
            {
                await context.Response.Body.WriteAsync(new byte[5]); // compressed=0, length=0
                await context.Response.Body.FlushAsync();
            }
            catch { /* response may already be gone */ }
        }

        // ── Append gRPC OK trailers ───────────────────────────────────────────
        var trailersFeature = context.Features.Get<IHttpResponseTrailersFeature>();
        if (trailersFeature?.Trailers is { IsReadOnly: false } trailers)
        {
            trailers["grpc-status"]  = "0";
            trailers["grpc-message"] = "";
        }

        sw.Stop();

        // ── Broadcast call ended ──────────────────────────────────────────────
        var sampledNote = frameCount > notifiedCount ? $", {frameCount - notifiedCount} sampled-out" : "";
        notify.UnregisterInboundResponseStream(callId);
        responseWriteLock?.Dispose();
        notify.NotifyCallEnded(new IncomingCallEndedEvent(
            CallId: callId,
            Res: $"OK ({frameCount} frame(s){sampledNote}, {totalBytes} bytes, {sw.ElapsedMilliseconds} ms)"));
    }

    private static MessageParser? GetParser(Type? messageType)
    {
        if (messageType == null) return null;
        return RequestParserCache.GetOrAdd(
            messageType,
            static t => t.GetProperty("Parser")?.GetValue(null) as MessageParser);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, count - total));
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// methodName에서 protobuf 요청 메시지 타입을 검색합니다.
    /// 모든 로드된 어셈블리에서 다음 순서로 시도합니다:
    ///   1. Stream 접두사 제거 (StreamFoo → Foo)
    ///   2. 정확한 methodName
    ///   3. methodName + "Request"
    ///   4. methodName + "Message"
    /// </summary>
    private static Type? FindRequestMessageType(string methodName)
    {
        var candidates = new List<string>();

        if (methodName.StartsWith("Stream", StringComparison.OrdinalIgnoreCase) && methodName.Length > 6)
            candidates.Add(methodName[6..]);

        candidates.Add(methodName);
        candidates.Add(methodName + "Request");
        candidates.Add(methodName + "Message");

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        foreach (var name in candidates)
        {
            foreach (var asm in assemblies)
            {
                Type? t;
                try
                {
                    t = asm.GetTypes().FirstOrDefault(
                        x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                          && typeof(IMessage).IsAssignableFrom(x));
                }
                catch (System.Reflection.ReflectionTypeLoadException)
                {
                    continue;
                }
                if (t != null) return t;
            }
        }

        return null;
    }

    private static string FallbackHex(byte[] payload, int length)
    {
        var hexPreview = length > 0
            ? Convert.ToHexString(payload.AsSpan(0, Math.Min(length, 48)))
            : "(empty)";
        return $"[{length} bytes] {hexPreview}{(length > 48 ? "…" : "")}";
    }
}

