namespace RemoteFlow.Rdp.Windows.Hosting;

internal interface IRdpControlContainer : IDisposable
{
    event EventHandler<RdpControlFocusChangedEventArgs>? FocusChanged;

    IntPtr Handle { get; }

    void Create(int width, int height);

    void SetSize(int width, int height);

    bool FocusControl();
}

internal sealed class RdpControlFocusChangedEventArgs(bool isFocused) : EventArgs
{
    public bool IsFocused { get; } = isFocused;
}
