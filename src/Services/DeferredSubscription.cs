namespace ASAP.Services;

/// <summary>Owns a handle even when a receive callback closes the registration before Subscribe returns.</summary>
internal sealed class DeferredSubscription : IDisposable
{
    private readonly object _gate = new();
    private IDisposable? _handle;
    private bool _closed;
    private bool _assigned;

    public void Set(IDisposable handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        bool dispose;
        lock (_gate)
        {
            if (_assigned) throw new InvalidOperationException("Subscription handle has already been assigned.");
            _assigned = true;
            dispose = _closed;
            if (!dispose) _handle = handle;
        }
        if (dispose) handle.Dispose();
    }

    public void Dispose()
    {
        IDisposable? handle;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            handle = _handle;
            _handle = null;
        }
        handle?.Dispose();
    }
}
