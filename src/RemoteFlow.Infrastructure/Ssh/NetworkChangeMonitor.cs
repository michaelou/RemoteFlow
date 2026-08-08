using System.Net.NetworkInformation;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Ssh;

public sealed class NetworkChangeMonitor : INetworkChangeMonitor, IDisposable
{
    private int _disposed;

    public NetworkChangeMonitor()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    public event EventHandler? NetworkChanged;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        NetworkChanged?.Invoke(this, EventArgs.Empty);
    }
}
