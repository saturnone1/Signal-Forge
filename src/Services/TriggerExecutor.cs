using System.Text.Json;
using System.Text.RegularExpressions;
using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Models.Session;
using GrpcWorkbench.Models.Triggers;

namespace GrpcWorkbench.Services;

/// <summary>
/// Trigger 등록 변경에 따라 Periodic 백그라운드 루프를 동기화하고,
/// OnIncoming은 WorkbenchNotificationService.StreamMessageReceived 구독으로
/// 매칭되는 수신 메시지마다 발사. Bulk는 UI/manual fire를 통해 호출.
/// 발사는 RPC 타입에 맞춰 UnaryGrpcService/GrpcStreamingService에 위임.
/// </summary>
public class TriggerExecutor : IHostedService, IDisposable
{
    private readonly WorkbenchStateService _state;
    private readonly WorkbenchNotificationService _notify;
    private readonly IUnaryGrpcService _unary;
    private readonly IGrpcStreamingService _streaming;
    private readonly ILogger<TriggerExecutor> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _periodicCts = new();
    private CancellationTokenSource? _hostCts;

    public TriggerExecutor(
        WorkbenchStateService state,
        WorkbenchNotificationService notify,
        IUnaryGrpcService unary,
        IGrpcStreamingService streaming,
        ILogger<TriggerExecutor> logger)
    {
        _state = state;
        _notify = notify;
        _unary = unary;
        _streaming = streaming;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _notify.StreamMessageReceived += OnIncomingMessage;
        _state.TriggersChanged += SyncPeriodic;
        SyncPeriodic();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _notify.StreamMessageReceived -= OnIncomingMessage;
        _state.TriggersChanged -= SyncPeriodic;
        lock (_lock)
        {
            foreach (var c in _periodicCts.Values) c.Cancel();
            _periodicCts.Clear();
        }
        _hostCts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _hostCts?.Dispose();

    // ── Periodic 루프 동기화 ─────────────────────────────────────────────
    private void SyncPeriodic()
    {
        var triggers = _state.SnapshotTriggers();
        lock (_lock)
        {
            // 비활성/삭제된 것 취소
            foreach (var id in _periodicCts.Keys.ToList())
            {
                var t = triggers.FirstOrDefault(x => x.Id == id);
                if (t == null || !t.Enabled || t.Type != TriggerType.Periodic)
                {
                    _periodicCts[id].Cancel();
                    _periodicCts.Remove(id);
                }
            }
            // 신규/활성화 시작
            foreach (var t in triggers)
            {
                if (t.Type != TriggerType.Periodic || !t.Enabled) continue;
                if (_periodicCts.ContainsKey(t.Id)) continue;
                if (_hostCts == null) continue;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_hostCts.Token);
                _periodicCts[t.Id] = cts;
                _ = Task.Run(() => PeriodicLoop(t, cts.Token), cts.Token);
            }
        }
    }

