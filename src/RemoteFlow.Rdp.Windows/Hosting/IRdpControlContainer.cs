namespace RemoteFlow.Rdp.Windows.Hosting;

internal interface IRdpControlContainer : IDisposable
{
    IntPtr Handle { get; }

    void Create(int width, int height);

    void SetSize(int width, int height);

    bool FocusControl();
}
