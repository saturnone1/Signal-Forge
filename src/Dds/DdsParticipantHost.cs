using System.Collections.Concurrent;
using System.Collections.Immutable;
using ASAP.Models.Dds;
using Rti.Dds.Core;
using Rti.Dds.Core.Policy;
using Rti.Dds.Domain;
using Rti.Dds.Publication;
using Rti.Dds.Subscription;
using Rti.Dds.Topics;
using Rti.Types.Dynamic;

namespace ASAP.Dds;

/// <summary>
/// 한 DDS 세션에 대응하는 RTI 런타임. DomainParticipant 1개와 그 위에서 만들어진
/// DynamicData 기반 Topic/Reader/Writer를 관리한다.
///
/// Type 등록은 QosProvider에 사용자가 올린 DDSSim.xml을 넘겨서 처리한다.
/// (Wire-compat: 동일 type_name + 동일 topic_name이면 ambassador와 매칭됨.)
/// </summary>
public sealed class DdsParticipantHost : IAsyncDisposable
{
    private readonly DomainParticipant _participant;
    private readonly QosProvider _qosProvider;
    private readonly Publisher _publisher;
    private readonly Subscriber _subscriber;
    private readonly ILogger<DdsParticipantHost> _logger;
    private readonly string _temporaryDirectory;

    private readonly ConcurrentDictionary<string, Topic<DynamicData>> _topics = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DataReader<DynamicData>> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DataWriter<DynamicData>> _writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DynamicType> _typeCache = new(StringComparer.OrdinalIgnoreCase);

    private long _disposed;

    public DdsParticipantHost(
        DomainParticipant participant,
        QosProvider qosProvider,
        string temporaryDirectory,
        ILogger<DdsParticipantHost> logger)
    {
        _participant = participant;
        _qosProvider = qosProvider;
        _temporaryDirectory = temporaryDirectory;
        _publisher = participant.ImplicitPublisher;
        _subscriber = participant.ImplicitSubscriber;
        _logger = logger;
    }

    public DomainParticipant Participant => _participant;
    public QosProvider QosProvider => _qosProvider;

    /// <summary>
    /// 토픽에 대한 typed Topic<DynamicData>를 가져오거나 새로 만든다.
    /// </summary>
    public Topic<DynamicData> GetOrCreateTopic(string topicName, string typeName)
    {
        return _topics.GetOrAdd(topicName, _ =>
        {
            var dynType = _typeCache.GetOrAdd(typeName, n =>
            {
                var t = _qosProvider.GetType(n)
                    ?? throw new InvalidOperationException($"DDS 타입을 찾을 수 없음: {n}");
                return t;
            });
            return _participant.CreateTopic(topicName, dynType);
        });
    }

    /// <summary>
    /// DataReader 생성. 동일 토픽에 이미 있으면 기존 것 반환.
    /// </summary>
    public DataReader<DynamicData> GetOrCreateReader(string topicName, string typeName, string qosProfileFullName)
    {
        return _readers.GetOrAdd(topicName, _ =>
        {
            var topic = GetOrCreateTopic(topicName, typeName);
            var readerQos = _qosProvider.GetDataReaderQos(qosProfileFullName);
            return _subscriber.CreateDataReader(topic, readerQos);
        });
    }

    /// <summary>
    /// DataWriter 생성. 동일 토픽에 이미 있으면 기존 것 반환.
    /// </summary>
    public DataWriter<DynamicData> GetOrCreateWriter(string topicName, string typeName, string qosProfileFullName)
    {
        return _writers.GetOrAdd(topicName, _ =>
        {
            var topic = GetOrCreateTopic(topicName, typeName);
            var writerQos = _qosProvider.GetDataWriterQos(qosProfileFullName);
            return _publisher.CreateDataWriter(topic, writerQos);
        });
    }

    public DynamicData CreateSample(string typeName)
    {
        var dynType = _typeCache.GetOrAdd(typeName, n =>
            _qosProvider.GetType(n)
                ?? throw new InvalidOperationException($"DDS 타입을 찾을 수 없음: {n}"));
        return new DynamicData(dynType);
    }

    public void ValidateQosProfile(string fullProfileName)
    {
        _ = _qosProvider.GetDataReaderQos(fullProfileName);
        _ = _qosProvider.GetDataWriterQos(fullProfileName);
    }