    private async Task PeriodicLoop(Trigger t, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Math.Max(10, t.IntervalMs), ct);
                if (ct.IsCancellationRequested) break;
                if (!t.Enabled) break;
                await FireOnce(t, incomingJson: null);
                if (t.MaxFires.HasValue && Interlocked.Read(ref t.TotalFires) >= t.MaxFires.Value)
                {
                    t.Enabled = false;
                    _state.NotifyTriggersChanged();
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Periodic loop crashed: {Id}", t.Id);
        }
    }

    // ── OnIncoming ───────────────────────────────────────────────────────
    private void OnIncomingMessage(IncomingStreamMessageEvent e)
    {
        // CallId로 어떤 RPC인지 알아내야 매칭 가능 — State의 callIndex 사용
        var call = _state.FindCallById(e.CallId);
        if (call == null) return;

        var triggers = _state.SnapshotTriggers();
        foreach (var t in triggers)
        {
            if (t.Type != TriggerType.OnIncoming || !t.Enabled) continue;
            if (!string.IsNullOrEmpty(t.MatchService) && t.MatchService != call.Service) continue;
            if (!string.IsNullOrEmpty(t.MatchMethod) && t.MatchMethod != call.Method) continue;
            if (!MatchesIncomingCondition(t, e.Data)) continue;
            _ = Task.Run(() => FireOnce(t, e.Data));
        }
    }

    // ── Bulk / Manual ────────────────────────────────────────────────────
    public async Task FireBulkAsync(Trigger t)
    {
        if (t.Type != TriggerType.Bulk) { await FireOnce(t, null); return; }
        if (t.BulkParallel)
            await Task.WhenAll(Enumerable.Range(0, t.BulkCount).Select(_ => FireOnce(t, null)));
        else
            for (int i = 0; i < t.BulkCount; i++) await FireOnce(t, null);
    }

    public Task FireManualAsync(Trigger t) => FireOnce(t, null);

    // ── 발사 본체 ───────────────────────────────────────────────────────
    private async Task FireOnce(Trigger t, string? incomingJson)
    {
        try
        {
            var json = ApplyTemplate(t.PayloadTemplate, t, incomingJson);

            if (!string.IsNullOrWhiteSpace(t.InboundTargetCallId))
            {
                await _notify.SendInboundResponseAsync(t.InboundTargetCallId, json);

                var inboundCall = _state.FindCallById(t.InboundTargetCallId);
                _state.AddOutbound(
                    inboundCall?.Service ?? t.TargetService,
                    inboundCall?.Method ?? t.TargetMethod,
                    json,
                    $"trigger:{(string.IsNullOrWhiteSpace(t.Name) ? t.Id : t.Name)}:stream",
                    _state.Session?.SessionId);

                Interlocked.Increment(ref t.TotalFires);
                t.LastFiredAt = DateTime.UtcNow;
                t.LastError = null;
                return;
            }

            var session = _state.Session;
            if (session == null)
            {
                t.LastError = "No active session";
                Interlocked.Increment(ref t.Errors);
                _state.NotifyTriggersChanged();
                return;
            }
            if (string.IsNullOrEmpty(t.TargetService) || string.IsNullOrEmpty(t.TargetMethod))
            {
                t.LastError = "Target Service/Method not set";
                Interlocked.Increment(ref t.Errors);
                _state.NotifyTriggersChanged();
                return;
            }

            var payload = new GrpcRequestPayload
            {
                SessionId = session.SessionId,
                ServiceName = t.TargetService,
                MethodName = t.TargetMethod,
                RequestJson = json,
                TimeoutSeconds = 10
            };
            // RPC 타입 조회 — 미상이면 Unary fallback
            var svc = session.Services?.FirstOrDefault(s => s.ServiceName == t.TargetService);
            var method = svc?.Methods.FirstOrDefault(m => m.MethodName == t.TargetMethod);
            var rpcType = method?.RpcType ?? "Unary";

            switch (rpcType)
            {
                case "Unary":
                    await _unary.ExecuteUnaryCallAsync(payload, session);
                    break;
                case "ServerStreaming":
                    await _streaming.ExecuteServerStreamingAsync(payload, session,
                        _ => Task.CompletedTask, _ => Task.CompletedTask);
                    break;
                case "ClientStreaming":
                    await FireStreamingTriggerAsync(t, session, payload, json, rpcType);
                    break;
                case "BidirectionalStreaming":
                    await FireStreamingTriggerAsync(t, session, payload, json, rpcType);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown RPC type: {rpcType}");
            }

            if (rpcType is not ("ClientStreaming" or "BidirectionalStreaming"))
                _state.AddOutbound(t.TargetService, t.TargetMethod, json, $"trigger:{(string.IsNullOrWhiteSpace(t.Name) ? t.Id : t.Name)}", session.SessionId);
            Interlocked.Increment(ref t.TotalFires);
            t.LastFiredAt = DateTime.UtcNow;
            t.LastError = null;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref t.Errors);
            // Reflection 래핑(TargetInvocationException) 다단계 풀어서 root 표시
            var root = ex;
            while (root is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                root = tie.InnerException;
            t.LastError = $"{root.GetType().Name}: {root.Message}";
            _logger.LogError(ex, "Trigger fire failed: {Id} {Name} target={Service}.{Method}",
                t.Id, t.Name, t.TargetService, t.TargetMethod);
        }
        finally
        {
            _state.NotifyTriggersChanged();
        }
    }

    private async Task FireStreamingTriggerAsync(Trigger t, GrpcSession session, GrpcRequestPayload payload, string json, string rpcType)
    {
        var triggerName = string.IsNullOrWhiteSpace(t.Name) ? t.Id : t.Name;
        var hasCompatibleOpenStream = HasCompatibleOpenLocalStream(session.SessionId, t.TargetService, t.TargetMethod, rpcType);

        switch (t.LocalStreamMode)
        {
            case TriggerLocalStreamMode.RequireCompatibleOpen:
                if (!hasCompatibleOpenStream)
                    throw new InvalidOperationException("No compatible open local stream");
                await WriteToCurrentLocalStreamAsync(t, json, $"trigger:{triggerName}:open-stream");
                return;

            case TriggerLocalStreamMode.Auto:
                if (hasCompatibleOpenStream)
                {
                    await WriteToCurrentLocalStreamAsync(t, json, $"trigger:{triggerName}:reuse-stream");
                    return;
                }
                break;

            case TriggerLocalStreamMode.AlwaysOpenNew:
            default:
                break;
        }

        await OpenWriteCloseStreamAsync(t, session, payload, json, rpcType, $"trigger:{triggerName}:ad-hoc-stream");
    }

    private bool HasCompatibleOpenLocalStream(string sessionId, string serviceName, string methodName, string rpcType)
        => _state.IsStreamOpen
           && !string.IsNullOrWhiteSpace(_state.StreamId)
           && string.Equals(_state.ActiveStreamSessionId, sessionId, StringComparison.Ordinal)
           && string.Equals(_state.ActiveStreamServiceName, serviceName, StringComparison.Ordinal)
           && string.Equals(_state.ActiveStreamMethodName, methodName, StringComparison.Ordinal)
           && string.Equals(_state.ActiveStreamRpcType, rpcType, StringComparison.Ordinal);

    private async Task WriteToCurrentLocalStreamAsync(Trigger t, string json, string source)
    {
        if (string.IsNullOrWhiteSpace(_state.StreamId))
            throw new InvalidOperationException("Open local stream id missing");

        await _streaming.WriteMessageAsync(_state.StreamId, json);
        _state.IncrementSent();
        _state.AddOutbound(
            _state.ActiveStreamServiceName ?? t.TargetService,
            _state.ActiveStreamMethodName ?? t.TargetMethod,
            json,
            source,
            _state.ActiveStreamSessionId);
    }

    private async Task OpenWriteCloseStreamAsync(Trigger t, GrpcSession session, GrpcRequestPayload payload, string json, string rpcType, string source)
    {
        var sid = await _streaming.OpenStreamAsync(payload, session);
        try
        {
            await _streaming.WriteMessageAsync(sid, json);
            if (rpcType == "BidirectionalStreaming")
                await Task.Delay(50);

            _state.AddOutbound(t.TargetService, t.TargetMethod, json, source, session.SessionId);
        }
        finally
        {
            if (_streaming.IsStreamOpen(sid))
                await _streaming.CloseStreamAsync(sid);
        }
    }

    // ── Templating ──────────────────────────────────────────────────────
    private static readonly Regex IncomingVarRx =
        new(@"\{\{incoming\.([\w\.]+)\}\}", RegexOptions.Compiled);

    private static string ApplyTemplate(string template, Trigger t, string? incomingJson)
    {
        var counter = Interlocked.Increment(ref t.Counter);
        var now = DateTime.UtcNow.ToString("O");
        var s = template
            .Replace("{{counter}}", counter.ToString())
            .Replace("{{now}}", now);
        if (!string.IsNullOrEmpty(incomingJson))
            s = ResolveIncomingVars(s, incomingJson);
        return s;
    }

    private static string ResolveIncomingVars(string template, string incomingJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(incomingJson);
            return IncomingVarRx.Replace(template, m =>
            {
                var path = m.Groups[1].Value;
                return GetByPath(doc.RootElement, path) ?? m.Value;
            });
        }
        catch { return template; }
    }

    private static bool MatchesIncomingCondition(Trigger t, string incomingJson)
    {
        if (string.IsNullOrWhiteSpace(t.MatchJsonPath) || string.IsNullOrWhiteSpace(t.MatchValue))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(incomingJson);
            var actual = GetByPath(doc.RootElement, t.MatchJsonPath.Trim());
            if (actual == null) return false;

            if (TryCompareAsNumbers(actual, t.MatchValue, out var actualNumber, out var expectedNumber))
            {
                return t.MatchOperator switch
                {
                    IncomingMatchOperator.GreaterThan => actualNumber > expectedNumber,
                    IncomingMatchOperator.GreaterThanOrEqual => actualNumber >= expectedNumber,
                    IncomingMatchOperator.LessThan => actualNumber < expectedNumber,
                    IncomingMatchOperator.LessThanOrEqual => actualNumber <= expectedNumber,
                    IncomingMatchOperator.NotEquals => actualNumber != expectedNumber,
                    _ => actualNumber == expectedNumber
                };
            }

            return t.MatchOperator switch
            {
                IncomingMatchOperator.NotEquals =>
                    !string.Equals(actual, t.MatchValue, StringComparison.OrdinalIgnoreCase),
                IncomingMatchOperator.Contains =>
                    actual.Contains(t.MatchValue, StringComparison.OrdinalIgnoreCase),
                IncomingMatchOperator.StartsWith =>
                    actual.StartsWith(t.MatchValue, StringComparison.OrdinalIgnoreCase),
                IncomingMatchOperator.EndsWith =>
                    actual.EndsWith(t.MatchValue, StringComparison.OrdinalIgnoreCase),
                IncomingMatchOperator.GreaterThan =>
                    string.Compare(actual, t.MatchValue, StringComparison.OrdinalIgnoreCase) > 0,
                IncomingMatchOperator.GreaterThanOrEqual =>
                    string.Compare(actual, t.MatchValue, StringComparison.OrdinalIgnoreCase) >= 0,
                IncomingMatchOperator.LessThan =>
                    string.Compare(actual, t.MatchValue, StringComparison.OrdinalIgnoreCase) < 0,
                IncomingMatchOperator.LessThanOrEqual =>
                    string.Compare(actual, t.MatchValue, StringComparison.OrdinalIgnoreCase) <= 0,
                _ =>
                    string.Equals(actual, t.MatchValue, StringComparison.OrdinalIgnoreCase)
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCompareAsNumbers(string actual, string expected, out decimal actualNumber, out decimal expectedNumber)
    {
        var style = System.Globalization.NumberStyles.Any;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var actualOk = decimal.TryParse(actual, style, culture, out actualNumber);
        var expectedOk = decimal.TryParse(expected, style, culture, out expectedNumber);
        return actualOk && expectedOk;
    }

    private static string? GetByPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(part, out var next)) return null;
            current = next;
        }
        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => current.GetRawText()
        };
    }
}
