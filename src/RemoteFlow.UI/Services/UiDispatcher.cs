using Avalonia.Threading;

namespace RemoteFlow.UI.Services;

public interface IUiDispatcher
{
    ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

public sealed class UiDispatcher : IUiDispatcher
{
    public async ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background, cancellationToken);
    }
}
