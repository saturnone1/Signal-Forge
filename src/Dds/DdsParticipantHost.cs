using ASAP.Models.Dds;
using DdsAmbassador.DDSClient;

namespace ASAP.Dds;

/// <summary>One session owns one DDSClient connection. No native DDS entities escape this adapter.</summary>
public sealed class DdsParticipantHost : IAsyncDisposable
{
    private readonly DdsClient _client;
    private readonly string _directory;
    private readonly IReadOnlyDictionary<string, BuiltTopic> _topics;
    private readonly IReadOnlyDictionary<string, Lazy<Action<object>>> _publishers;
    private readonly ILogger _logger;
    public BuiltMessageModule Module { get; }
    public DdsShutdownState ConnectionState => _client.ShutdownState;
    public Exception? ConnectionError => _client.ShutdownError;
    private int _disposed;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    internal DdsParticipantHost(DdsClient client, string directory, IReadOnlyDictionary<string, BuiltTopic> topics, BuiltMessageModule module, ILogger logger,
        int requiredSubscribers = 0, int discoveryTimeoutMs = 0)
    {
        _client = client;
        _directory = directory;
        _topics = topics;
        // 첫 사용 때 Writer를 바인딩하고 이후 캐시 조회·상태 확인의 중복 경로를 피한다.
        // 일시적인 생성 실패는 캐시하지 않으므로 다음 요청에서 다시 시도할 수 있다.
        _publishers = topics.ToDictionary(entry => entry.Key,
            entry => new Lazy<Action<object>>(() => entry.Value.BindPublisher(client), LazyThreadSafetyMode.PublicationOnly),
            StringComparer.Ordinal);
        Module = module;
        _logger = logger;
        // 이전 생성자 호출과 설정 파일의 호환 인수다. 발행을 구독자 수나 발견 완료에 종속시키지 않는다.
    }

    public event Action<DdsSubscriptionError>? SubscriptionError
    {
        add => _client.SubscriptionError += value;
        remove => _client.SubscriptionError -= value;
    }

    public DdsTypedSample CreateSample(string typeName)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return Module.CreateSample(typeName);
    }

    public void Publish(string topicName, string typeName, string qosProfile, DdsTypedSample sample)
        => Publish(topicName, typeName, qosProfile, sample, null);

    internal void Publish(string topicName, string typeName, string qosProfile, DdsTypedSample sample, Action? writeStarting)
    {
        _ = Resolve(topicName, typeName, qosProfile);
        var write = _publishers[topicName].Value;
        writeStarting?.Invoke();
        write(sample.Value);
    }

    public void PreparePublisher(string topicName, string typeName, string qosProfile)
    {
        // Writer만 미리 만든다. 구독자의 존재나 Discovery 완료를 기다리지 않는다.
        _ = Resolve(topicName, typeName, qosProfile);
        _ = _publishers[topicName].Value;
    }

    public void WaitForSubscribers(string topicName, string typeName, string qosProfile, int count, TimeSpan timeout)
    {
        Resolve(topicName, typeName, qosProfile).WaitForSubscribers(_client, count, timeout);
    }

    public void WaitForAcknowledgments(string topicName, string typeName, string qosProfile, TimeSpan timeout) =>
        Resolve(topicName, typeName, qosProfile).WaitForAcknowledgments(_client, timeout);

    public IDisposable Subscribe(string topicName, string typeName, string qosProfile, Action<DdsReceivedSample> handler,
        Action<DdsSubscriptionError>? onError = null)
    {
        var topic = Resolve(topicName, typeName, qosProfile);
        // JSON은 대여 콜백 안에서 완성한다. 원본 샘플은 외부로 넘기지 않고 소유된 문자열만 전달한다.
        return topic.SubscribeWithInfoBorrowed(_client, (value, info) => handler(new DdsReceivedSample(
            DdsJsonConverter.ToJson(value), info.ReceivedAtUtc, info.SourceTimestampNs)), onError);
    }

    private BuiltTopic Resolve(string topicName, string typeName, string qosProfile)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_topics.TryGetValue(topicName, out var topic) || topic.TypeName != typeName)
            throw new InvalidOperationException($"Topic/type is not registered in this session: {topicName} / {typeName}.");
        var library = topic.Definition.QualifiedQosProfile.Split("::")[0];
        var qualified = qosProfile.Contains("::", StringComparison.Ordinal) ? qosProfile : library + "::" + qosProfile;
        if (qualified != topic.Definition.QualifiedQosProfile)
            throw new InvalidOperationException($"Topic '{topicName}' uses the session snapshot QoS '{topic.Definition.QualifiedQosProfile}'. Create a new session to change it.");
        return topic;
    }

    public ValueTask DisposeAsync()
        => new(BeginShutdown().WaitAsync(TimeSpan.FromSeconds(10)));

    // Only the session owner awaits the full lifetime; public callers have a finite wait.
    internal Task BeginShutdown()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is not null) return _disposeTask;
            Volatile.Write(ref _disposed, 1);
            _disposeTask = Task.Run(CompleteDisposeAsync);
            return _disposeTask;
        }
    }

    private async Task CompleteDisposeAsync()
    {
        try
        {
            _client.Close();
            // The library's timeout limits caller waiting, not native resource lifetime.
            while (_client.ShutdownState == DdsShutdownState.Closing)
                await Task.Delay(25).ConfigureAwait(false);
            if (_client.ShutdownError is { } failure)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            // 정리 실패 시 네이티브 객체와 QoS 스냅샷을 함께 유지한다. 아직 쓰일 수 있는 파일을 지우지 않는다.
            if (_client.ShutdownState == DdsShutdownState.Closed)
            {
                try { Directory.Delete(_directory, recursive: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "DDS 세션 스냅샷 삭제 실패: {Directory}", _directory); }
            }
        }
    }
}

