namespace AgOpenNtripCaster.Server.Services.NTRIP;

/// <summary>Bounds all open sockets, including connections that have not authenticated.</summary>
public sealed class ConnectionAdmission(int maximum, int maximumPerAddress)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _addresses = new();
    private int _count;

    public IDisposable? TryAcquire(string address)
    {
        lock (_gate)
        {
            var count = _addresses.GetValueOrDefault(address);
            if (_count >= maximum || count >= maximumPerAddress)
                return null;
            _addresses[address] = count + 1;
            _count++;
            return new Lease(this, address);
        }
    }

    private void Release(string address)
    {
        lock (_gate)
        {
            if (--_addresses[address] == 0)
                _addresses.Remove(address);
            _count--;
        }
    }

    private sealed class Lease(ConnectionAdmission owner, string address) : IDisposable
    {
        private ConnectionAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(address);
    }
}
