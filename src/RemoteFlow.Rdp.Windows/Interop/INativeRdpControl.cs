namespace RemoteFlow.Rdp.Windows.Interop;

// All mstscax COM calls stay behind this seam. The session state machine can therefore be exercised with
// a managed fake and without creating a window or activating COM.
internal interface INativeRdpControl;