public sealed record DdsReceivedSample(string Json, DateTime ReceivedAtUtc, long? SourceTimestampNs);

public sealed class DdsParticipantHostFactory
{
    private readonly ILoggerFactory _loggerFactory;
    public DdsParticipantHostFactory(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    public DdsParticipantHost Create(DdsTransportSettings transport, string ddsSimXmlContent,
        string topicSettingsJsonContent, string qosProfilesXmlContent, string? moduleName = null, string? qosLibraryName = null)
    {
        // All contract checks precede connection creation (and temporary files).
        var module = BuiltMessageRegistry.Select(ddsSimXmlContent, moduleName);
        var topics = BuiltMessageRegistry.ValidateAndBind(ddsSimXmlContent, topicSettingsJsonContent, qosProfilesXmlContent, module.Module.Name, qosLibraryName);
        var effectiveQos = DdsSessionQos.Create(qosProfilesXmlContent, transport, qosLibraryName);
        var directory = Path.Combine(Path.GetTempPath(), "signal-forge-ddsclient", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        DdsClient? client = null;
        try
        {
            var qosPath = Path.Combine(directory, "qos_profiles.xml");
            File.WriteAllText(qosPath, effectiveQos.Xml);
            client = DdsClient.Connect(new DdsClientOptions
            {
                MessageModules = [module.Module],
                DomainId = transport.DomainId,
                ParticipantName = $"SignalForge-{Path.GetFileName(directory)}",
                ParticipantQosProfile = effectiveQos.ParticipantProfile,
                QosProfilesXmlPath = qosPath,
                Topics = topics.Values.Select(topic => topic.Definition).ToArray(),
            });
            var logger = _loggerFactory.CreateLogger<DdsParticipantHost>();
            client.SubscriptionError += error => logger.LogError(error.Exception,
                "DDSClient subscription failure: {Topic}; fatal={Fatal}", error.TopicName, error.IsFatal);
            return new DdsParticipantHost(client, directory, topics, module, logger,
                transport.RequiredSubscribers, transport.DiscoveryTimeoutMs);
        }
        catch
        {
            client?.Close();
            // 연결 직후 어댑터 생성이 실패해도 연결 종료를 요청한다. 정리 중인 네이티브 자원의 파일은 유지한다.
            if (client is null || client.ShutdownState == DdsShutdownState.Closed)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch { /* 정리 로그 제공자도 실패할 수 있으므로 최초 연결 예외를 보존한다. */ }
            }
            throw;
        }
    }
}
