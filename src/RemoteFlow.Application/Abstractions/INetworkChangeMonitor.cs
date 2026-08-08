namespace RemoteFlow.Application.Abstractions;

public interface INetworkChangeMonitor
{
    event EventHandler? NetworkChanged;
}