    public void RemoveReader(string topicName)
    {
        if (_readers.TryRemove(topicName, out var r))
        {
            try { r.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Reader Dispose 실패: {Topic}", topicName); }
        }
    }

    public void RemoveWriter(string topicName)
    {
        if (_writers.TryRemove(topicName, out var w))
        {
            try { w.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Writer Dispose 실패: {Topic}", topicName); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        foreach (var r in _readers.Values) try { r.Dispose(); } catch { /* swallow */ }
        foreach (var w in _writers.Values) try { w.Dispose(); } catch { /* swallow */ }
        foreach (var t in _topics.Values) try { t.Dispose(); } catch { /* swallow */ }
        _readers.Clear();
        _writers.Clear();
        _topics.Clear();
        _typeCache.Clear();

        try { _qosProvider.Dispose(); } catch { /* swallow */ }
        try { _participant.Dispose(); } catch { /* swallow */ }
        try
        {
            if (Directory.Exists(_temporaryDirectory))
                Directory.Delete(_temporaryDirectory, recursive: true);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "DDS 임시 디렉터리 삭제 실패: {Path}", _temporaryDirectory); }

        await Task.CompletedTask;
    }
}

/// <summary>
/// DdsParticipantHost 생성 팩토리. 세션 단위로 호출된다.
/// QosProvider 입력 XML을 임시 파일에 쓰고, DomainParticipant를 생성한 뒤 반환.
/// </summary>
public sealed class DdsParticipantHostFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public DdsParticipantHostFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public DdsParticipantHost Create(
        DdsTransportSettings transport,
        string ddsSimXmlContent,
        string topicsXmlContent,
        string qosProfilesXmlContent)
    {
        // 세션도 DDSClient와 동일한 세 파일 스냅샷을 사용한다.
        // RTI QosProvider에는 이 중 DDSSim.xml과 qos_profiles.xml을 전달한다.
        var sessionTempDir = Path.Combine(Path.GetTempPath(), "asap-dds", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionTempDir);
        try
        {
            var ddsSimPath = Path.Combine(sessionTempDir, "DDSSim.xml");
            var topicsPath = Path.Combine(sessionTempDir, "topics.xml");
            var qosPath = Path.Combine(sessionTempDir, "qos_profiles.xml");
            File.WriteAllText(ddsSimPath, ddsSimXmlContent);
            File.WriteAllText(topicsPath, topicsXmlContent);
            File.WriteAllText(qosPath, qosProfilesXmlContent);

            var profile = Profile.Default.With(builder =>
            {
                builder.UrlProfile.Clear();
                builder.UrlProfile.Add(ddsSimPath);
                builder.UrlProfile.Add(qosPath);
            });
            var qosProvider = new QosProvider(profile);

            var participantQos = ApplyTransport(qosProvider.GetDomainParticipantQos(), transport);
            var participant = DomainParticipantFactory.Instance.CreateParticipant(
                transport.DomainId, participantQos);

            var hostLogger = _loggerFactory.CreateLogger<DdsParticipantHost>();
            return new DdsParticipantHost(participant, qosProvider, sessionTempDir, hostLogger);
        }
        catch
        {
            try { Directory.Delete(sessionTempDir, recursive: true); } catch { }
            throw;
        }
    }

    private static DomainParticipantQos ApplyTransport(
        DomainParticipantQos baseQos,
        DdsTransportSettings transport)
    {
        var props = new Dictionary<string, string>();

        if (transport.AllowInterfaces.Count > 0)
            props["dds.transport.UDPv4.builtin.parent.allow_interfaces"] =
                string.Join(",", transport.AllowInterfaces);
        if (transport.DenyInterfaces.Count > 0)
            props["dds.transport.UDPv4.builtin.parent.deny_interfaces"] =
                string.Join(",", transport.DenyInterfaces);
        if (transport.SendBufferSize is int sb)
            props["dds.transport.UDPv4.builtin.send_socket_buffer_size"] = sb.ToString();
        if (transport.ReceiveBufferSize is int rb)
            props["dds.transport.UDPv4.builtin.recv_socket_buffer_size"] = rb.ToString();

        var qos = baseQos;
        if (props.Count > 0)
            qos = qos.WithProperty(Property.FromDictionary(props));

        qos = qos.WithDiscovery(d =>
        {
            d.InitialPeers.Add("udpv4://127.0.0.1");
            d.InitialPeers.Add("udpv4://localhost");

            if (!string.IsNullOrWhiteSpace(transport.MulticastAddress))
                d.MulticastReceiveAddresses.Add(transport.MulticastAddress!);
        });

        return qos;
    }
}
