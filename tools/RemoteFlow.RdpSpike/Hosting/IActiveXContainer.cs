namespace RemoteFlow.RdpSpike.Hosting;

/// <summary>The two candidate ActiveX containers behind one surface, so the spike can switch between them
/// at launch and the rest of the app cannot tell which one it is talking to.</summary>
internal interface IActiveXContainer : IDisposable
{
    /// <summary>What to call this container in the log and the ADR.</summary>
    string Name { get; }

    /// <summary>The HWND handed to Avalonia's NativeControlHost. Not the control's own window: the
    /// container owns an outer window so reparenting never touches the control.</summary>
    IntPtr Handle { get; }

    /// <summary>The activated ActiveX object, or null before <see cref="Create"/>.</summary>
    object? Control { get; }

    /// <summary>Creates the container window and activates the control in place inside it.</summary>
    void Create(Guid classId, int width, int height);

    /// <summary>Resizes the container and tells the control about its new client rectangle.</summary>
    void SetSize(int width, int height);

    /// <summary>Gives the control keyboard focus, and reports whether the focus landed inside it.</summary>
    bool FocusControl();

    /// <summary>Whatever this container learned while activating, for the log and the evidence file.</summary>
    IReadOnlyList<string> Notes { get; }
}
