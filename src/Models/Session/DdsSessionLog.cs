using System.Collections;
using ASAP.Models.Dds;

namespace ASAP.Models.Session;

// Shared by browser circuits and DDS callbacks. Enumerators own immutable snapshots.
public sealed class DdsSessionLog : IEnumerable<DdsSessionLogEntry>
{
    private readonly object _gate = new();
    private readonly List<DdsSessionLogEntry> _entries = [];
    public int Count { get { lock (_gate) return _entries.Count; } }
    public void Add(DdsSessionLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > 500) _entries.RemoveRange(500, _entries.Count - 500);
        }
    }
    public void Clear() { lock (_gate) _entries.Clear(); }
    public IEnumerator<DdsSessionLogEntry> GetEnumerator()
    {
        lock (_gate) return ((IEnumerable<DdsSessionLogEntry>)_entries.ToArray()).GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
