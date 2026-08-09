namespace RemoteFlow.Rdp.Windows.Interop;

// All mstscax COM calls stay behind this seam. The session state machine can therefore be exercised with
// a managed fake and without creating a window or activating COM.
internal interface INativeRdpControl : IAsyncDisposable
{
    event EventHandler<NativeRdpEventArgs>? EventReceived;

    /// <summary>The one COM identity that the Avalonia host will site in #84.</summary>
    object NativeInstance { get; }

    void Connect(CancellationToken cancellationToken);

    void Disconnect(CancellationToken cancellationToken);

    void ConfigureCredentialPolicy(bool allowCredentialSaving, bool allowPromptingForCredentials);

    void SetClearTextPassword(ReadOnlySpan<char> password);

    void ResetPassword();

    string DescribeDisconnect(uint disconnectReason, uint extendedDisconnectReason);

    uint ExtendedDisconnectReason { get; }
}

internal sealed class NativeRdpEventArgs(int dispatchId, IReadOnlyList<object?> arguments) : EventArgs
{
    public int DispatchId { get; } = dispatchId;

    public IReadOnlyList<object?> Arguments { get; } = arguments;
}

internal interface INativeRdpControlFactory
{
    INativeRdpControl Create(RdpControlSettings settings, CancellationToken cancellationToken);
}
