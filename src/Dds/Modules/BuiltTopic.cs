using System.Collections.ObjectModel;
using System.Xml.Linq;
using DdsAmbassador.DDSClient;

namespace ASAP.Dds;

public abstract class BuiltTopic(TopicDefinition definition, string typeName)
{
    public TopicDefinition Definition { get; } = definition;
    public string TypeName { get; } = typeName;
    public abstract void CreatePublisher(DdsClient client);
    public virtual Action<object> BindPublisher(DdsClient client)
    {
        CreatePublisher(client);
        return value => Publish(client, value);
    }
    public abstract void Publish(DdsClient client, object value);
    public abstract IDisposable Subscribe(DdsClient client, Action<object> handler, Action<DdsSubscriptionError>? onError = null);
    public virtual IDisposable SubscribeBorrowed(DdsClient client, Action<object> handler, Action<DdsSubscriptionError>? onError = null)
        => Subscribe(client, handler, onError);
    public virtual IDisposable SubscribeWithInfoBorrowed(DdsClient client, Action<object, DdsSampleInfo> handler, Action<DdsSubscriptionError>? onError = null)
        => SubscribeBorrowed(client, sample => handler(sample, new(DateTime.UtcNow, null)), onError);
    public abstract void WaitForSubscribers(DdsClient client, int count, TimeSpan timeout);
    public abstract void WaitForAcknowledgments(DdsClient client, TimeSpan timeout);
}

public sealed class BuiltTopic<T>(TopicDefinition definition, string typeName) : BuiltTopic(definition, typeName)
{
    public override void CreatePublisher(DdsClient client) => client.CreatePublisher<T>(Definition.Name);
    public override Action<object> BindPublisher(DdsClient client)
    {
        var publisher = client.CreatePublisher<T>(Definition.Name);
        return value => publisher.Publish((T)value);
    }
    public override void Publish(DdsClient client, object value) => client.Publish((T)value, Definition.Name);
    public override IDisposable Subscribe(DdsClient client, Action<object> handler, Action<DdsSubscriptionError>? onError = null) =>
        client.Subscribe<T>(sample => handler(sample!), Definition.Name, onError ?? (_ => { }));
    public override IDisposable SubscribeBorrowed(DdsClient client, Action<object> handler, Action<DdsSubscriptionError>? onError = null) =>
        client.SubscribeBorrowed<T>(sample => handler(sample!), Definition.Name, onError ?? (_ => { }));
    public override IDisposable SubscribeWithInfoBorrowed(DdsClient client, Action<object, DdsSampleInfo> handler, Action<DdsSubscriptionError>? onError = null) =>
        client.SubscribeWithInfoBorrowed<T>((sample, info) => handler(sample!, info), Definition.Name, onError);
    public override void WaitForSubscribers(DdsClient client, int count, TimeSpan timeout) =>
        client.CreatePublisher<T>(Definition.Name).WaitForSubscribers(count, timeout);
    public override void WaitForAcknowledgments(DdsClient client, TimeSpan timeout) =>
        client.CreatePublisher<T>(Definition.Name).WaitForAcknowledgments(timeout);
}

// Preserves the existing UI's using/CreateSample contract without native sample allocation.
public sealed class DdsTypedSample(object value) : IDisposable
{
    public object Value { get; internal set; } = value;
    public void Dispose() { }
}
