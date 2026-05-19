using System.Text.Json;
using System.Text.RegularExpressions;
using GrpcWorkbench.Models.Api;
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

        try
        {
            var json = ApplyTemplate(t.PayloadTemplate, t, incomingJson);
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
                {
                    // try/finally: Write 실패해도 stream leak 방지
                    var sid = await _streaming.OpenStreamAsync(payload, session);
                    try
                    {
                        await _streaming.WriteMessageAsync(sid, json);
                    }
                    finally
                    {
                        if (_streaming.IsStreamOpen(sid))
                            await _streaming.CloseStreamAsync(sid);
                    }
                    break;
                }
                case "BidirectionalStreaming":
                {
                    // 트리거 발사마다 ad-hoc 양방향 스트림 — 1건 쓰고 즉시 닫음.
                    // try/finally: Write 실패해도 stream leak 방지 (active call 누적 방지)
                    var sid = await _streaming.OpenStreamAsync(payload, session);
                    try
                    {
                        await _streaming.WriteMessageAsync(sid, json);
                        await Task.Delay(50); // 서버가 응답 처리할 짧은 틈
                    }
                    finally
                    {
                        if (_streaming.IsStreamOpen(sid))
                            await _streaming.CloseStreamAsync(sid);
                    }
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown RPC type: {rpcType}");
            }

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
