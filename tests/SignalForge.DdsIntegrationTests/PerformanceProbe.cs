using System.Diagnostics;
using ASAP.Dds;
using ASAP.Models.Dds;

internal static class PerformanceProbe
{
    public static void Run()
    {
        var module = BuiltMessageRegistry.Get("DDSSim");
        var cases = module.Types.Keys.Select(name =>
        {
            var sample = module.CreateSample(name);
            return (sample, json: DdsJsonConverter.ToJson(sample));
        }).ToArray();
        void JsonLoop()
        {
            for (int n = 0; n < 200; n++)
                foreach (var item in cases) DdsJsonConverter.ApplyJson(item.sample, item.json);
        }
        Measure("JSON DDSSim default payloads: 3200 validations", JsonLoop);
        var subscriptions = Enumerable.Range(0, 200).Select(i => new DdsSubscriptionInfo
        {
            SubscriptionId = i.ToString(), SessionId = "bench", TopicName = "T", TypeName = "T", StartedAt = DateTime.UtcNow,
            IsActive = i % 2 == 0
        }).ToList();
        int sink = 0;
        Measure("UI original: 1000 frames x 20 reads", () =>
        {
            for (int frame = 0; frame < 1000; frame++)
                for (int read = 0; read < 20; read++) sink += subscriptions.Where(s => s.IsActive).ToList().Count;
        });
        Measure("UI snapshot: 1000 refreshes x 20 reads", () =>
        {
            for (int frame = 0; frame < 1000; frame++)
            {
                var active = subscriptions.Where(s => s.IsActive).ToArray();
                for (int read = 0; read < 20; read++) sink += active.Length;
            }
        });
        GC.KeepAlive(sink);
        foreach (var item in cases) item.sample.Dispose();
    }

    private static void Measure(string name, Action action)
    {
        action(); // warmup JIT and serializer metadata
        var times = new List<double>();
        var allocations = new List<long>();
        for (int i = 0; i < 7; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            action();
            timer.Stop();
            allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
            times.Add(timer.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"BENCH {name}: median_ms={times.Order().ElementAt(3):F3}; allocated_bytes={allocations.Order().ElementAt(3)}");
    }
}
