using System.Collections.Concurrent;
using System.Text.Json;
using ASAP.Dds;
using ASAP.Models.Dds;
using Microsoft.Extensions.Logging.Abstractions;

internal static class MultiNetworkTests
{
    public static async Task Run(string qos, Action<bool, string> check)
    {
        var factory = new DdsParticipantHostFactory(NullLoggerFactory.Instance);
        const string aType = "MSG::TimeTickInformation";
        const string bType = "Telemetry::Heartbeat";
        DdsParticipantHost Open(string module, string type, int domain)
        {
            var topics = $"<topics><topic name=\"Shared\" type_name=\"{type}\" qos_profile=\"P\" direction=\"Both\"/></topics>";
            return factory.Create(new DdsTransportSettings { DomainId = domain, AllowInterfaces = ["127.0.0.1"] },
                BuiltMessageRegistry.Get(module).ContractXml, topics, qos, module);
        }
        await using var a = Open("DDSSim", aType, IntegrationDomains.Resolve(194));
        await using var b = Open("Telemetry", bType, IntegrationDomains.Resolve(186));
        var receivedA = Enumerable.Range(0, 3).Select(_ => new ConcurrentQueue<ulong>()).ToArray();
        var receivedB = Enumerable.Range(0, 3).Select(_ => new ConcurrentQueue<ulong>()).ToArray();
        IDisposable[] Subscribe(DdsParticipantHost host, string type, ConcurrentQueue<ulong>[] queues, bool header) =>
            queues.Select(queue => host.Subscribe("Shared", type, "P", sample =>
            {
                using var json = JsonDocument.Parse(sample.Json);
                queue.Enqueue(header ? json.RootElement.GetProperty("Header").GetProperty("TimeTick").GetUInt64()
                    : json.RootElement.GetProperty("Counter").GetUInt64());
            })).ToArray();
        var handlesA = Subscribe(a, aType, receivedA, true);
        var handlesB = Subscribe(b, bType, receivedB, false);
        void Burst(DdsParticipantHost host, string type, bool header, ulong start)
        {
            host.WaitForSubscribers("Shared", type, "P", 3, TimeSpan.FromSeconds(15));
            for (var value = start; value < start + 4; value++)
            {
                using var sample = host.CreateSample(type);
                DdsJsonConverter.ApplyJson(sample, header ? $"{{\"Header\":{{\"TimeTick\":{value}}}}}" : $"{{\"Counter\":{value}}}");
                host.Publish("Shared", type, "P", sample);
            }
            host.WaitForAcknowledgments("Shared", type, "P", TimeSpan.FromSeconds(10));
        }
        try
        {
            Burst(a, aType, true, 1);
            Burst(b, bType, false, 101);
            await Until(() => receivedA.All(q => q.Count >= 4) && receivedB.All(q => q.Count >= 4));
            check(receivedA.All(q => q.SequenceEqual(new ulong[] { 1, 2, 3, 4 })), "DDSSim Shared: three readers receive burst 4 without cross-network payloads");
            check(receivedB.All(q => q.SequenceEqual(new ulong[] { 101, 102, 103, 104 })), "Telemetry Shared: three readers receive burst 4 with independent built type");
            foreach (var handle in handlesA) handle.Dispose();
            await a.DisposeAsync();
            Burst(b, bType, false, 105);
            await Until(() => receivedB.All(q => q.Count >= 8));
            check(receivedB.All(q => q.SequenceEqual(Enumerable.Range(101, 8).Select(i => (ulong)i))), "Telemetry survives DDSSim shutdown: every reader receives 4+4");
            await using var reconnected = Open("DDSSim", aType, IntegrationDomains.Resolve(194));
            var restarted = Enumerable.Range(0, 3).Select(_ => new ConcurrentQueue<ulong>()).ToArray();
            var restartedHandles = Subscribe(reconnected, aType, restarted, true);
            try
            {
                Burst(reconnected, aType, true, 5);
                await Until(() => restarted.All(q => q.Count >= 4));
                check(restarted.All(q => q.SequenceEqual(new ulong[] { 5, 6, 7, 8 })), "DDSSim reconnect: three readers receive second burst of four");
            }
            finally { foreach (var handle in restartedHandles) handle.Dispose(); }
        }
        finally
        {
            foreach (var handle in handlesA.Concat(handlesB)) handle.Dispose();
        }
    }

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Multi-module burst delivery timed out.");
            await Task.Delay(20);
        }
    }
}
